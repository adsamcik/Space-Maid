param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'
# SemVer release tags, without build metadata; .NET assembly components are UInt16.
if ($Tag -cnotmatch '\Av(?<core>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?<pre>-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z') {
    throw 'Use a release tag such as v1.2.3 or v1.2.3-beta.1.'
}
$core = $Matches.core
$prerelease = $Matches.pre
foreach ($component in $core.Split('.')) {
    if ($component.Length -gt 5 -or [int]$component -gt 65534) {
        throw 'Release version components must be at most 65534 for .NET assembly versioning.'
    }
}
if ($prerelease) {
    foreach ($identifier in $prerelease.Substring(1).Split('.')) {
        if ($identifier -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers cannot have leading zeros.' }
    }
}
[pscustomobject]@{ Version = $Tag.Substring(1); Prerelease = [bool]$prerelease }
