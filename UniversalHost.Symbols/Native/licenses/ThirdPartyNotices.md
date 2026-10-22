# Firmware parser third-party notices

The `symbols-worker` executable contains Go 1.26.8 runtime and standard-library
code, including ELF decoding, and a source snapshot of `debug/dwarf` from Go
1.26.8. These components are licensed under the Go Authors' BSD-style license,
not UniversalHost's MIT license. See `Go-LICENSE` and `Go-PATENTS` in this folder.

Upstream: https://go.dev/dl/ and https://go.googlesource.com/go/+/refs/tags/go1.26.8/src/debug/dwarf/

Build toolchain: Go 1.26.8, `CGO_ENABLED=0`, no third-party Go modules, no bundled
compiler or system runtime. The Windows amd64 SDK archive was obtained from
https://go.dev/dl/go1.26.8.windows-amd64.zip and verified with SHA-256
`b92c3b2adae85a11ba71fe7216daf0d84e82af4c8ab6c5625807f28622043a59`.

The upstream DWARF files are unmodified; `upstream-sha256.json` records their
hashes. `internal/dwarf/universalhost.go` is an additional MIT-licensed adapter
exposing raw DWARF 4 type-unit entries and indexed address resolution. Container
adaptation, the worker JSON protocol and UniversalHost C# code are MIT-licensed.

The Go toolchain is needed only by maintainers rebuilding the parser with
`build/Build-SymbolWorkers.ps1`. Application builds and publishes use the versioned
prebuilt executables and SHA-256 files in `Native/dist/`, without invoking Go.
End users receive the
matching executable inside `symbol-tools/<RID>/` and need no Go, CCS or Python
installation, PATH customization, or first-run network download.

TI ofd2000 and TI compiler binaries are NOT distributed with UniversalHost.
The TI 22.6.1.LTS tools were used only for independent, local verification probes.
