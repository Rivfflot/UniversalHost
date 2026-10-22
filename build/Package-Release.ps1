param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$archivePath = [IO.Path]::GetFullPath($OutputFile)
if ($archivePath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The archive must be outside the publish directory'
}
& "$PSScriptRoot/Verify-SymbolPackage.ps1" -PublishDirectory $root -Rid $Rid
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archivePath)) | Out-Null
$names = [Collections.Generic.HashSet[string]]::new()
$workerName = if ($Rid.StartsWith('win-')) { 'symbols-worker.exe' } else { 'symbols-worker' }
$workerEntry = "symbol-tools/$Rid/$workerName"
$expectedHash = (Get-FileHash -LiteralPath (Join-Path $root $workerEntry) -Algorithm SHA256).Hash
$archivedHash = ''
if ($Rid.StartsWith('win-')) {
    Compress-Archive -Path "$root/*" -DestinationPath $archivePath -Force
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $zip.Entries) {
            $names.Add($entry.FullName.Replace('\','/')) | Out-Null
            if ($entry.FullName.Replace('\','/') -eq $workerEntry) {
                $stream = $entry.Open()
                try { $archivedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
                finally { $stream.Dispose() }
            }
        }
    } finally { $zip.Dispose() }
} else {
    # Write POSIX permissions explicitly, including when packaging on Windows.
    $file = [IO.File]::Create($archivePath)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
    $writer = [System.Formats.Tar.TarWriter]::new($gzip)
    try {
        foreach ($item in Get-ChildItem -LiteralPath $root -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\','/')
            $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $relative)
            $executable = $relative -eq 'UniversalHost' -or $relative -eq "symbol-tools/$Rid/symbols-worker"
            $entry.Mode = [IO.UnixFileMode]$(if ($executable) { 493 } else { 420 }) # 0755 / 0644
            $entry.ModificationTime = $item.LastWriteTimeUtc
            $entry.DataStream = [IO.File]::OpenRead($item.FullName)
            try { $writer.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
        }
    } finally { $writer.Dispose(); $gzip.Dispose(); $file.Dispose() }
    $file = [IO.File]::OpenRead($archivePath)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
    $reader = [System.Formats.Tar.TarReader]::new($gzip)
    $executables = 0
    try {
        while ($null -ne ($entry = $reader.GetNextEntry())) {
            $names.Add($entry.Name) | Out-Null
            if ($entry.Name -eq $workerEntry) {
                $archivedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.DataStream))
            }
            if ($entry.Name -eq 'UniversalHost' -or $entry.Name -eq "symbol-tools/$Rid/symbols-worker") {
                if ([int]$entry.Mode -ne 493) { throw "Wrong executable archive permissions: $($entry.Name)" }
                $executables++
            }
        }
    } finally { $reader.Dispose(); $gzip.Dispose(); $file.Dispose() }
    if ($executables -ne 2) { throw 'Archive lacks the application or parser executable' }
}
if ($archivedHash -ne $expectedHash) { throw 'Archived worker differs from the verified publish output' }
foreach ($license in @('Go-LICENSE','Go-PATENTS','ThirdPartyNotices.md')) {
    if (!$names.Contains("symbol-tools/licenses/$license")) { throw "Archive lacks license: $license" }
}
Write-Output 'Archive worker hash, licenses and executable entries verified'
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256 | Format-List
