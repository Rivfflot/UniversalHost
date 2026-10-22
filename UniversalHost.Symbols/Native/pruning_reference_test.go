package main

import (
	"unicode/utf8"
	"universalhost/symbols/internal/dwarf"
)

// Run after decoding all DWARF, so pruning cannot hide reader diagnostics.
// Mirror DwarfModelBuilder's inherited attribute/location resolution: own
// attribute presence wins, specification precedes abstract_origin, and each
// lookup shares a visited set with a 128-node limit. Do not flatten nodes: C#
// still needs the original references, unsupported expressions and ordering.
func referenceModelVariableRoots(r *result, byID map[string]*node) []string {
	// encoding/json replaces invalid UTF-8 before C# compares names. Compare
	// that same text here, including distinct invalid byte sequences that
	// become identical names on the wire.
	wireText := func(text string) string {
		if utf8.ValidString(text) {
			return text
		}
		return string([]rune(text))
	}
	names := make(map[string]bool, len(r.Symbols))
	for _, s := range r.Symbols {
		names[wireText(s.Name)] = true
	}
	var attr func(*node, uint32, map[string]bool) *attribute
	attr = func(n *node, key uint32, seen map[string]bool) *attribute {
		if seen[n.ID] {
			return nil
		}
		seen[n.ID] = true
		if len(seen) > 128 {
			return nil
		}
		if a, ok := n.Attributes[key]; ok {
			return &a
		}
		for _, ref := range [...]uint32{uint32(dwarf.AttrSpecification), uint32(dwarf.AttrAbstractOrigin)} {
			if target := byID[n.Attributes[ref].Ref]; target != nil {
				if a := attr(target, key, seen); a != nil {
					return a
				}
			}
		}
		return nil
	}
	text := func(n *node, key uint32) string {
		if a := attr(n, key, map[string]bool{}); a != nil {
			return wireText(a.Text)
		}
		return ""
	}
	// C# updates variable.Location in original node order. Simulate that in a
	// side map (including nil values), without changing what is sent to C#.
	locations := make(map[string]*uint64)
	var location func(*node, map[string]bool) *uint64
	location = func(n *node, seen map[string]bool) *uint64 {
		if seen[n.ID] {
			return nil
		}
		seen[n.ID] = true
		if len(seen) > 128 {
			return nil
		}
		loc := n.Location
		if updated, ok := locations[n.ID]; ok {
			loc = updated
		}
		if loc != nil || n.HasLocation {
			return loc
		}
		for _, ref := range [...]uint32{uint32(dwarf.AttrSpecification), uint32(dwarf.AttrAbstractOrigin)} {
			if target := byID[n.Attributes[ref].Ref]; target != nil {
				if value := location(target, seen); value != nil {
					return value
				}
			}
		}
		return nil
	}
	var roots []string
	for _, n := range r.Nodes {
		if n.Tag != uint32(dwarf.TagVariable) {
			continue
		}
		loc := location(n, map[string]bool{})
		locations[n.ID] = loc
		name := text(n, uint32(dwarf.AttrName))
		link := ""
		for _, key := range [...]uint32{uint32(dwarf.AttrLinkageName), 0x2001, 0x2007, uint32(dwarf.AttrName)} {
			if link = text(n, key); link != "" {
				break
			}
		}
		// Keep private fixed-location variables too: they determine exclusion
		// names and TypesComplete. Location-less name matches determine the
		// invalid-location diagnostic even when they cannot be imported.
		if loc != nil || names[link] || names[name] {
			roots = append(roots, n.ID)
		}
	}
	return roots
}
