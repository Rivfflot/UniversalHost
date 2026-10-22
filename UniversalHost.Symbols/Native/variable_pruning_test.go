package main

import "testing"

func TestVariablePruningKeepsRequiredRoles(t *testing.T) {
	num := func(v int64) *int64 { return &v }
	addr := uint64(4096)
	n := func(id string, tag uint32, name string) *node {
		return &node{ID: id, Tag: tag, Version: 5, Attributes: map[uint32]attribute{3: {Text: name}}, Children: []string{}}
	}
	public := n("public", 0x34, "global")
	public.Location = &addr
	public.Attributes[73] = attribute{Ref: "base"}
	private := n("private", 0x34, "private")
	private.Location = &addr
	private.Attributes[73] = attribute{Ref: "unused-type"}
	stack := n("stack", 0x34, "unrelated-stack")
	stack.HasLocation = true
	stack.Attributes[73] = attribute{Ref: "unused-type"}
	nameOnly := n("name-only", 0x34, "global")
	nameOnly.HasLocation = true
	external := n("external", 0x34, "stripped-external")
	external.Location = &addr
	external.Attributes[0x3f] = attribute{Number: num(1)}
	external.Attributes[73] = attribute{Ref: "base"}
	decl := n("decl", 0x34, "inherited")
	decl.Attributes[73] = attribute{Ref: "base"}
	decl.Attributes[0x3f] = attribute{Number: num(1)}
	definition := n("definition", 0x34, "")
	delete(definition.Attributes, 3)
	definition.Attributes[0x47] = attribute{Ref: "decl"}
	definition.Location = &addr
	base := n("base", 0x24, "int")
	base.Attributes[11] = attribute{Number: num(4)}
	base.Attributes[62] = attribute{Number: num(5)}
	unused := n("unused-type", 0x13, "unused")
	unused.Children = []string{"unused-member"}
	member := n("unused-member", 0xd, "member")
	member.Attributes[73] = attribute{Ref: "base"}
	r := &result{Symbols: []symbol{{Name: "global", Address: addr, Size: 4}}, Signatures: map[string]string{}, Nodes: []*node{public, private, stack, nameOnly, external, decl, definition, base, unused, member}}
	retainModelDependencies(r)
	got := map[string]*node{}
	for _, entry := range r.Nodes {
		got[entry.ID] = entry
	}
	for _, id := range []string{"public", "private", "name-only", "external", "decl", "definition", "base"} {
		if got[id] == nil {
			t.Fatal("lost matching, exclusion, fallback or declaration role:", id)
		}
	}
	if got["stack"] != nil {
		t.Fatal("irrelevant stack variable retained")
	}
	if got["unused-type"] == nil || got["unused-member"] == nil {
		t.Fatal("lost fixed private type closure")
	}
}

func TestVariablePruningPromotesReferencesAndPreservesUnsupportedLocation(t *testing.T) {
	addr := uint64(8192)
	decl := &node{ID: "decl", Tag: 0x34, Attributes: map[uint32]attribute{3: {Text: "declaration"}, 73: {Ref: "base"}}}
	definition := &node{ID: "definition", Tag: 0x34, Location: &addr, Attributes: map[uint32]attribute{0x47: {Ref: "decl"}}}
	unsupported := &node{ID: "unsupported", Tag: 0x34, HasLocation: true, Attributes: map[uint32]attribute{3: {Text: "global"}, 0x47: {Ref: "definition"}}}
	base := &node{ID: "base", Tag: 0x24, Attributes: map[uint32]attribute{}}
	r := &result{Symbols: []symbol{{Name: "global"}}, Nodes: []*node{decl, definition, unsupported, base}}
	retainModelDependencies(r)
	got := map[string]*node{}
	for _, n := range r.Nodes {
		got[n.ID] = n
	}
	if len(got) != 4 || got["decl"] != decl || got["definition"] != definition || got["unsupported"].Location != nil || !got["unsupported"].HasLocation {
		t.Fatal("dependency declaration must be full; unsupported own location must block inheritance")
	}
}
