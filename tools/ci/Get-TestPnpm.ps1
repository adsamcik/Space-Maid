[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The pinned native pruning fixture requires Windows x64.' }
$destinationPath = [IO.Path]::GetFullPath($Destination)
$expectedHash = 'E0AF8D34DD5319F7E9A6C9838779E7E85CAF9CFDEDF99A7A4106AA1FDCF16D5C'
$asset = 'https://github.com/pnpm/pnpm/releases/download/v10.34.9/pnpm-win-x64.exe'
[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
$target = Join-Path $destinationPath 'pnpm-win-x64.exe'
if (-not (Test-Path -LiteralPath $target)) {
    $temporary = Join-Path $destinationPath ('.pnpm-' + [Guid]::NewGuid().ToString('N') + '.download')
    try {
        Invoke-WebRequest -Uri $asset -OutFile $temporary -TimeoutSec 120
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The pnpm 10.34.9 release checksum did not match.' }
        [IO.File]::Move($temporary, $target, $false)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The existing native pnpm fixture differs from the pinned release. It was preserved.'
}
Write-Output $target
