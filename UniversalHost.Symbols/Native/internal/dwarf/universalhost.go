// Copyright 2026 UniversalHost contributors. MIT licensed additions.
// The surrounding package is derived from Go 1.26.8 (BSD-3-Clause).
package dwarf

// UnitEntries exposes raw type-unit DIEs; Type() intentionally abstracts away
// vendor qualifiers and layout completeness, which a calibration consumer needs.
type UnitEntries struct {
	Signature  uint64
	Name       string
	TypeOffset Offset
	Entries    []*Entry
}

func (d *Data) TypeUnitEntries(limit int) ([]UnitEntries, error) {
	var result []UnitEntries
	for sig, tu := range d.typeSigs {
		b := makeBuf(d, tu, tu.name, tu.off, tu.data)
		row := UnitEntries{Signature: sig, Name: tu.name, TypeOffset: tu.base + tu.toff}
		for len(b.data) > 0 {
			e := b.entry(nil, &tu.unit)
			if b.err != nil {
				return nil, b.err
			}
			limit--
			if limit < 0 {
				return nil, DecodeError{tu.name, b.off, "DIE limit exceeded"}
			}
			row.Entries = append(row.Entries, e)
		}
		result = append(result, row)
	}
	return result, nil
}

func (r *Reader) IndexedAddress(index uint64) (uint64, error) {
	u := &r.d.unit[r.unit]
	return r.d.debugAddr(u, u.addrBase(), index)
}

func (r *Reader) UnitVersion() int { return r.d.unit[r.unit].vers }
