package main

import (
	"encoding/json"
	"fmt"
	"math/rand"
	"reflect"
	"testing"
)

func TestPruningFastPathsMatchReference(t *testing.T) {
	random := rand.New(rand.NewSource(20261022))
	names := []string{"", "global", "local", "name\xff", "name\xfe"}
	for trial := 0; trial < 200; trial++ {
		r := &result{Symbols: []symbol{{Name: names[trial%len(names)]}}}
		byID := make(map[string]*node)
		for i := 0; i < 160; i++ {
			id := fmt.Sprintf("node:%d", i)
			if trial%7 == 0 && i == 159 {
				id = "node:0"
			}
			n := &node{ID: id, Tag: 0x34, Attributes: map[uint32]attribute{}}
			if random.Intn(5) == 0 {
				n.Tag = 0x13
			}
			n.HasLocation = random.Intn(2) == 0
			if random.Intn(5) == 0 {
				address := uint64(i)
				n.Location = &address
			}
			for _, key := range []uint32{3, 0x6e, 0x2001, 0x2007} {
				if random.Intn(3) == 0 {
					n.Attributes[key] = attribute{Text: names[random.Intn(len(names))]}
				}
			}
			for _, key := range []uint32{0x47, 0x31} {
				if random.Intn(3) == 0 {
					n.Attributes[key] = attribute{Ref: fmt.Sprintf("node:%d", random.Intn(161))}
				}
			}
			r.Nodes = append(r.Nodes, n)
			byID[id] = n
		}
		before, err := json.Marshal(r.Nodes)
		if err != nil {
			t.Fatal(err)
		}
		expected := referenceModelVariableRoots(r, byID)
		actual := modelVariableRoots(r, byID)
		if !reflect.DeepEqual(expected, actual) {
			t.Fatalf("trial %d roots differ: %v / %v", trial, expected, actual)
		}
		after, err := json.Marshal(r.Nodes)
		if err != nil || string(before) != string(after) {
			t.Fatal("candidate modified original nodes")
		}
	}
}

func TestPruningClearsDiscardedBackingReferences(t *testing.T) {
	address := uint64(4096)
	r := &result{Nodes: []*node{
		{ID: "keep", Tag: 0x34, Location: &address},
		{ID: "discard", Tag: 0x34, HasLocation: true},
	}}
	backing := r.Nodes
	retainModelDependencies(r)
	if len(r.Nodes) != 1 || r.Nodes[0].ID != "keep" || backing[1] != nil {
		t.Fatal("discarded nodes still reachable through backing array")
	}
}
