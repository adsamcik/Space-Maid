[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The pinned native pruning fixture requires Windows x64.' }
$destinationPath = [IO.Path]::GetFullPath($Destination)
$expectedHash = 'CB92EDF62161AA367C80B9D7C34D4D0E1310EBF8DFF843A7E8FA6F03E44BA0ED'
$asset = 'https://github.com/pnpm/pnpm/releases/download/v10.15.1/pnpm-win-x64.exe'
[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
$target = Join-Path $destinationPath 'pnpm-win-x64.exe'
if (-not (Test-Path -LiteralPath $target)) {
    $temporary = Join-Path $destinationPath ('.pnpm-' + [Guid]::NewGuid().ToString('N') + '.download')
    try {
        Invoke-WebRequest -Uri $asset -OutFile $temporary -TimeoutSec 120
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The pnpm 10.15.1 release checksum did not match.' }
        [IO.File]::Move($temporary, $target, $false)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The existing native pnpm fixture differs from the pinned release. It was preserved.'
}
Write-Output $target
