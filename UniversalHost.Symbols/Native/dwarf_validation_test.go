package main

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"strings"
	"testing"
)

func dwarfTestSections(body []byte) []section {
	info := make([]byte, 11, 11+len(body))
	binary.LittleEndian.PutUint32(info, uint32(7+len(body)))
	binary.LittleEndian.PutUint16(info[4:], 4)
	info[10] = 4
	info = append(info, body...)
	return []section{{".debug_info", info}, {".debug_abbrev", []byte{1, 0x11, 1, 0, 0, 2, 0x13, 1, 0, 0, 0}}}
}

func TestDwarfDecodeAndResourceErrorPrecedence(t *testing.T) {
	deep := []byte{1}
	for i := 0; i < 128; i++ {
		deep = append(deep, 2)
	}
	for _, tc := range []struct {
		name string
		body []byte
		want string
	}{
		{"nesting", append(append([]byte{}, deep...), 0), "DIE nesting exceeds 128"},
		{"decode-before-construction", append(append([]byte{}, deep...), 99), "unknown abbreviation"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			err := readDwarf(dwarfTestSections(tc.body), &result{Signatures: map[string]string{}})
			if err == nil || !strings.Contains(err.Error(), tc.want) {
				t.Fatalf("got %v, want %s", err, tc.want)
			}
		})
	}
	for _, tc := range []struct {
		name   string
		length int
		last   byte
		want   string
	}{
		{"exact-count", maxDies, 0, ""},
		{"over-count", maxDies + 1, 0, "DIE count exceeds 500000"},
		{"decode-before-count", maxDies + 1, 99, "unknown abbreviation"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			body := make([]byte, tc.length)
			body[0] = 1
			body[len(body)-1] = tc.last
			err := readDwarf(dwarfTestSections(body), &result{Signatures: map[string]string{}})
			if tc.want == "" {
				if err != nil {
					t.Fatal(err)
				}
				return
			}
			if err == nil || !strings.Contains(err.Error(), tc.want) {
				t.Fatalf("got %v, want %s", err, tc.want)
			}
		})
	}
}

func TestParallelDwarfDecodePreservesResultsAndErrors(t *testing.T) {
	normal := []byte{1, 2, 0, 0}
	deep := []byte{1}
	for range 128 {
		deep = append(deep, 2)
	}
	corrupt := []byte{1, 99}
	full := make([]byte, maxDies)
	full[0] = 1

	for _, tc := range []struct {
		name   string
		bodies [][]byte
	}{
		{"valid", [][]byte{normal, normal, normal, normal}},
		{"decode-before-nesting", [][]byte{deep, corrupt, normal, normal}},
		{"ordered-invalid", [][]byte{normal, corrupt, []byte{99}, normal}},
		{"count-before-later-decoder-error", [][]byte{full, normal, corrupt, normal}},
		{"limit-plus-corrupt-die", [][]byte{full, corrupt, normal, normal}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			sections := dwarfTestSections(tc.bodies[0])
			// Include DWARF 4 and 5 units in the same section.
			for i, body := range tc.bodies[1:] {
				if i%2 == 0 {
					info := make([]byte, 12)
					binary.LittleEndian.PutUint32(info, uint32(8+len(body)))
					binary.LittleEndian.PutUint16(info[4:], 5)
					info[6], info[7] = 1, 4
					sections[0].data = append(sections[0].data, info...)
					sections[0].data = append(sections[0].data, body...)
				} else {
					sections[0].data = append(sections[0].data, dwarfTestSections(body)[0].data...)
				}
			}
			run := func(workers int) (string, string) {
				data := &result{Signatures: map[string]string{}}
				err := readDwarfWithWorkers(sections, data, workers)
				if err != nil {
					return "", fmt.Sprint(err)
				}
				retainModelDependencies(data)
				raw, err := json.Marshal(data)
				if err != nil {
					t.Fatal(err)
				}
				return string(raw), ""
			}
			want, wantErr := run(1)
			for _, workers := range []int{2, 4} {
				got, gotErr := run(workers)
				if got != want || gotErr != wantErr {
					t.Fatalf("workers=%d, serial error=%q, experimental error=%q, same result=%t", workers, wantErr, gotErr, got == want)
				}
			}
		})
	}
}
