// Copyright 2026 UniversalHost contributors. MIT license.
// The worker is isolated from the UI process and uses no network or device I/O.
package main

import (
	"bytes"
	"crypto/sha256"
	"debug/elf"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"runtime/debug"
	"sort"
	"strings"
	"universalhost/symbols/internal/dwarf"
)

const maxFile = 256 << 20
const maxDies = 500000

type attribute struct {
	Text   string `json:",omitempty"`
	Number *int64 `json:",omitempty"`
	Ref    string `json:",omitempty"`
}
type node struct {
	ID                string
	Tag               uint32
	Version           int
	Attributes        map[uint32]attribute
	Children          []string
	Location          *uint64 `json:",omitempty"`
	HasLocation       bool    `json:",omitempty"`
	MemberOffset      *uint64 `json:",omitempty"`
	HasMemberLocation bool    `json:",omitempty"`
}
type symbol struct {
	Name          string
	Address, Size uint64
}
type result struct {
	Schema                                               int
	Backend, Format, Platform, ABI, Sha256               string
	AddressUnitBits, AddressBits                         int
	LittleEndian, Linked, SymbolsComplete, TypesComplete bool
	Symbols                                              []symbol
	ExcludedSymbolNames                                  []string
	Nodes                                                []*node
	Signatures                                           map[string]string
	Diagnostics                                          []string
	DwarfVersions                                        []int
}
type section struct {
	name string
	data []byte
}

func main() {
	debug.SetMemoryLimit(768 << 20)
	defer func() {
		if p := recover(); p != nil {
			fmt.Fprintln(os.Stderr, "parser panic:", p)
			os.Exit(2)
		}
	}()
	if len(os.Args) != 2 {
		fmt.Fprintln(os.Stderr, "usage: symbols-worker <firmware>")
		os.Exit(2)
	}
	r, err := parse(os.Args[1])
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if err = json.NewEncoder(os.Stdout).Encode(r); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func parse(path string) (*result, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	st, err := f.Stat()
	if err != nil {
		return nil, err
	}
	if st.Size() > maxFile {
		return nil, errors.New("file exceeds 256 MiB limit")
	}
	b, err := io.ReadAll(io.LimitReader(f, maxFile+1))
	if err != nil {
		return nil, err
	}
	if len(b) > maxFile {
		return nil, errors.New("file exceeds limit")
	}
	hash := sha256.Sum256(b)
	r := &result{Schema: 1, Backend: "Go 1.26.8 debug/dwarf + UniversalHost TI COFF adapter", Platform: "Unknown", ABI: "Unknown", Sha256: hex.EncodeToString(hash[:]), Signatures: map[string]string{}, Symbols: []symbol{}, Nodes: []*node{}, Diagnostics: []string{}}
	var sections []section
	if len(b) >= 20 && bytes.Equal(b[:4], []byte{0x7f, 'E', 'L', 'F'}) {
		sections, err = readELF(b, r)
	} else {
		sections, err = readCOFF(b, r)
	}
	if err != nil {
		return nil, err
	}
	if !r.Linked {
		r.Diagnostics = append(r.Diagnostics, "Relocatable/shared object: device addresses are not resolved")
	}
	if r.AddressUnitBits == 0 {
		r.Diagnostics = append(r.Diagnostics, "Unknown target architecture: no address-unit assumption")
	}
	if err = readDwarf(sections, r); err != nil {
		r.TypesComplete = false
		r.Nodes = nil
		r.Diagnostics = append(r.Diagnostics, "DWARF: "+err.Error())
	}
	retainModelDependencies(r)
	return r, nil
}

func readELF(b []byte, r *result) ([]section, error) {
	f, err := elf.NewFile(bytes.NewReader(b))
	if err != nil {
		return nil, err
	}
	defer f.Close()
	r.Format = "ELF"
	r.LittleEndian = f.ByteOrder == binary.LittleEndian
	r.AddressBits = 32
	if f.Class == elf.ELFCLASS64 {
		r.AddressBits = 64
	}
	r.Linked = f.Type == elf.ET_EXEC
	switch f.Machine {
	case 141:
		r.Platform = "TI C28x"
		r.ABI = "EABI"
		r.AddressUnitBits = 16
	case elf.EM_ARM:
		r.Platform = "ARM"
		r.ABI = "ARM ELF"
		r.AddressUnitBits = 8
	case elf.EM_AARCH64:
		r.Platform = "AArch64"
		r.ABI = "ELF"
		r.AddressUnitBits = 8
	case elf.EM_386, elf.EM_X86_64, elf.EM_RISCV:
		r.Platform = f.Machine.String()
		r.ABI = "ELF"
		r.AddressUnitBits = 8
	}
	syms, err := f.Symbols()
	if errors.Is(err, elf.ErrNoSymbols) {
		r.Diagnostics = append(r.Diagnostics, "No full symbol table; absence is not deletion evidence")
	} else if err != nil {
		return nil, err
	} else {
		r.SymbolsComplete = r.Linked
	}
	for _, s := range syms {
		t := elf.ST_TYPE(s.Info)
		if t == elf.STT_FILE {
			continue
		}
		if s.Section == elf.SHN_UNDEF || s.Section >= elf.SHN_LORESERVE || int(s.Section) >= len(f.Sections) {
			continue
		}
		sec := f.Sections[s.Section]
		if sec.Flags&elf.SHF_ALLOC == 0 || sec.Flags&elf.SHF_EXECINSTR != 0 {
			continue
		}
		if (t != elf.STT_OBJECT && t != elf.STT_NOTYPE) || s.Name == "" || strings.HasPrefix(s.Name, "$") {
			continue
		}
		binding := elf.ST_BIND(s.Info)
		if binding != elf.STB_GLOBAL && binding != elf.STB_WEAK {
			r.ExcludedSymbolNames = append(r.ExcludedSymbolNames, s.Name)
			continue
		}
		size := s.Size
		// SPRAC71 chapter 11: addresses are native words; ELF sizes are octets.
		if f.Machine == 141 {
			if size%2 != 0 {
				return nil, fmt.Errorf("C28x ELF symbol %s has odd octet size", s.Name)
			}
			size /= 2
		}
		r.Symbols = append(r.Symbols, symbol{s.Name, s.Value, size})
	}
	var sections []section
	var total uint64
	for _, s := range f.Sections {
		if !strings.HasPrefix(s.Name, ".debug_") && !strings.HasPrefix(s.Name, ".zdebug_") {
			continue
		}
		total += s.Size
		if total > maxFile {
			return nil, errors.New("expanded debug sections exceed 256 MiB")
		}
		if strings.HasPrefix(s.Name, ".zdebug_") {
			return nil, errors.New("legacy .zdebug compression unsupported; use SHF_COMPRESSED")
		}
		data, err := s.Data()
		if err != nil {
			return nil, err
		}
		sections = append(sections, section{s.Name, data})
	}
	return sections, nil
}

// TI COFF v1/v2 has a 22-byte header and target ID; Windows PE/COFF is unrelated.
// Reference: TI SPRAAO8, tables 1, 5, 6 and 11. Debug sections use octets.
func readCOFF(b []byte, r *result) ([]section, error) {
	if len(b) < 22 {
		return nil, errors.New("unrecognized/truncated firmware header")
	}
	o := binary.LittleEndian
	v := o.Uint16(b)
	if (v != 0xc1 && v != 0xc2) || o.Uint16(b[20:]) != 0x9d {
		return nil, errors.New("unsupported file header (not ELF or TI C28x COFF)")
	}
	r.Format = fmt.Sprintf("TI COFF v%d", v-0xc0)
	r.Platform = "TI C28x"
	r.ABI = "COFF ABI"
	r.AddressUnitBits = 16
	r.AddressBits = 32
	r.LittleEndian = true
	flags := o.Uint16(b[18:])
	if flags&0x200 != 0 {
		return nil, errors.New("unsupported big-endian C28x COFF")
	}
	r.Linked = flags&2 != 0 && o.Uint16(b[16:]) == 28
	n := int(o.Uint16(b[2:]))
	sympos := uint64(o.Uint32(b[8:]))
	count := uint64(o.Uint32(b[12:]))
	strpos := sympos + count*18
	if strpos+4 > uint64(len(b)) {
		return nil, errors.New("invalid COFF symbol/string table")
	}
	strsize := uint64(o.Uint32(b[strpos:]))
	if strsize < 4 || strpos+strsize > uint64(len(b)) {
		return nil, errors.New("invalid COFF string table length")
	}
	name := func(p []byte) (string, error) {
		if o.Uint32(p) == 0 {
			off := uint64(o.Uint32(p[4:]))
			if off < 4 || off >= strsize {
				return "", errors.New("invalid COFF name offset")
			}
			return cstr(b[strpos+off : strpos+strsize]), nil
		}
		return cstr(p[:8]), nil
	}
	header := 22 + int(o.Uint16(b[16:]))
	hs := 48
	if v == 0xc1 {
		hs = 40
	}
	if header+n*hs > len(b) {
		return nil, errors.New("truncated COFF sections")
	}
	type secmeta struct {
		name  string
		flags uint32
	}
	meta := make([]secmeta, n+1)
	var sections []section
	for i := 0; i < n; i++ {
		p := b[header+i*hs : header+(i+1)*hs]
		sn, err := name(p)
		if err != nil {
			return nil, err
		}
		var sf uint32
		if hs == 40 {
			sf = uint32(o.Uint16(p[36:]))
		} else {
			sf = o.Uint32(p[40:])
		}
		meta[i+1] = secmeta{sn, sf}
		if strings.HasPrefix(sn, ".debug_") {
			sz, off := uint64(o.Uint32(p[16:])), uint64(o.Uint32(p[20:]))
			if off+sz > uint64(len(b)) {
				return nil, errors.New("truncated COFF debug section")
			}
			sections = append(sections, section{sn, b[off : off+sz]})
		}
	}
	for i := uint64(0); i < count; {
		p := b[sympos+i*18 : sympos+(i+1)*18]
		sn, err := name(p)
		if err != nil {
			return nil, err
		}
		sec := int(int16(o.Uint16(p[12:])))
		storage := p[16]
		aux := uint64(p[17])
		if i+aux >= count {
			return nil, errors.New("invalid COFF auxiliary record count")
		}
		if sec > 0 && sec < len(meta) && (storage == 2 || storage == 3) && meta[sec].flags&0x20 == 0 && !strings.HasPrefix(sn, ".") && !strings.HasPrefix(sn, "$") && sn != "" {
			if storage == 3 {
				r.ExcludedSymbolNames = append(r.ExcludedSymbolNames, sn)
			} else {
				r.Symbols = append(r.Symbols, symbol{Name: sn, Address: uint64(o.Uint32(p[8:]))})
			}
		}
		i += 1 + aux
	}
	r.SymbolsComplete = r.Linked && count > 0
	return sections, nil
}
func cstr(b []byte) string {
	if i := bytes.IndexByte(b, 0); i >= 0 {
		return string(b[:i])
	}
	return string(b)
}

func readDwarf(sections []section, r *result) error {
	parts := map[string][]byte{}
	for _, s := range sections {
		if s.name != ".debug_types" {
			if _, ok := parts[s.name]; ok {
				return fmt.Errorf("duplicate %s in unlinked/grouped file; link before import", s.name)
			}
			parts[s.name] = s.data
		}
	}
	if len(parts[".debug_info"]) == 0 {
		r.Diagnostics = append(r.Diagnostics, "No variable type DWARF; existing types must remain unverified")
		return nil
	}
	d, err := dwarf.New(parts[".debug_abbrev"], nil, nil, parts[".debug_info"], nil, nil, nil, parts[".debug_str"])
	if err != nil {
		return err
	}
	for _, s := range sections {
		if s.name == ".debug_types" {
			err = d.AddTypes(fmt.Sprintf("types%d", len(r.Signatures)), s.data)
		} else {
			err = d.AddSection(s.name, s.data)
		}
		if err != nil {
			return err
		}
	}
	reader := d.Reader()
	var entries []*dwarf.Entry
	locations := map[dwarf.Offset]uint64{}
	var versions = map[int]bool{}
	entryVersions := map[dwarf.Offset]int{}
	for {
		e, err := reader.Next()
		if err != nil {
			return err
		}
		if e == nil {
			break
		}
		if len(entries) >= maxDies {
			return errors.New("DIE count exceeds 500000")
		}
		entries = append(entries, e)
		versions[reader.UnitVersion()] = true
		entryVersions[e.Offset] = reader.UnitVersion()
		if e.Tag == dwarf.TagVariable {
			if expr, ok := e.Val(dwarf.AttrLocation).([]byte); ok {
				if addr, ok := absolute(expr, reader.AddressSize(), reader.ByteOrder(), reader.IndexedAddress); ok {
					locations[e.Offset] = addr
				}
			}
		}
	}
	if err = addModelNodes(entries, "info", r, locations, entryVersions); err != nil {
		return err
	}
	tus, err := d.TypeUnitEntries(maxDies - len(entries))
	if err != nil {
		return err
	}
	// Stable identifiers and deterministic JSON independent of Go map order.
	sort.Slice(tus, func(i, j int) bool { return tus[i].Signature < tus[j].Signature })
	for _, tu := range tus {
		prefix := fmt.Sprintf("type:%x", tu.Signature)
		r.Signatures[fmt.Sprintf("%x", tu.Signature)] = fmt.Sprintf("%s:%x", prefix, tu.TypeOffset)
		if err = addNodes(tu.Entries, prefix, r, nil, nil); err != nil {
			return err
		}
	}
	for v := range versions {
		r.DwarfVersions = append(r.DwarfVersions, v)
	}
	sort.Ints(r.DwarfVersions)
	r.TypesComplete = true
	return nil
}

func addNodes(entries []*dwarf.Entry, prefix string, r *result, locations map[dwarf.Offset]uint64, versions map[dwarf.Offset]int) error {
	var stack []*node
	for _, e := range entries {
		if e.Tag == 0 {
			if len(stack) > 0 {
				stack = stack[:len(stack)-1]
			}
			continue
		}
		id := fmt.Sprintf("%s:%x", prefix, e.Offset)
		if e.Tag == dwarf.TagCompileUnit || e.Tag == dwarf.TagPartialUnit || e.Tag == dwarf.TagTypeUnit {
			stack = nil
		}
		n := &node{ID: id, Tag: uint32(e.Tag), Attributes: map[uint32]attribute{}, Children: []string{}}
		n.Version = 4
		if versions != nil {
			n.Version = versions[e.Offset]
		}
		for _, f := range e.Field {
			if !modelAttribute(f.Attr) {
				continue
			}
			a := attribute{}
			switch v := f.Val.(type) {
			case dwarf.Offset:
				a.Ref = fmt.Sprintf("%s:%x", prefix, v)
			case string:
				a.Text = v
			case int64:
				a.Number = &v
			case uint64:
				if f.Class == dwarf.ClassReferenceSig {
					a.Ref = fmt.Sprintf("sig:%x", v)
				} else if v <= 0x7fffffffffffffff {
					x := int64(v)
					a.Number = &x
				}
			case bool:
				x := int64(0)
				if v {
					x = 1
				}
				a.Number = &x
			default:
				// Preserve attribute presence even when its value is a dynamic
				// expression. The model must not mistake it for an omitted zero.
			}
			n.Attributes[uint32(f.Attr)] = a
		}
		if addr, ok := locations[e.Offset]; ok {
			n.Location = &addr
		}
		n.HasLocation = e.AttrField(dwarf.AttrLocation) != nil
		if e.Tag == dwarf.TagMember {
			n.HasMemberLocation = e.AttrField(dwarf.AttrDataMemberLoc) != nil
			switch v := e.Val(dwarf.AttrDataMemberLoc).(type) {
			case int64:
				if v >= 0 {
					x := uint64(v)
					n.MemberOffset = &x
				}
			case []byte:
				if off, ok := memberOffset(v); ok {
					n.MemberOffset = &off
				}
			}
		}
		if len(stack) > 0 {
			parent := stack[len(stack)-1]
			parent.Children = append(parent.Children, id)
		}
		r.Nodes = append(r.Nodes, n)
		if e.Children {
			stack = append(stack, n)
			if len(stack) > 128 {
				return errors.New("DIE nesting exceeds 128")
			}
		}
	}
	return nil
}

// This is the wire contract consumed by DwarfModelBuilder.Attribute and
// BuildTypeCore. Keep presence even for values the model cannot evaluate:
// an unsupported expression is different from an omitted (default) value.
func modelAttribute(a dwarf.Attr) bool {
	switch a {
	case dwarf.AttrName, dwarf.AttrByteSize, dwarf.AttrType, dwarf.AttrEncoding,
		dwarf.AttrSpecification, dwarf.AttrAbstractOrigin, dwarf.AttrLinkageName,
		0x2001, 0x2007, // MIPS / TI linkage names
		dwarf.AttrExternal, dwarf.AttrDeclaration, dwarf.AttrBitOffset,
		dwarf.AttrBitSize, dwarf.AttrDataBitOffset, dwarf.AttrOrdering,
		dwarf.AttrStride, dwarf.AttrStrideSize, dwarf.AttrLowerBound,
		dwarf.AttrCount, dwarf.AttrUpperBound, dwarf.AttrSignature:
		return true
	}
	return false
}

// Seed variables that can affect C# matching, fallback imports or diagnostics.
// Follow all retained references, signature targets and complete child lists.
// Do not prune members, inheritance, unknown children or pointer targets based
// on today's supported layouts. Preserve original order and dangling refs.
func retainModelDependencies(r *result) {
	byID := make(map[string]*node, len(r.Nodes))
	for _, n := range r.Nodes {
		byID[n.ID] = n
	}
	pending := modelVariableRoots(r, byID)
	for _, id := range r.Signatures {
		pending = append(pending, id)
	}
	retained := make(map[string]bool)
	for len(pending) > 0 {
		id := pending[len(pending)-1]
		pending = pending[:len(pending)-1]
		if strings.HasPrefix(id, "sig:") {
			id = r.Signatures[strings.TrimPrefix(id, "sig:")]
		}
		if retained[id] {
			continue
		}
		n := byID[id]
		if n == nil {
			continue
		}
		retained[id] = true
		pending = append(pending, n.Children...)
		for _, a := range n.Attributes {
			if a.Ref != "" {
				pending = append(pending, a.Ref)
			}
		}
	}
	nodes := r.Nodes[:0]
	for _, n := range r.Nodes {
		if retained[n.ID] {
			nodes = append(nodes, n)
		}
	}
	clear(r.Nodes[len(nodes):])
	r.Nodes = nodes
}

func uleb(b []byte) (uint64, int, bool) {
	var n uint64
	for i, c := range b {
		if i >= 10 || (i == 9 && c > 1) {
			return 0, 0, false
		}
		n |= uint64(c&127) << uint(i*7)
		if c < 128 {
			return n, i + 1, true
		}
	}
	return 0, 0, false
}
func absolute(b []byte, size int, order binary.ByteOrder, index func(uint64) (uint64, error)) (uint64, bool) {
	if len(b) == 0 {
		return 0, false
	}
	var addr uint64
	var rest []byte
	switch b[0] {
	case 3:
		if len(b) < size+1 {
			return 0, false
		}
		switch size {
		case 4:
			addr = uint64(order.Uint32(b[1:]))
		case 8:
			addr = order.Uint64(b[1:])
		case 2:
			addr = uint64(order.Uint16(b[1:]))
		default:
			return 0, false
		}
		rest = b[1+size:]
	case 0xa1:
		n, l, ok := uleb(b[1:])
		if !ok {
			return 0, false
		}
		var err error
		addr, err = index(n)
		if err != nil {
			return 0, false
		}
		rest = b[1+l:]
	default:
		return 0, false
	}
	if len(rest) == 0 {
		return addr, true
	}
	if rest[0] != 0x23 {
		return 0, false
	}
	n, l, ok := uleb(rest[1:])
	if !ok || l+1 != len(rest) || addr > ^uint64(0)-n {
		return 0, false
	}
	return addr + n, true
}
func memberOffset(b []byte) (uint64, bool) {
	// Initial stack is the containing object's address. Only constant addition
	// is accepted; dereference, registers, frame base and stack_value are rejected.
	if len(b) == 0 {
		return 0, false
	}
	if b[0] == 0x23 {
		n, l, ok := uleb(b[1:])
		return n, ok && l+1 == len(b)
	}
	if b[0] == 0x10 {
		n, l, ok := uleb(b[1:])
		return n, ok && l+2 == len(b) && b[len(b)-1] == 0x22
	}
	if len(b) == 2 && b[0] >= 0x30 && b[0] <= 0x4f && b[1] == 0x22 {
		return uint64(b[0] - 0x30), true
	}
	return 0, false
}
