package main

import (
	"runtime"
	"sync"
	"universalhost/symbols/internal/dwarf"
)

func addModelNodes(entries []*dwarf.Entry, prefix string, r *result, locations map[dwarf.Offset]uint64, versions map[dwarf.Offset]int) error {
	workers := min(4, runtime.GOMAXPROCS(0))
	return addNodesParallel(entries, prefix, r, locations, versions, workers)
}

// Entry decoding stays serial: the reader has shared DWARF state and its
// error order is observable. Only construction from immutable decoded entries
// runs concurrently, with private stacks/results and an ordered merge.
func addNodesParallel(entries []*dwarf.Entry, prefix string, r *result, locations map[dwarf.Offset]uint64, versions map[dwarf.Offset]int, workers int) error {
	if len(entries) < 4096 || workers < 2 {
		return addNodes(entries, prefix, r, locations, versions)
	}
	starts := []int{0}
	// Group small units, bounding job/result bookkeeping even when the input
	// contains hundreds of thousands of tiny compilation units.
	chunkSize := max(1024, len(entries)/(workers*4))
	for i, entry := range entries {
		if i-starts[len(starts)-1] >= chunkSize && (entry.Tag == dwarf.TagCompileUnit || entry.Tag == dwarf.TagPartialUnit || entry.Tag == dwarf.TagTypeUnit) {
			starts = append(starts, i)
		}
	}
	if len(starts) < 2 {
		return addNodes(entries, prefix, r, locations, versions)
	}
	if workers > len(starts) {
		workers = len(starts)
	}
	starts = append(starts, len(entries))
	type outcome struct {
		nodes      []*node
		err        error
		panicValue any
	}
	outcomes := make([]outcome, len(starts)-1)
	jobs := make(chan int)
	var group sync.WaitGroup
	for i := 0; i < workers; i++ {
		group.Go(func() {
			for index := range jobs {
				func() {
					defer func() { outcomes[index].panicValue = recover() }()
					local := &result{}
					outcomes[index].err = addNodes(entries[starts[index]:starts[index+1]], prefix, local, locations, versions)
					outcomes[index].nodes = local.Nodes
				}()
			}
		})
	}
	for index := range outcomes {
		jobs <- index
	}
	close(jobs)
	group.Wait()
	for _, outcome := range outcomes {
		if outcome.panicValue != nil {
			panic(outcome.panicValue)
		}
		r.Nodes = append(r.Nodes, outcome.nodes...)
		if outcome.err != nil {
			return outcome.err
		}
	}
	return nil
}
