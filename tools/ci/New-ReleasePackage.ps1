param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$Tag,
    [string]$OutputPath = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
$release = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -Tag $Tag
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath $PackagePath
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
if ($output -eq $package -or $output.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release archive must be outside the input package.'
}
foreach ($assembly in Get-ChildItem -LiteralPath $package -Recurse -Filter 'SpaceMaid*.dll' -File) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly.FullName).ProductVersion
    if ($version -ne $release.Version -and -not ([string]$version).StartsWith($release.Version + '+', [StringComparison]::Ordinal)) {
        throw "Release version mismatch in $($assembly.Name): expected $($release.Version), found $version."
    }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) {
    throw 'The release output directory must be empty.'
}
$notes = Join-Path $output 'release-notes.md'
$versionNotes = Join-Path $repository "docs/releases/$Tag.md"
$changes = if (Test-Path -LiteralPath $versionNotes -PathType Leaf) { Get-Content -LiteralPath $versionNotes -Raw } else { '' }
(@"
$changes

Windows x64. Run **SpaceMaid-$($release.Version)-win-x64-Setup.exe**, or extract the portable ZIP and run **Space Maid.exe**.

Setup detects an existing installation and offers to update it while keeping settings and catalogs. New installations can use normal installation or a portable folder. Normal installation installs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) when needed; portable mode requires it to be installed already.
Original code is MIT licensed; dependency and imported-data terms are in the included notices.
"@).TrimStart() | Set-Content -LiteralPath $notes -Encoding utf8NoBOM
Push-Location $repository
try {
    dotnet tool restore --tool-manifest (Join-Path $repository '.config/dotnet-tools.json')
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pinned release packager.' }
    dotnet tool run vpk -- '[win]' pack --packId SpaceMaid --packTitle 'Space Maid' `
        --packVersion $release.Version --packDir $package --mainExe SpaceMaid.App.exe `
        --runtime win-x64 --channel win --framework net10.0-x64-desktop --delta None `
        --icon (Join-Path $repository 'assets/branding/space-maid-visor.ico') `
        --releaseNotes $notes --outputDir $output
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the installer and update packages.' }
}
finally { Pop-Location }
$portable = Join-Path $output 'SpaceMaid-win-Portable.zip'
$installer = Join-Path $output 'SpaceMaid-win-Setup.exe'
foreach ($file in @($portable, $installer, (Join-Path $output 'releases.win.json'), (Join-Path $output 'RELEASES'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release asset: $file" }
}
if (-not (Get-ChildItem -LiteralPath $output -File -Filter '*-full.nupkg')) { throw 'The full update package is missing.' }
$feed = Get-Content -LiteralPath (Join-Path $output 'releases.win.json') -Raw | ConvertFrom-Json
if (-not $feed.Assets) { throw 'The update feed contains no packages.' }
foreach ($asset in $feed.Assets) {
    $file = Join-Path $output $asset.FileName
    if ($asset.Version -ne $release.Version -or -not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "The update feed references an unexpected package: $($asset.FileName)"
    }
    if ((Get-Item -LiteralPath $file).Length -ne $asset.Size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.SHA256) {
        throw "The update feed checksum or size does not match: $($asset.FileName)"
    }
}
$portableDownload = Join-Path $output "SpaceMaid-$($release.Version)-win-x64.zip"
$installerDownload = Join-Path $output "SpaceMaid-$($release.Version)-win-x64-Setup.exe"
Move-Item -LiteralPath $portable -Destination $portableDownload
# The public bootstrap embeds both choices; the native installer stays internal.
$bootstrap = Join-Path $output '.bootstrap'
New-Item -ItemType Directory -Path $bootstrap | Out-Null
$nativeInstaller = Join-Path $bootstrap 'Setup.exe'
Move-Item -LiteralPath $installer -Destination $nativeInstaller
$setupHash = (Get-FileHash -LiteralPath $nativeInstaller -Algorithm SHA256).Hash
$portableHash = (Get-FileHash -LiteralPath $portableDownload -Algorithm SHA256).Hash
$updatePackage = Join-Path $output "SpaceMaid-$($release.Version)-full.nupkg"
$updateHash = (Get-FileHash -LiteralPath $updatePackage -Algorithm SHA256).Hash
Push-Location $repository
try {
    dotnet publish src/SpaceMaid.Installer/SpaceMaid.Installer.csproj -c Release -r win-x64 --self-contained true `
        -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        "-p:Version=$($release.Version)" "-p:InstallerSetupPayload=$nativeInstaller" "-p:InstallerPortablePayload=$portableDownload" `
        "-p:InstallerSetupSha256=$setupHash" "-p:InstallerPortableSha256=$portableHash" `
        "-p:InstallerUpdatePayload=$updatePackage" "-p:InstallerUpdateSha256=$updateHash" `
        --artifacts-path (Join-Path $bootstrap 'build') --output (Join-Path $bootstrap 'published') -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the installation-mode bootstrap.' }
}
finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $bootstrap 'published/SpaceMaid.Installer.exe') -Destination $installerDownload
Get-ChildItem -LiteralPath (Join-Path $bootstrap 'published') -File -Filter '*.txt' |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $output }
$assetIndex = Join-Path $output 'assets.win.json'
$assets = @(Get-Content -LiteralPath $assetIndex -Raw | ConvertFrom-Json)
foreach ($asset in $assets) {
    if ($asset.RelativeFileName -eq 'SpaceMaid-win-Portable.zip') { $asset.RelativeFileName = "SpaceMaid-$($release.Version)-win-x64.zip" }
    if ($asset.RelativeFileName -eq 'SpaceMaid-win-Setup.exe') { $asset.RelativeFileName = "SpaceMaid-$($release.Version)-win-x64-Setup.exe" }
    if (-not (Test-Path -LiteralPath (Join-Path $output $asset.RelativeFileName) -PathType Leaf)) {
        throw "The release asset index references a missing file: $($asset.RelativeFileName)"
    }
}
ConvertTo-Json -InputObject $assets | Set-Content -LiteralPath $assetIndex -Encoding utf8NoBOM
foreach ($asset in Get-ChildItem -LiteralPath $output -File | Where-Object Extension -in '.zip', '.exe', '.nupkg') {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($asset.FullName + '.sha256', "$hash  $($asset.Name)`n", [Text.UTF8Encoding]::new($false))
}
Write-Host 'Prepared installer, portable app, update feed and SHA-256 checksums.'
