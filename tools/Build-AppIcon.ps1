param(
    [string]$BrandingDirectory = (Join-Path $PSScriptRoot '../assets/branding')
)

$ErrorActionPreference = 'Stop'
$taskMagick = (Get-Command magick -ErrorAction Stop).Source
$taskBranding = [System.IO.Path]::GetFullPath($BrandingDirectory)
$taskFrames = Join-Path $taskBranding 'taskbar-icon-frames'
[System.IO.Directory]::CreateDirectory($taskFrames) | Out-Null

# Small variants retain the face while removing secondary detail. Each
# frame has its own footprint, sampling and edge treatment.
$taskProfiles = @(
    @{ Size = 16; Content = 16; Source = 'space-maid-small-portrait.png'; Filter = 'Box'; Sharpen = '0x0.4+0.6+0.02' },
    @{ Size = 20; Content = 18; Source = 'space-maid-small-portrait.png'; Filter = 'Box'; Sharpen = '0x0.4+0.5+0.02' },
    @{ Size = 24; Content = 22; Source = 'space-maid-small-portrait.png'; Filter = 'Mitchell'; Sharpen = '0x0.4+0.4+0.02' },
    @{ Size = 32; Content = 30; Source = 'space-maid-medium-portrait.png'; Filter = 'Mitchell'; Sharpen = '0x0.4+0.3+0.02' },
    @{ Size = 40; Content = 38; Source = 'space-maid-medium-portrait.png'; Filter = 'Mitchell'; Sharpen = $null },
    @{ Size = 48; Content = 44; Source = 'space-maid-medium-portrait.png'; Filter = 'Lanczos'; Sharpen = $null },
    @{ Size = 64; Content = 60; Source = 'space-maid-medium-portrait.png'; Filter = 'Lanczos'; Sharpen = $null },
    @{ Size = 96; Content = 90; Source = 'space-maid-astronaut-icon.png'; Filter = 'Lanczos'; Sharpen = $null },
    @{ Size = 128; Content = 120; Source = 'space-maid-astronaut-icon.png'; Filter = 'Lanczos'; Sharpen = $null },
    @{ Size = 256; Content = 240; Source = 'space-maid-astronaut-icon.png'; Filter = 'Lanczos'; Sharpen = $null }
)

$taskFramePaths = foreach ($taskProfile in $taskProfiles) {
    $taskSource = Join-Path $taskBranding $taskProfile.Source
    if (!(Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw "Missing icon source: $taskSource" }
    $taskFrame = Join-Path $taskFrames "$($taskProfile.Size).png"
    $taskContent = "$($taskProfile.Content)x$($taskProfile.Content)"
    $taskCanvas = "$($taskProfile.Size)x$($taskProfile.Size)"
    $taskArguments = @($taskSource, '-trim', '+repage', '-filter', $taskProfile.Filter, '-resize', $taskContent)
    if ($taskProfile.Sharpen) { $taskArguments += @('-channel', 'RGB', '-unsharp', $taskProfile.Sharpen, '+channel') }
    $taskArguments += @('-gravity', 'center', '-background', 'none', '-extent', $taskCanvas, "PNG32:$taskFrame")
    & $taskMagick @taskArguments
    if ($LASTEXITCODE -ne 0) { throw "Could not render the $($taskProfile.Size) px icon." }
    $taskFrame
}

$taskDestination = Join-Path $taskBranding 'space-maid-size-tuned-icon.ico'
& $taskMagick @taskFramePaths $taskDestination
if ($LASTEXITCODE -ne 0) { throw 'Could not package the Windows icon.' }
Write-Output $taskDestination
