package main

import (
	"encoding/json"
	"runtime"
	"testing"
	"universalhost/symbols/internal/dwarf"
)

func TestParallelNodesPreserveOrderAndReferences(t *testing.T) {
	var entries []*dwarf.Entry
	locations := map[dwarf.Offset]uint64{}
	versions := map[dwarf.Offset]int{}
	for unit := 0; unit < 4; unit++ {
		entries = append(entries, &dwarf.Entry{Offset: dwarf.Offset(len(entries) + 1), Tag: dwarf.TagCompileUnit, Children: true})
		for member := 0; member < 1500; member++ {
			offset := dwarf.Offset(len(entries) + 1)
			entry := &dwarf.Entry{Offset: offset, Tag: dwarf.TagVariable, Field: []dwarf.Field{
				{Attr: dwarf.AttrName, Val: "variable"},
				{Attr: dwarf.AttrType, Val: dwarf.Offset(1503)}, // cross-unit reference
				{Attr: dwarf.AttrSpecification, Val: dwarf.Offset(2)},
				{Attr: dwarf.AttrLowerBound, Val: []byte{0x91, 4}},
			}}
			entries = append(entries, entry)
			locations[offset] = uint64(offset) * 4
			versions[offset] = unit%3 + 3
		}
		entries = append(entries, &dwarf.Entry{})
	}
	before, _ := json.Marshal(entries)
	expected := &result{}
	if err := addNodes(entries, "info", expected, locations, versions); err != nil {
		t.Fatal(err)
	}
	want, _ := json.Marshal(expected.Nodes)
	for _, workers := range []int{1, 2, 4, 8, 64} {
		actual := &result{}
		if err := addNodesParallel(entries, "info", actual, locations, versions, workers); err != nil {
			t.Fatal(err)
		}
		got, _ := json.Marshal(actual.Nodes)
		if string(want) != string(got) {
			t.Fatalf("%d workers changed node content or order", workers)
		}
	}
	after, _ := json.Marshal(entries)
	previous := runtime.GOMAXPROCS(0)
	defer runtime.GOMAXPROCS(previous)
	for _, processors := range []int{1, 2, 4} {
		runtime.GOMAXPROCS(processors)
		actual := &result{}
		if err := addModelNodes(entries, "info", actual, locations, versions); err != nil {
			t.Fatal(err)
		}
		got, _ := json.Marshal(actual.Nodes)
		if string(want) != string(got) {
			t.Fatalf("%d processors changed node content or order", processors)
		}
	}
	if string(before) != string(after) {
		t.Fatal("parallel conversion modified decoder entries")
	}
}

func TestParallelNodesPropagateNestingError(t *testing.T) {
	entries := []*dwarf.Entry{{Offset: 1, Tag: dwarf.TagCompileUnit, Children: true}}
	for i := 0; i < 128; i++ {
		entries = append(entries, &dwarf.Entry{Offset: dwarf.Offset(i + 2), Tag: dwarf.TagStructType, Children: true})
	}
	entries = append(entries, &dwarf.Entry{Offset: 1000, Tag: dwarf.TagCompileUnit})
	for i := 0; i < 4500; i++ {
		entries = append(entries, &dwarf.Entry{Offset: dwarf.Offset(i + 2000), Tag: dwarf.TagVariable})
	}
	for _, workers := range []int{1, 2, 4, 8} {
		err := addNodesParallel(entries, "info", &result{}, nil, nil, workers)
		if err == nil || err.Error() != "DIE nesting exceeds 128" {
			t.Fatalf("%d workers: %v", workers, err)
		}
	}
}
