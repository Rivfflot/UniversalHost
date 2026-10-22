package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestVariablePruningDifferentialFixtures(t *testing.T) {
	dir := os.Getenv("SYMBOL_PRUNING_FIXTURES")
	if dir != "" {
		if err := os.MkdirAll(dir, 0755); err != nil {
			t.Fatal(err)
		}
	}
	for _, name := range []string{"conflict", "name-only", "fallback", "private-only", "declaration", "unsupported-own-location", "signature", "attribute-cycle", "depth-limit", "missing-reference", "nonvariable-origin", "type-child-variable", "invalid-utf8-name"} {
		num := func(v int64) *int64 { return &v }
		addr := uint64(4096)
		n := func(id string, tag uint32, name string) *node {
			return &node{ID: id, Tag: tag, Version: 5, Attributes: map[uint32]attribute{3: {Text: name}}, Children: []string{}}
		}
		base := n("base", 0x24, "int32")
		base.Attributes[11] = attribute{Number: num(4)}
		base.Attributes[62] = attribute{Number: num(5)}
		v := n("v", 0x34, "global")
		v.Location = &addr
		v.Attributes[73] = attribute{Ref: "base"}
		r := &result{Schema: 1, Backend: "fixture", Format: "ELF", Platform: "ARM", ABI: "ARM ELF", AddressUnitBits: 8, AddressBits: 32, LittleEndian: true, Linked: true, SymbolsComplete: true, TypesComplete: true, Symbols: []symbol{{Name: "global", Address: addr, Size: 4}}, Nodes: []*node{v, base}, Signatures: map[string]string{}, Diagnostics: []string{}, DwarfVersions: []int{5}}
		switch name {
		case "conflict":
			dup := n("duplicate", 0x34, "global")
			dup.Location = &addr
			dup.Attributes[73] = attribute{Ref: "base"}
			r.Nodes = append(r.Nodes, dup)
		case "name-only":
			v.Location = nil
			v.HasLocation = true
		case "fallback":
			r.Symbols = []symbol{}
			v.Attributes[0x3f] = attribute{Number: num(1)}
		case "private-only":
			r.Symbols = []symbol{}
		case "declaration":
			decl := n("decl", 0x34, "global")
			decl.Attributes[73] = attribute{Ref: "base"}
			decl.Attributes[0x3f] = attribute{Number: num(1)}
			origin := n("origin", 0x34, "")
			origin.Attributes = map[uint32]attribute{0x47: {Ref: "decl"}}
			v.Attributes = map[uint32]attribute{0x31: {Ref: "origin"}}
			r.Nodes = append(r.Nodes, decl, origin)
		case "unsupported-own-location":
			origin := n("origin", 0x34, "global")
			origin.Location = &addr
			origin.Attributes[73] = attribute{Ref: "base"}
			v.Location = nil
			v.HasLocation = true
			v.Attributes[0x47] = attribute{Ref: "origin"}
			r.Nodes = append(r.Nodes, origin)
		case "signature":
			v.Attributes[73] = attribute{Ref: "sig:abc"}
			r.Signatures["abc"] = "base"
		case "attribute-cycle":
			origin := n("origin", 0x34, "")
			origin.Attributes = map[uint32]attribute{0x47: {Ref: "v"}}
			v.Attributes = map[uint32]attribute{0x47: {Ref: "origin"}}
			r.Nodes = append(r.Nodes, origin)
		case "depth-limit":
			r.Nodes = []*node{base}
			for i := 0; i < 130; i++ {
				id := string(rune('a' + i))
				entry := n(id, 0x34, "")
				entry.Attributes = map[uint32]attribute{0x47: {Ref: string(rune('a' + i + 1))}}
				if i == 129 {
					entry.Attributes = map[uint32]attribute{3: {Text: "global"}, 73: {Ref: "base"}}
					entry.Location = &addr
				}
				r.Nodes = append(r.Nodes, entry)
			}
		case "missing-reference":
			v.Attributes[73] = attribute{Ref: "missing"}
			v.Attributes[0x47] = attribute{Ref: "missing-origin"}
		case "nonvariable-origin":
			origin := n("function-origin", 0x2e, "global")
			origin.Attributes[73] = attribute{Ref: "base"}
			v.Attributes = map[uint32]attribute{0x47: {Ref: "function-origin"}}
			r.Nodes = append(r.Nodes, origin)
		case "type-child-variable":
			child := n("type-child", 0x34, "private-child")
			child.Location = &addr
			child.Attributes[73] = attribute{Ref: "base"}
			base.Children = []string{child.ID}
			r.Nodes = append(r.Nodes, child)
		case "invalid-utf8-name":
			v.Attributes[3] = attribute{Text: "global\xff"}
			r.Symbols[0].Name = "global\xfe"
			v.Location = nil
			v.HasLocation = true
		}
		// A disjoint stack variable and its type must never reach C#.
		stack := n("unrelated-stack", 0x34, "unrelated-stack")
		stack.HasLocation = true
		stack.Attributes[73] = attribute{Ref: "unrelated-type"}
		r.Nodes = append(r.Nodes, stack, n("unrelated-type", 0x24, "unused"))
		original := make(map[string]string)
		for _, entry := range r.Nodes {
			b, err := json.Marshal(entry)
			if err != nil {
				t.Fatal(err)
			}
			original[entry.ID] = string(b)
		}
		write := func(suffix string) {
			if dir == "" {
				return
			}
			b, err := json.Marshal(r)
			if err != nil {
				t.Fatal(err)
			}
			if err = os.WriteFile(filepath.Join(dir, name+suffix+".json"), b, 0644); err != nil {
				t.Fatal(err)
			}
		}
		write(".before")
		retainModelDependencies(r)
		found := make(map[string]bool)
		for _, entry := range r.Nodes {
			found[entry.ID] = true
			b, err := json.Marshal(entry)
			if err != nil || string(b) != original[entry.ID] {
				t.Fatalf("%s: retained node was modified: %s", name, entry.ID)
			}
		}
		if found[stack.ID] || found["unrelated-type"] {
			t.Fatalf("%s: unrelated stack graph survived", name)
		}
		if name != "depth-limit" && !found[v.ID] {
			t.Fatalf("%s: lost variable affecting matching or diagnostics", name)
		}
		write(".after")
	}
}
