param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '../assets/navigation')
)

$ErrorActionPreference = 'Stop'
$magick = (Get-Command magick -ErrorAction Stop).Source
$AssetDirectory = (Resolve-Path -LiteralPath $AssetDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $AssetDirectory 'prompts.json') -Raw | ConvertFrom-Json

foreach ($icon in $manifest.icons) {
    $source = Join-Path $AssetDirectory $icon.source
    $destination = Join-Path $AssetDirectory "$($icon.key).png"
    # Keep the generated silhouette and alpha; normalize only bounds and resolution.
    & $magick $source -trim +repage -resize 56x56 -gravity center -background none -extent 64x64 -strip $destination
    if ($LASTEXITCODE -ne 0) {
        throw "Could not prepare navigation icon '$($icon.key)'."
    }
}
