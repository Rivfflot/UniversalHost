# Maintainer-only: ordinary dotnet build/publish consumes the versioned binaries.
[CmdletBinding()]
param([string]$GoExe)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$nativeRoot = Join-Path $repoRoot 'UniversalHost.Symbols/Native'
if (!$GoExe) {
    $localGo = Join-Path $repoRoot $(if ($IsWindows) { '.tools/go/bin/go.exe' } else { '.tools/go/bin/go' })
    $GoExe = if (Test-Path -LiteralPath $localGo) { $localGo } else { 'go' }
}
$GoExe = (Get-Command $GoExe -CommandType Application -ErrorAction Stop).Source
$savedEnvironment = @{}
foreach ($name in @('GOOS','GOARCH','CGO_ENABLED','GOTOOLCHAIN','GOCACHE','GOFLAGS','GOAMD64','GOARM64')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location $nativeRoot
try {
    $env:GOTOOLCHAIN = 'local'
    $version = (& $GoExe version) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $version -notmatch 'go1\.26\.8 ') { throw 'Requires pinned Go 1.26.8; specify -GoExe if needed.' }
    $env:CGO_ENABLED = '0'
    $env:GOCACHE = Join-Path $repoRoot '.tools/gocache'
    $env:GOFLAGS = ''
    $env:GOAMD64 = 'v1'
    $env:GOARM64 = 'v8.0'
    # Tests run for the build host before selecting any cross-compilation target.
    $env:GOOS = (& $GoExe env GOHOSTOS).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine Go host OS' }
    $env:GOARCH = (& $GoExe env GOHOSTARCH).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine Go host architecture' }
    & $GoExe test ./...
    if ($LASTEXITCODE -ne 0) { throw 'Go regression tests failed' }
    $artifacts = @()
    foreach ($rid in @('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')) {
        $env:GOOS = if ($rid.StartsWith('win-')) { 'windows' } elseif ($rid.StartsWith('osx-')) { 'darwin' } else { 'linux' }
        $env:GOARCH = if ($rid.EndsWith('x64')) { 'amd64' } else { 'arm64' }
        $directory = Join-Path $nativeRoot "dist/$rid"
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $fileName = if ($env:GOOS -eq 'windows') { 'symbols-worker.exe' } else { 'symbols-worker' }
        $binary = Join-Path $directory $fileName
        $temporary = "$binary.pending"
        try {
            & $GoExe build -trimpath -buildvcs=false '-ldflags=-s -w' -o $temporary .
            if ($LASTEXITCODE -ne 0) { throw "Go build failed for $rid" }
            Move-Item -LiteralPath $temporary -Destination $binary -Force
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
        }
        $hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
        Set-Content -LiteralPath "$binary.sha256" -Value $hash -Encoding utf8NoBOM
        $artifacts += [ordered]@{ rid = $rid; file = "$rid/$fileName"; sha256 = $hash }
        Write-Output "Built $rid : $hash"
    }
    $sources = @(Get-ChildItem -LiteralPath $nativeRoot -Recurse -File | Where-Object {
        $_.Extension -eq '.go' -or $_.Name -eq 'go.mod'
    } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ file = [IO.Path]::GetRelativePath($nativeRoot, $_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{ goVersion = '1.26.8'; cgoEnabled = $false; schema = 1; sources = $sources; artifacts = $artifacts } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $nativeRoot 'dist/manifest.json') -Encoding utf8NoBOM
} finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
