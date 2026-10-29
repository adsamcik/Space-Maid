[CmdletBinding()]
param([string]$Repository = (Join-Path $PSScriptRoot '..'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = (Resolve-Path -LiteralPath $Repository).Path
$roots = @('src', 'tools/SpaceMaid.Bench')
$violations = [Collections.Generic.List[string]]::new()
$checked = 0

# This is an architecture audit, not a replacement for behavioral tests or a
# C# parser. Include untracked source too, so a new bypass cannot hide in a new file.
foreach ($relativeRoot in $roots) {
    $sourceRoot = Join-Path $repositoryRoot $relativeRoot
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs') {
        $relative = [IO.Path]::GetRelativePath($repositoryRoot, $file.FullName).Replace('\', '/')
        if ($relative -cmatch '/(?:bin|obj)/' -or $relative.StartsWith('src/SpaceMaid.Core/IO/')) { continue }
        $checked++
        $lineNumber = 0
        foreach ($line in [IO.File]::ReadLines($file.FullName)) {
            $lineNumber++
            if ($line -cmatch '^\s*//') { continue }
            $reason = $null
            if ($line -cmatch '(?<![\w.])(?:global::)?(?:System\.IO\.)?(?:File|Directory)\.(?:Exists|Open\w*|Read\w*|Write\w*|Append\w*|Copy|Move|Delete|Create\w*|Enumerate\w*|Get\w*|Set\w*|ResolveLinkTarget|CreateSymbolicLink)\b' -or
                $line -cmatch '(?<![\w.])(?:global::)?(?:System\.IO\.)?(?:DriveInfo|RandomAccess)\.\w+\b' -or
                $line -cmatch '\bnew\s+(?:global::)?(?:System\.IO\.(?:Enumeration\.)?)?(?:FileStream|FileInfo|DirectoryInfo|DriveInfo|FileSystemEnumerable)\b') {
                $reason = 'use DiskAccess for filesystem primitives'
            }
            elseif ($line -cmatch '^\s*using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?System\.IO\.(?:File|Directory|DriveInfo|RandomAccess|FileStream|FileInfo|DirectoryInfo)(?:\s*;|\.)' -or
                $line -cmatch '^\s*using\s+\w+\s*=\s*(?:global::)?System\.IO\s*;') {
                $reason = 'filesystem aliases/static imports must not bypass DiskAccess'
            }
            elseif ($line -cmatch '\bextern\b|\bpartial\b' -and
                $line -cmatch '\b(?:CreateFileW?|GetFileInformationByHandle(?:Ex)?|SetFileInformationByHandle|SetFileTime|GetVolumeInformationW?|GetVolumeNameForVolumeMountPointW?|GetDiskFreeSpaceW?|DeviceIoControl)\s*\(') {
                $reason = 'native file primitives belong in Core/IO'
            }
            elseif ($line -cmatch 'EntryPoint\s*=\s*"(?:CreateFileW?|GetFileInformationByHandle(?:Ex)?|SetFileInformationByHandle|SetFileTime|GetVolumeInformationW?|GetVolumeNameForVolumeMountPointW?|GetDiskFreeSpaceW?|DeviceIoControl)"' -and
                # This one import opens only an inheritable named-pipe endpoint
                # for the isolated worker; its callers and flags remain reviewed.
                $relative -ne 'src/SpaceMaid.LocalModel/WindowsSandboxNative.cs') {
                $reason = 'renamed native file imports must not bypass Core/IO'
            }
            elseif ($line -cmatch '\bnew\s+VolumeScanner\s*\(' -and $relative -ne 'src/SpaceMaid.Core/Scanning/VolumeScanner.cs') {
                $reason = 'request scan/refresh operations through DiskAccess'
            }
            elseif ($relative.StartsWith('src/SpaceMaid.Extensions.') -or $relative.StartsWith('src/SpaceMaid.App/Extensions/')) {
                if ($line -cmatch '\bDiskAccess\.Default\s*\.') { $reason = 'pass host.Files into extension operations and brokers' }
            }
            if ($reason) { $violations.Add("${relative}:${lineNumber}: $reason`n  $($line.Trim())") }
        }
    }
}
if ($violations.Count) { throw "File access boundary violations ($($violations.Count)):`n$($violations -join "`n")" }
Write-Host "Verified disk access boundary across $checked production C# files."
