[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Filter,
    [ValidateSet('Unit', 'System')][string]$Stage = 'Unit',
    [switch]$Desktop,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$ListTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($Filter)) { throw 'Select the affected test class or method with -Filter.' }
if ($Desktop -and $Stage -ne 'System') { throw '-Desktop requires -Stage System.' }

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stageFilter = if ($Stage -eq 'Unit') { 'Stage!=System' } else { 'Stage=System' }
if ($Stage -eq 'System') {
    # WPF pack resources require desktop and other tests in separate hosts.
    $desktopFilter = if ($Desktop) { 'FullyQualifiedName~SpaceMaid.Tests.DesktopTests' } else { 'FullyQualifiedName!~SpaceMaid.Tests.DesktopTests' }
    $stageFilter = "($stageFilter)&($desktopFilter)"
}
$testArguments = @('test', 'tests/SpaceMaid.Tests/SpaceMaid.Tests.csproj', '-c', $Configuration,
    '--settings', (Join-Path $PSScriptRoot "testing/$Stage.runsettings"),
    '--filter', "($stageFilter)&($Filter)")
if ($NoBuild) { $testArguments += '--no-build' }
if ($ListTests) { $testArguments += '--list-tests' }

Push-Location -LiteralPath $repository
try {
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw "The $Stage test run failed (exit $LASTEXITCODE). Check the filter and test output." }
}
finally { Pop-Location }
