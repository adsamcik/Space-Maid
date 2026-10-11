param(
    [string]$BrandingDirectory = (Join-Path $PSScriptRoot '../assets/branding')
)

$ErrorActionPreference = 'Stop'
$taskMagick = (Get-Command magick -ErrorAction Stop).Source
$taskBranding = [System.IO.Path]::GetFullPath($BrandingDirectory)
$taskSource = Join-Path $taskBranding 'space-maid-visor.png'
if (!(Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw "Missing icon source: $taskSource" }
$taskFrames = Join-Path $taskBranding 'visor-icon-frames'
[System.IO.Directory]::CreateDirectory($taskFrames) | Out-Null

# The simple symbol uses one source. Area sampling keeps the smallest
# frames clear; larger frames use smoother edge sampling.
$taskProfiles = @(
    @{ Size = 16; Filter = 'Box' },
    @{ Size = 20; Filter = 'Box' },
    @{ Size = 24; Filter = 'Box' },
    @{ Size = 32; Filter = 'Box' },
    @{ Size = 40; Filter = 'Mitchell' },
    @{ Size = 48; Filter = 'Mitchell' },
    @{ Size = 64; Filter = 'Mitchell' },
    @{ Size = 96; Filter = 'Lanczos' },
    @{ Size = 128; Filter = 'Lanczos' },
    @{ Size = 256; Filter = 'Lanczos' }
)

$taskFramePaths = foreach ($taskProfile in $taskProfiles) {
    $taskFrame = Join-Path $taskFrames "$($taskProfile.Size).png"
    $taskCanvas = "$($taskProfile.Size)x$($taskProfile.Size)"
    & $taskMagick $taskSource -filter $taskProfile.Filter -resize $taskCanvas -gravity center -background '#082140' -extent $taskCanvas "PNG32:$taskFrame"
    if ($LASTEXITCODE -ne 0) { throw "Could not render the $($taskProfile.Size) px icon." }
    $taskFrame
}

$taskDestination = Join-Path $taskBranding 'space-maid-visor.ico'
& $taskMagick @taskFramePaths $taskDestination
if ($LASTEXITCODE -ne 0) { throw 'Could not package the Windows icon.' }
Write-Output $taskDestination
