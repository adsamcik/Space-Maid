param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$WorkPath,
    [Parameter(Mandatory)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work = [IO.Path]::GetFullPath($WorkPath)
$destinationFile = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $work) { throw 'Use a new working directory for the portable EXE.' }
if (Test-Path -LiteralPath $destinationFile) { throw 'The portable EXE destination already exists.' }

$package = Join-Path $work 'app'
$appBuild = Join-Path $work 'app-build'
$helperBuild = Join-Path $work 'helper-build'
$published = Join-Path $work 'launcher'
Push-Location $repository
try {
    dotnet publish src/SpaceMaid.App/SpaceMaid.App.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true `
        -p:SpaceMaidPortableExe=true "-p:Version=$Version" --artifacts-path $appBuild --output $package -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish the self-contained portable app.' }

    $helpers = @{
        ScanHelper = 'ScanHelper'
        DiskHelper = 'Extensions/space-maid.containers/DiskHelper'
    }
    foreach ($helper in $helpers.Keys) {
        dotnet publish "src/SpaceMaid.$helper/SpaceMaid.$helper.csproj" -c Release -r win-x64 --self-contained true `
            -p:EnableWindowsTargeting=true "-p:Version=$Version" --artifacts-path $helperBuild `
            --output (Join-Path $package $helpers[$helper]) -m:1
        if ($LASTEXITCODE -ne 0) { throw "Could not publish the self-contained $helper." }
    }
    & (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath $package -SelfContained

    $payload = Join-Path $work 'Portable.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    dotnet publish src/SpaceMaid.Installer/SpaceMaid.Installer.csproj -c Release -r win-x64 --self-contained true `
        -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:InstallerPortableLauncher=true "-p:Version=$Version" "-p:InstallerPortablePayload=$payload" "-p:InstallerPortableSha256=$hash" `
        --artifacts-path (Join-Path $work 'launcher-build') --output $published -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish the self-contained portable launcher.' }
}
finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $published 'SpaceMaid.Installer.exe') -Destination $destinationFile
