<#
.SYNOPSIS
  Downloads a pdf.js prebuilt viewer into assets/pdfjs.
.DESCRIPTION
  The viewer is vendored because it is served offline through WebView2. Source maps and the demo PDF are removed
  to keep the repository small. After updating, check that viewer.mjs still accepts the #search= hash parameter
  and that saving still goes through the download path.
.EXAMPLE
  ./tools/Get-PdfJs.ps1 -Version 6.3.289
#>
param(
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'assets\pdfjs'
$zip = Join-Path $env:TEMP "pdfjs-$Version-dist.zip"
$url = "https://github.com/mozilla/pdf.js/releases/download/v$Version/pdfjs-$Version-dist.zip"

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $target
Get-ChildItem $target -Recurse -Filter '*.map' | Remove-Item -Force
Remove-Item (Join-Path $target 'web\compressed.tracemonkey-pldi-09.pdf') -ErrorAction SilentlyContinue
Remove-Item $zip

Write-Host "pdf.js $Version installed in $target"
