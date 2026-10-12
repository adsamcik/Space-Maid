param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$required = @(
    'SpaceMaid.App.exe', 'SpaceMaid.App.dll', 'SpaceMaid.Core.dll', 'SpaceMaid.Extensions.Contracts.dll',
    'LICENSE', 'THIRD_PARTY_NOTICES.md', 'Notices/FileFormatRegistry/README.md', 'Notices/FileFormatRegistry/FileFormatRegistry.provenance.json.gz',
    'ScanHelper/SpaceMaid.ScanHelper.exe', 'ScanHelper/SpaceMaid.Core.dll',
    'Extensions/space-maid.photos/extension.json', 'Extensions/space-maid.photos/SpaceMaid.Extensions.Photos.dll',
    'Extensions/space-maid.photos/e_sqlite3.dll',
    'Extensions/space-maid.apps/extension.json', 'Extensions/space-maid.apps/SpaceMaid.Extensions.Apps.dll',
    'Extensions/space-maid.developer/extension.json', 'Extensions/space-maid.developer/SpaceMaid.Extensions.Developer.dll',
    'Extensions/space-maid.assistant/extension.json', 'Extensions/space-maid.assistant/SpaceMaid.Extensions.Assistant.dll',
    'Extensions/space-maid.assistant/Worker/SpaceMaid.ModelWorker.exe',
    'Extensions/space-maid.assistant/Worker/coreclr.dll',
    'Extensions/space-maid.assistant/Worker/Runtime-LICENSE.TXT',
    'Extensions/space-maid.assistant/Worker/Runtime-THIRD-PARTY-NOTICES.TXT'
)
foreach ($relative in $required) {
    $file = Join-Path $package $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Missing or empty package asset: $relative"
    }
}
foreach ($relative in @('SpaceMaid.App.exe', 'ScanHelper/SpaceMaid.ScanHelper.exe',
        'Extensions/space-maid.assistant/Worker/SpaceMaid.ModelWorker.exe')) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead((Join-Path $package $relative)))
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a Windows executable: $relative" }
        $reader.BaseStream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "Not a Windows x64 executable: $relative"
        }
    }
    finally { $reader.Dispose() }
}
if (Test-Path -LiteralPath (Join-Path $package 'extension.json')) { throw 'An extension manifest leaked into the app root.' }
foreach ($relative in @('SpaceMaid.ModelWorker.exe', 'SpaceMaid.ScanHelper.exe')) {
    if (Test-Path -LiteralPath (Join-Path $package $relative)) { throw "An isolated component leaked into the app root: $relative" }
}
$worker = Join-Path $package 'Extensions/space-maid.assistant/Worker'
$runtime = Get-Content -LiteralPath (Join-Path $worker 'SpaceMaid.ModelWorker.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtime.runtimeOptions.includedFrameworks) { throw 'The isolated worker must remain self-contained.' }
if (-not (Get-ChildItem -LiteralPath $worker -Recurse -Filter 'llama.dll')) { throw 'The CPU worker native backend is missing.' }
Write-Host "Verified $($required.Count) required package assets, Windows x64 executables and isolated worker runtime."
