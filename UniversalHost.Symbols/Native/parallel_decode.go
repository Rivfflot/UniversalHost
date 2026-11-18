package main

import (
	"sync"
	"sync/atomic"
	"universalhost/symbols/internal/dwarf"
)

type decodedUnit struct {
	entries   []*dwarf.Entry
	locations map[dwarf.Offset]uint64
	version   int
}

func decodeUnits(d *dwarf.Data, workers int) ([]decodedUnit, bool) {
	unitCount := d.UnitCount()
	if workers < 2 || unitCount < workers || unitCount > maxDies {
		return nil, false
	}
	units := make([]decodedUnit, unitCount)
	var next, count atomic.Int64
	var failed atomic.Bool
	var group sync.WaitGroup
	for range workers {
		group.Go(func() {
			defer func() {
				if recover() != nil {
					failed.Store(true)
				}
			}()
			for !failed.Load() {
				index := int(next.Add(1)) - 1
				if index >= len(units) {
					return
				}
				r := d.UnitReader(index)
				unit := &units[index]
				unit.version = r.UnitVersion()
				for !failed.Load() {
					e, err := r.Next()
					if err != nil {
						failed.Store(true)
						return
					}
					if e == nil {
						break
					}
					if count.Add(1) > maxDies {
						failed.Store(true)
						return
					}
					unit.entries = append(unit.entries, e)
					if address, ok := fixedVariableAddress(e, r); ok {
						if unit.locations == nil {
							unit.locations = make(map[dwarf.Offset]uint64)
						}
						unit.locations[e.Offset] = address
					}
				}
			}
		})
	}
	group.Wait()
	if failed.Load() {
		// Let the serial reader report the first error in file order.
		return nil, false
	}
	return units, true
}
