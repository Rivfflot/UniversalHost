package main

import (
	"encoding/binary"
	"encoding/json"
	"errors"
	"reflect"
	"strings"
	"testing"
	"universalhost/symbols/internal/dwarf"
)

func TestModelDependencyClosure(t *testing.T) {
	n := func(id string, tag dwarf.Tag, children []string, refs ...string) *node {
		attrs := map[uint32]attribute{}
		for i, ref := range refs {
			attrs[uint32(i)] = attribute{Ref: ref}
		}
		return &node{ID: id, Tag: uint32(tag), Attributes: attrs, Children: children}
	}
	r := &result{Signatures: map[string]string{"abc": "type", "unused": "signature-only"}, Nodes: []*node{
		n("cu", dwarf.TagCompileUnit, []string{"variable", "unrelated"}),
		n("variable", dwarf.TagVariable, nil, "declaration"),
		n("declaration", dwarf.TagVariable, nil, "origin"),
		n("origin", dwarf.TagVariable, nil, "sig:abc"),
		n("local-no-address", dwarf.TagVariable, nil, "pointer"),
		n("pointer", dwarf.TagPointerType, nil, "type"),
		n("type", dwarf.TagStructType, []string{"member", "inheritance", "unknown-child", "dangling"}),
		n("member", dwarf.TagMember, nil, "array"),
		n("inheritance", dwarf.TagInheritance, nil, "base"),
		n("array", dwarf.TagArrayType, []string{"dimension"}, "base"),
		n("dimension", dwarf.TagSubrangeType, nil),
		n("base", dwarf.TagBaseType, nil),
		n("unknown-child", dwarf.Tag(0x9999), nil, "type"),
		n("signature-only", dwarf.TagTypedef, nil, "base"),
		n("unrelated", dwarf.TagSubprogram, []string{"unrelated-type"}),
		n("unrelated-type", dwarf.TagBaseType, nil),
	}}
	address := uint64(4096)
	r.Nodes[1].Location = &address
	// Even a location-less variable must survive when referenced by a type.
	r.Nodes[6].Children = append(r.Nodes[6].Children, "local-no-address")
	before, _ := json.Marshal(r.Nodes[1:14])
	retainModelDependencies(r)
	after, _ := json.Marshal(r.Nodes)
	if string(before) != string(after) {
		t.Fatalf("closure must preserve required variables, declarations, members, inheritance, dimensions, signatures, cycles, unknown and dangling children: %s", after)
	}
}

func TestWireAttributesPreserveDynamicLayoutAndInheritedValues(t *testing.T) {
	entries := []*dwarf.Entry{
		{Offset: 1, Tag: dwarf.TagVariable, Field: []dwarf.Field{
			{Attr: dwarf.AttrName, Val: "global"}, {Attr: dwarf.AttrSpecification, Val: dwarf.Offset(2)},
			{Attr: dwarf.AttrAbstractOrigin, Val: dwarf.Offset(3)}, {Attr: dwarf.AttrExternal, Val: true},
			{Attr: dwarf.AttrType, Class: dwarf.ClassReferenceSig, Val: uint64(0xabc)},
			{Attr: dwarf.AttrDeclFile, Val: int64(9)}, {Attr: dwarf.AttrProducer, Val: "unused"},
		}},
		{Offset: 2, Tag: dwarf.TagMember, Field: []dwarf.Field{
			{Attr: dwarf.AttrDataMemberLoc, Val: []byte{0x91, 4}}, // unsupported, never default to zero
			{Attr: dwarf.AttrBitSize, Val: int64(3)}, {Attr: dwarf.AttrDataBitOffset, Val: int64(7)},
			{Attr: dwarf.AttrDeclaration, Val: true},
		}},
		{Offset: 3, Tag: dwarf.TagSubrangeType, Field: []dwarf.Field{
			{Attr: dwarf.AttrLowerBound, Val: []byte{0x91, 4}}, {Attr: dwarf.AttrCount, Val: []byte{0x91, 5}},
			{Attr: dwarf.AttrStride, Val: []byte{0x91, 6}}, {Attr: dwarf.AttrUpperBound, Val: int64(7)},
		}},
	}
	r := &result{}
	if err := addNodes(entries, "info", r, map[dwarf.Offset]uint64{1: 4096}, map[dwarf.Offset]int{1: 5, 2: 3, 3: 4}); err != nil {
		t.Fatal(err)
	}
	v, m, dim := r.Nodes[0], r.Nodes[1], r.Nodes[2]
	if v.Attributes[uint32(dwarf.AttrType)].Ref != "sig:abc" || v.Attributes[uint32(dwarf.AttrSpecification)].Ref != "info:2" || v.Location == nil || *v.Location != 4096 {
		t.Fatal("lost signature, inherited reference or location")
	}
	if _, ok := v.Attributes[uint32(dwarf.AttrProducer)]; ok || len(v.Attributes) != 5 {
		t.Fatalf("unused source metadata still on wire: %+v", v.Attributes)
	}
	if !m.HasMemberLocation || m.MemberOffset != nil || m.Version != 3 {
		t.Fatal("unsupported member expression changed semantics")
	}
	for _, a := range []dwarf.Attr{dwarf.AttrLowerBound, dwarf.AttrCount, dwarf.AttrStride} {
		if value, ok := dim.Attributes[uint32(a)]; !ok || !reflect.DeepEqual(value, attribute{}) {
			t.Fatal("dynamic array attribute presence lost", a)
		}
	}
}

func TestFixedAddressExpressions(t *testing.T) {
	index := func(i uint64) (uint64, error) {
		if i == 3 {
			return 0x20000000, nil
		}
		return 0, errors.New("bad index")
	}
	for _, tc := range []struct {
		expr []byte
		want uint64
		ok   bool
	}{
		{[]byte{3, 0x34, 0x12, 0, 0}, 0x1234, true},
		{[]byte{0xa1, 3}, 0x20000000, true},
		{[]byte{0xa1, 3, 0x23, 4}, 0x20000004, true},
		{[]byte{0xa1, 9}, 0, false},
		{[]byte{0x91, 0}, 0, false},       // frame base
		{[]byte{0x50}, 0, false},          // register
		{[]byte{0x10, 1, 0x9f}, 0, false}, // constant value, not an address
		{[]byte{3, 1}, 0, false},
		{[]byte{3, 1, 0, 0, 0, 0x06}, 0, false}, // dereference
	} {
		got, ok := absolute(tc.expr, 4, binary.LittleEndian, index)
		if ok != tc.ok || (ok && got != tc.want) {
			t.Fatalf("%x got %x/%v want %x/%v", tc.expr, got, ok, tc.want, tc.ok)
		}
	}
}

func TestStaticSymbolsFilteredAtRead(t *testing.T) {
	order := binary.LittleEndian
	// ELF32 with one local and one global object in an allocated data section.
	b := make([]byte, 424)
	copy(b, []byte{0x7f, 'E', 'L', 'F', 1, 1, 1})
	order.PutUint16(b[16:], 2)
	order.PutUint16(b[18:], 40)
	order.PutUint32(b[20:], 1)
	order.PutUint32(b[32:], 224)
	order.PutUint16(b[40:], 52)
	order.PutUint16(b[46:], 40)
	order.PutUint16(b[48:], 5)
	order.PutUint16(b[50:], 4)
	names := "\x00private\x00public\x00"
	sections := "\x00.data\x00.symtab\x00.strtab\x00.shstrtab\x00"
	copy(b[116:], names)
	copy(b[144:], sections)
	for i, name := range []string{".data", ".symtab", ".strtab", ".shstrtab"} {
		p := b[224+(i+1)*40:]
		order.PutUint32(p, uint32(strings.Index(sections, name)))
		order.PutUint32(p[32:], 1)
	}
	p := b[264:]
	order.PutUint32(p[4:], 1)
	order.PutUint32(p[8:], 3)
	order.PutUint32(p[12:], 0x1000)
	order.PutUint32(p[16:], 64)
	order.PutUint32(p[20:], 4)
	p = b[304:]
	order.PutUint32(p[4:], 2)
	order.PutUint32(p[16:], 68)
	order.PutUint32(p[20:], 48)
	order.PutUint32(p[24:], 3)
	order.PutUint32(p[28:], 2)
	order.PutUint32(p[36:], 16)
	for i, offset := range []uint32{116, 144} {
		p = b[344+i*40:]
		order.PutUint32(p[4:], 3)
		order.PutUint32(p[16:], offset)
		order.PutUint32(p[20:], uint32([]int{len(names), len(sections)}[i]))
	}
	for i, nameOffset := range []uint32{1, 9} {
		p = b[84+i*16:]
		order.PutUint32(p, nameOffset)
		order.PutUint32(p[4:], uint32(0x1000+i*2))
		order.PutUint32(p[8:], 2)
		p[12] = byte(i<<4 | 1)
		order.PutUint16(p[14:], 1)
	}
	r := &result{}
	if _, err := readELF(b, r); err != nil {
		t.Fatal(err)
	}
	if len(r.Symbols) != 1 || r.Symbols[0].Name != "public" || len(r.ExcludedSymbolNames) != 1 || r.ExcludedSymbolNames[0] != "private" {
		t.Fatalf("ELF static filter: %+v", r)
	}
	// TI COFF v2 uses C_STAT=3 and C_EXT=2, independently of ELF binding.
	b = make([]byte, 110)
	order.PutUint16(b, 0xc2)
	order.PutUint16(b[2:], 1)
	order.PutUint32(b[8:], 70)
	order.PutUint32(b[12:], 2)
	order.PutUint16(b[18:], 2)
	order.PutUint16(b[20:], 0x9d)
	copy(b[22:], ".data")
	order.PutUint32(b[62:], 0x40)
	for i, name := range []string{"private", "public"} {
		p = b[70+i*18:]
		copy(p, name)
		order.PutUint32(p[8:], uint32(0x1000+i))
		order.PutUint16(p[12:], 1)
		p[16] = byte(3 - i)
	}
	order.PutUint32(b[106:], 4)
	r = &result{}
	if _, err := readCOFF(b, r); err != nil {
		t.Fatal(err)
	}
	if len(r.Symbols) != 1 || r.Symbols[0].Name != "public" || len(r.ExcludedSymbolNames) != 1 || r.ExcludedSymbolNames[0] != "private" {
		t.Fatalf("TI COFF static filter: %+v", r)
	}
}

func TestMemberExpressions(t *testing.T) {
	for _, tc := range []struct {
		expr []byte
		want uint64
		ok   bool
	}{
		{[]byte{0x23, 4}, 4, true}, {[]byte{0x10, 16, 0x22}, 16, true},
		{[]byte{0x34, 0x22}, 4, true}, {[]byte{0x91, 4}, 0, false},
		{[]byte{0x23, 4, 0x06}, 0, false}, {[]byte{0x23, 0x80}, 0, false},
	} {
		got, ok := memberOffset(tc.expr)
		if ok != tc.ok || (ok && got != tc.want) {
			t.Fatalf("%x got %x/%v", tc.expr, got, ok)
		}
	}
}

func TestCOFFRejectsPEAndTruncation(t *testing.T) {
	for _, data := range [][]byte{nil, []byte("MZ Windows PE is not TI COFF"), {0xc2, 0, 0, 0}} {
		if _, err := readCOFF(data, &result{}); err == nil {
			t.Fatal("accepted invalid COFF")
		}
	}
}

func TestELFArchitectureWithoutDwarf(t *testing.T) {
	for _, tc := range []struct {
		machine uint16
		units   int
		abi     string
	}{
		{141, 16, "EABI"}, {40, 8, "ARM ELF"}, {0xffff, 0, "Unknown"},
	} {
		b := make([]byte, 52)
		copy(b, []byte{0x7f, 'E', 'L', 'F', 1, 1, 1})
		binary.LittleEndian.PutUint16(b[16:], 2)
		binary.LittleEndian.PutUint16(b[18:], tc.machine)
		binary.LittleEndian.PutUint32(b[20:], 1)
		binary.LittleEndian.PutUint16(b[40:], 52)
		r := &result{Platform: "Unknown", ABI: "Unknown"}
		if _, err := readELF(b, r); err != nil {
			t.Fatal(err)
		}
		if r.AddressUnitBits != tc.units || r.ABI != tc.abi || !r.Linked {
			t.Fatalf("machine %x: %+v", tc.machine, r)
		}
		binary.LittleEndian.PutUint16(b[16:], 1)
		if _, err := readELF(b, r); err != nil || r.Linked {
			t.Fatalf("relocatable file marked linked: %v", err)
		}
	}
}
