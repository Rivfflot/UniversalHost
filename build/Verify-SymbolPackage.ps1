param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Rid,
    [switch]$RunWorker,
    [string]$Sample,
    [string]$GoExe
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$dependencyRoot = Join-Path $root 'dependencies'
$toolRoot = Join-Path $dependencyRoot 'symbol-tools'
$name = if ($Rid.StartsWith('win-')) { 'symbols-worker.exe' } else { 'symbols-worker' }
$tool = Join-Path (Join-Path $toolRoot $Rid) $name
if (!(Test-Path -LiteralPath $tool)) { throw "Missing worker for $Rid" }
$others = @(Get-ChildItem -LiteralPath $toolRoot -Directory | Where-Object { $_.Name -ne $Rid -and $_.Name -ne 'licenses' })
if ($others.Count) { throw "Package contains other host tools: $($others.Name -join ', ')" }
foreach ($auditFile in @("$tool.sha256", (Join-Path $toolRoot 'licenses/upstream-sha256.json'))) {
    if (Test-Path -LiteralPath $auditFile) { throw "Package contains repository-only audit file: $auditFile" }
}
foreach ($license in @('Go-LICENSE','Go-PATENTS','ThirdPartyNotices.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $toolRoot "licenses/$license"))) { throw "Missing license/source notice: $license" }
}
$bytes = [IO.File]::ReadAllBytes($tool)
$architecture = ''
if ($Rid.StartsWith('win-')) {
    if ($bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw 'Worker is not PE' }
    $pe = [BitConverter]::ToInt32($bytes,0x3c)
    $machine = [BitConverter]::ToUInt16($bytes,$pe+4)
    $expected = if ($Rid.EndsWith('x64')) { 0x8664 } else { 0xaa64 }
    if ($machine -ne $expected) { throw "Wrong PE machine: $machine" }
    $architecture = "PE machine 0x$($machine.ToString('X'))"
    $optional = $pe+24
    if ([BitConverter]::ToUInt16($bytes,$optional) -ne 0x20b) { throw 'Worker must be PE32+' }
    $sectionStart = $optional+[BitConverter]::ToUInt16($bytes,$pe+20)
    $sectionCount = [BitConverter]::ToUInt16($bytes,$pe+6)
    function RvaOffset([uint32]$rva) {
        for ($sectionIndex=0;$sectionIndex -lt $sectionCount;$sectionIndex++) {
            $sectionAt=$sectionStart+40*$sectionIndex
            $base=[BitConverter]::ToUInt32($bytes,$sectionAt+12)
            $size=[Math]::Max([BitConverter]::ToUInt32($bytes,$sectionAt+8),[BitConverter]::ToUInt32($bytes,$sectionAt+16))
            if ($rva -ge $base -and $rva -lt $base+$size) {
                return [int]($rva-$base+[BitConverter]::ToUInt32($bytes,$sectionAt+20))
            }
        }
        throw "Invalid PE import RVA: $rva"
    }
    $dependencies=@()
    $importRva=[BitConverter]::ToUInt32($bytes,$optional+120)
    if ($importRva) {
        $descriptor=RvaOffset $importRva
        while (($nameRva=[BitConverter]::ToUInt32($bytes,$descriptor+12)) -ne 0) {
            $start=RvaOffset $nameRva
            $end=$start
            while ($bytes[$end] -ne 0) { $end++ }
            $dependency=[Text.Encoding]::ASCII.GetString($bytes,$start,$end-$start)
            if ($dependency.ToLowerInvariant() -notin @('kernel32.dll','ntdll.dll','ws2_32.dll','advapi32.dll','bcrypt.dll','user32.dll')) {
                throw "Unbundled non-system Windows dependency: $dependency"
            }
            $dependencies+=$dependency
            $descriptor+=20
        }
    }
    $architecture += "; system dependencies: $($dependencies -join ', ')"
} elseif ($Rid.StartsWith('linux-')) {
    if ($bytes[0] -ne 0x7f -or [Text.Encoding]::ASCII.GetString($bytes,1,3) -ne 'ELF') { throw 'Worker is not ELF' }
    $machine = [BitConverter]::ToUInt16($bytes,18)
    $expected = if ($Rid.EndsWith('x64')) { 62 } else { 183 }
    if ($machine -ne $expected -or $bytes[4] -ne 2) { throw "Wrong ELF class/machine: $machine" }
    # CGO_ENABLED=0 must produce no interpreter (PT_INTERP) or dynamic segment.
    $offset = [BitConverter]::ToUInt64($bytes,32)
    $entry = [BitConverter]::ToUInt16($bytes,54)
    $count = [BitConverter]::ToUInt16($bytes,56)
    for ($i=0;$i -lt $count;$i++) {
        $type = [BitConverter]::ToUInt32($bytes,[int]($offset+$i*$entry))
        if ($type -in @(2,3)) { throw 'Go worker unexpectedly requires ELF dynamic libraries' }
    }
    $architecture = "ELF64 machine $machine, no dynamic interpreter/dependencies"
} else {
    if ([BitConverter]::ToUInt32($bytes,0) -ne [Convert]::ToUInt32('feedfacf',16)) { throw 'Worker is not Mach-O 64' }
    $cpu = [BitConverter]::ToUInt32($bytes,4)
    $expected = if ($Rid.EndsWith('x64')) { 0x01000007 } else { 0x0100000c }
    if ($cpu -ne $expected) { throw "Wrong Mach-O CPU: $cpu" }
    $architecture = "Mach-O64 CPU 0x$($cpu.ToString('X'))"
    $commandCount = [BitConverter]::ToUInt32($bytes,16)
    $offset = 32
    $dependencies = @()
    for ($i=0;$i -lt $commandCount;$i++) {
        $command = [BitConverter]::ToUInt32($bytes,$offset)
        $length = [BitConverter]::ToUInt32($bytes,$offset+4)
        if ($length -lt 8 -or $offset+$length -gt $bytes.Length) { throw 'Invalid Mach-O load command' }
        if (($command -band 0x7fffffff) -in @(12,24,31,35)) {
            $nameOffset = $offset + [BitConverter]::ToUInt32($bytes,$offset+8)
            $end = $nameOffset
            while ($bytes[$end] -ne 0 -and $end -lt $offset+$length) { $end++ }
            $dependency = [Text.Encoding]::UTF8.GetString($bytes,$nameOffset,$end-$nameOffset)
            if (!$dependency.StartsWith('/usr/lib/') -and !$dependency.StartsWith('/System/Library/')) {
                throw "Unbundled non-system macOS dependency: $dependency"
            }
            $dependencies += $dependency
        }
        $offset += $length
    }
    $architecture += "; system dependencies: $($dependencies -join ', ')"
}
# Go build information is embedded as plain text; package verification needs no Go SDK.
$buildInfo = [Text.Encoding]::UTF8.GetString($bytes)
if ($GoExe) {
    $buildInfo = (& $GoExe version -m $tool) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read Go build metadata' }
}
if ($buildInfo -notmatch 'go1\.26\.8(?:\x00|\s)' -or $buildInfo -notmatch 'CGO_ENABLED=0') {
    throw 'Worker version or CGO setting does not match the pinned build'
}
$goOs = if ($Rid.StartsWith('win-')) { 'windows' } elseif ($Rid.StartsWith('osx-')) { 'darwin' } else { 'linux' }
$goArch = if ($Rid.EndsWith('x64')) { 'amd64' } else { 'arm64' }
if ($buildInfo -notmatch "GOOS=$goOs" -or $buildInfo -notmatch "GOARCH=$goArch") { throw 'Go build metadata disagrees with RID' }
if (!$IsWindows -and !$Rid.StartsWith('win-')) {
    $mode = [IO.File]::GetUnixFileMode($tool)
    if (($mode -band [IO.UnixFileMode]::UserExecute) -eq 0) { throw 'Worker lost executable permission' }
}
$hash = (Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumFile = Join-Path $PSScriptRoot "../UniversalHost.Symbols/Native/dist/$Rid/$name.sha256"
if (!(Test-Path -LiteralPath $checksumFile)) { throw 'Missing worker SHA256 checksum' }
$expectedHash = (Get-Content -LiteralPath $checksumFile -Raw).Trim()
if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$' -or $hash -ne $expectedHash) { throw 'Worker SHA256 mismatch' }
$converterName = if ($Rid.StartsWith('win-')) { 'UniversalHost.ProjectConverter.exe' } else { 'UniversalHost.ProjectConverter' }
$converter = Join-Path $dependencyRoot $converterName
if (!(Test-Path -LiteralPath $converter)) { throw 'Missing project converter executable' }
foreach ($legacyDirectory in @('project-converter', 'project-tools', 'symbol-tools')) {
    if (Test-Path -LiteralPath (Join-Path $root $legacyDirectory)) { throw "Obsolete dependency directory remains: $legacyDirectory" }
}
$toolDirectories = @(Get-ChildItem -LiteralPath $root -Directory -Recurse -Filter 'symbol-tools')
if ($toolDirectories.Count -ne 1 -or $toolDirectories[0].FullName -ne $toolRoot) {
    throw 'Package must contain exactly one shared symbol-tools directory under dependencies'
}
$dependencyFiles = @(Get-ChildItem -LiteralPath $dependencyRoot -File -Recurse)
$expectedDependencyFiles = @($converterName, "symbol-tools/$Rid/$name", 'symbol-tools/licenses/Go-LICENSE',
    'symbol-tools/licenses/Go-PATENTS', 'symbol-tools/licenses/ThirdPartyNotices.md')
if ($dependencyFiles.Count -ne $expectedDependencyFiles.Count) { throw 'Unexpected or duplicated dependency files' }
foreach ($file in $dependencyFiles) {
    if ([IO.Path]::GetRelativePath($dependencyRoot, $file.FullName).Replace('\', '/') -notin $expectedDependencyFiles) {
        throw "Unexpected dependency file: $($file.FullName)"
    }
}
if (!$IsWindows -and !$Rid.StartsWith('win-')) {
    foreach ($file in @($converter)) {
        if (([IO.File]::GetUnixFileMode($file) -band [IO.UnixFileMode]::UserExecute) -eq 0) { throw "Converter file lost executable permission: $file" }
    }
}
Write-Output "$Rid : $architecture; Go 1.26.8 CGO=0; SHA256=$hash; shared dependencies and licenses OK"
if ($RunWorker) {
    $processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $osName = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { 'unknown' }
    if ("$osName-$processArchitecture" -ne $Rid) { throw 'Refusing to claim native validation on a different host RID' }
    $converterVersion = & $converter --version
    if ($LASTEXITCODE -ne 0 -or ($converterVersion | ConvertFrom-Json).FormatVersion -lt 1) { throw 'Converter startup/version contract failed' }
    & $tool 2> $null
    if ($LASTEXITCODE -ne 2) { throw 'Worker startup/usage contract failed' }
    if ($Sample) {
        $text = & $tool (Resolve-Path -LiteralPath $Sample).Path
        if ($LASTEXITCODE -ne 0) { throw 'Worker sample parsing failed' }
        $parsed = $text | ConvertFrom-Json
        if ($parsed.Schema -ne 1 -or !$parsed.TypesComplete) { throw 'Worker sample has no complete types' }
        Write-Output "Native parsing OK: $($parsed.Platform), $($parsed.Symbols.Count) symbols, DWARF $($parsed.DwarfVersions -join ',')"
    }
}
