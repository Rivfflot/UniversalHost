package main

import (
	"encoding/binary"
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
