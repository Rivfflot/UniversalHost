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
func modelVariableRoots(r *result, byID map[string]*node) []string {
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
	origins := [...]dwarf.Attr{dwarf.AttrSpecification, dwarf.AttrAbstractOrigin}
	hasOrigin := func(n *node) bool {
		return byID[n.reference(origins[0])] != nil || byID[n.reference(origins[1])] != nil
	}
	var attr func(*node, uint32, map[string]bool) (attribute, bool)
	attr = func(n *node, key uint32, seen map[string]bool) (attribute, bool) {
		if seen[n.ID] {
			return attribute{}, false
		}
		seen[n.ID] = true
		if len(seen) > 128 {
			return attribute{}, false
		}
		if a, ok := n.lookupAttribute(key); ok {
			return a, true
		}
		for _, keyRef := range origins {
			if target := byID[n.reference(keyRef)]; target != nil {
				if a, ok := attr(target, key, seen); ok {
					return a, true
				}
			}
		}
		return attribute{}, false
	}
	text := func(n *node, key uint32) string {
		// Each text lookup starts with an empty visited set. Own presence and
		// missing inheritance targets can be handled without allocating it.
		if value, ok := n.lookupAttribute(key); ok {
			return wireText(value.Text)
		}
		if !hasOrigin(n) {
			return ""
		}
		if a, ok := attr(n, key, map[string]bool{}); ok {
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
		for _, key := range origins {
			if target := byID[n.reference(key)]; target != nil {
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
		loc, visited := locations[n.ID]
		if !visited {
			loc = n.Location
		}
		if loc == nil && !n.HasLocation && hasOrigin(n) {
			loc = location(n, map[string]bool{})
		}
		locations[n.ID] = loc
		// A fixed location already makes this a root; names cannot change that.
		if loc != nil {
			roots = append(roots, n.ID)
			continue
		}
		name := text(n, uint32(dwarf.AttrName))
		if names[name] {
			roots = append(roots, n.ID)
			continue
		}
		link := ""
		for _, key := range [...]uint32{uint32(dwarf.AttrLinkageName), 0x2001, 0x2007, uint32(dwarf.AttrName)} {
			if link = text(n, key); link != "" {
				break
			}
		}
		// Keep private fixed-location variables too: they determine exclusion
		// names and TypesComplete. Location-less name matches determine the
		// invalid-location diagnostic even when they cannot be imported.
		if names[link] {
			roots = append(roots, n.ID)
		}
	}
	return roots
}
