param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$Tag,
    [string]$OutputPath = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
$release = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -Tag $Tag
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath $PackagePath
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
if ($output -eq $package -or $output.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release archive must be outside the input package.'
}
foreach ($assembly in Get-ChildItem -LiteralPath $package -Recurse -Filter 'SpaceMaid*.dll' -File) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly.FullName).ProductVersion
    if ($version -ne $release.Version -and -not $version.StartsWith($release.Version + '+', [StringComparison]::Ordinal)) {
        throw "Release version mismatch in $($assembly.Name): expected $($release.Version), found $version."
    }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zip = Join-Path $output "SpaceMaid-$($release.Version)-win-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($zip + '.sha256', "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
@'
Windows x64. Extract the ZIP and run **SpaceMaid.App.exe**.

Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
Original code is MIT licensed; dependency and imported-data terms are in the included notices.
'@ | Set-Content -LiteralPath (Join-Path $output 'release-notes.md') -Encoding utf8NoBOM
Write-Host "Prepared $([IO.Path]::GetFileName($zip)) and SHA-256 checksum."
