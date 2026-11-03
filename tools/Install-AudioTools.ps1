<#
.SYNOPSIS
    Instala las herramientas externas que necesita el procesado de audio de DeskPanel.

.DESCRIPTION
    DeskPanel limpia el ruido con DeepFilterNet3, transcribe con Whisper y convierte audio con ffmpeg.
    Ninguna de esas piezas se incluye con la aplicacion; este script las descarga a una carpeta:

        <ToolsFolder>\ffmpeg.exe        (ffmpeg 'essentials', gyan.dev, ~109 MB comprimido)
        <ToolsFolder>\deep-filter.exe   (DeepFilterNet 0.5.6, GitHub Rikorose/DeepFilterNet, ~26 MB)
        <ToolsFolder>\models\ggml-<modelo>.bin   (modelo de Whisper, HuggingFace ggerganov/whisper.cpp)

    Por defecto ToolsFolder es %LOCALAPPDATA%\DeskPanel\tools, que es donde DeskPanel las busca.
    Si ffmpeg ya esta en el PATH no se vuelve a descargar. Es seguro ejecutarlo varias veces:
    lo que ya esta instalado se omite (usa -Force para reinstalar).

    No necesita permisos de administrador.

.PARAMETER ToolsFolder
    Carpeta de destino.

.PARAMETER WhisperModel
    Modelo de Whisper. 'large-v3-turbo' (1,5 GB) es la recomendacion: casi la calidad de large-v3 y mucho mas rapido.
    Tamanos aproximados: tiny 74 MB, base 142 MB, small 466 MB, medium 1,5 GB, large-v3-turbo-q5_0 547 MB,
    large-v3-turbo 1,5 GB, large-v3 2,9 GB.

.PARAMETER SkipFfmpeg / SkipDeepFilter / SkipWhisper
    Omite esa descarga.

.PARAMETER Force
    Descarga de nuevo aunque ya exista.

.PARAMETER NoPause
    No espera a pulsar Intro al terminar (para ejecutarlo desde otro programa o en automatico).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-AudioTools.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-AudioTools.ps1 -WhisperModel large-v3 -ToolsFolder D:\Herramientas\DeskPanel
#>
[CmdletBinding()]
param(
    [string]$ToolsFolder = (Join-Path $env:LOCALAPPDATA 'DeskPanel\tools'),
    [ValidateSet('tiny', 'base', 'small', 'medium', 'large-v3', 'large-v3-turbo', 'large-v3-turbo-q5_0')]
    [string]$WhisperModel = 'large-v3-turbo',
    [switch]$SkipFfmpeg,
    [switch]$SkipDeepFilter,
    [switch]$SkipWhisper,
    [switch]$Force,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # la barra de progreso de Invoke-WebRequest multiplica por 10 el tiempo de descarga
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$DeepFilterUrl = 'https://github.com/Rikorose/DeepFilterNet/releases/download/v0.5.6/deep-filter-0.5.6-x86_64-pc-windows-msvc.exe'
$FfmpegUrl     = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
$ModelUrl      = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-$WhisperModel.bin"

$results = New-Object System.Collections.Generic.List[object]

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Save-Download([string]$Url, [string]$Target) {
    # Se descarga a un fichero temporal y solo se renombra si esta completo: un corte de red no deja un .exe/.bin a medias.
    $part = "$Target.part"
    for ($try = 1; $try -le 3; $try++) {
        try {
            if (Test-Path $part) { Remove-Item $part -Force }
            Invoke-WebRequest -Uri $Url -OutFile $part -UseBasicParsing
            $head = Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing
            $expected = [int64]($head.Headers['Content-Length'] | Select-Object -First 1)
            $actual = (Get-Item $part).Length
            if ($expected -gt 0 -and $actual -ne $expected) { throw "tamano incorrecto ($actual de $expected bytes)" }
            Move-Item $part $Target -Force
            return
        }
        catch {
            Write-Host "   intento $try fallido: $($_.Exception.Message)" -ForegroundColor Yellow
            if ($try -eq 3) { throw }
            Start-Sleep -Seconds (3 * $try)
        }
    }
}

function Test-Sha256([string]$File, [string]$Expected, [string]$What) {
    if (-not $Expected) { Write-Host "   (sin suma de verificacion publicada para $What; se omite la comprobacion)" -ForegroundColor DarkGray; return }
    $actual = (Get-FileHash $File -Algorithm SHA256).Hash
    if ($actual -ne $Expected.Trim('"').ToUpperInvariant()) { throw "La suma SHA-256 de $What no coincide. Borra el fichero y vuelve a intentarlo." }
    Write-Host '   SHA-256 correcto' -ForegroundColor DarkGray
}

function Get-LinkedSha256([string]$Url) {
    # HuggingFace publica el SHA-256 del fichero en la cabecera X-Linked-Etag de la primera respuesta (la redireccion),
    # que se pierde si se sigue la redireccion; por eso se pide sin seguirla.
    try {
        $request = [Net.HttpWebRequest]::Create($Url)
        $request.Method = 'HEAD'
        $request.AllowAutoRedirect = $false
        $response = $request.GetResponse()
        try { return $response.Headers['X-Linked-ETag'] } finally { $response.Close() }
    }
    catch { return $null }
}

function Add-Result([string]$Name, [string]$State, [string]$Detail) { $results.Add([pscustomobject]@{ Herramienta = $Name; Estado = $State; Detalle = $Detail }) }

New-Item -ItemType Directory -Force -Path $ToolsFolder | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ToolsFolder 'models') | Out-Null
Write-Host "Carpeta de herramientas: $ToolsFolder"

$failed = $false

# ---------- ffmpeg ----------
if ($SkipFfmpeg) { Add-Result 'ffmpeg' 'omitido' '' }
else {
    Write-Step 'ffmpeg'
    $ffmpegTarget = Join-Path $ToolsFolder 'ffmpeg.exe'
    $onPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    try {
        if ((Test-Path $ffmpegTarget) -and -not $Force) { Write-Host '   ya instalado'; Add-Result 'ffmpeg' 'ya estaba' $ffmpegTarget }
        elseif ($onPath -and -not $Force) { Write-Host "   ya esta en el PATH: $($onPath.Source)"; Add-Result 'ffmpeg' 'ya estaba (PATH)' $onPath.Source }
        else {
            $zip = Join-Path ([IO.Path]::GetTempPath()) "deskpanel-ffmpeg-$([guid]::NewGuid().ToString('N')).zip"
            $extract = Join-Path ([IO.Path]::GetTempPath()) "deskpanel-ffmpeg-$([guid]::NewGuid().ToString('N'))"
            try {
                Write-Host '   descargando (~109 MB)...'
                Save-Download $FfmpegUrl $zip
                $sha = (Invoke-WebRequest -Uri "$FfmpegUrl.sha256" -UseBasicParsing).Content
                # Segun el tipo MIME del servidor llega como texto o como bytes.
                $shaText = if ($sha -is [byte[]]) { [Text.Encoding]::ASCII.GetString($sha) } else { [string]$sha }
                Test-Sha256 $zip $shaText.Trim().Split()[0] 'ffmpeg'
                Expand-Archive -Path $zip -DestinationPath $extract -Force
                $exe = Get-ChildItem $extract -Recurse -Filter ffmpeg.exe | Select-Object -First 1
                if (-not $exe) { throw 'el zip no contiene ffmpeg.exe' }
                Copy-Item $exe.FullName $ffmpegTarget -Force
                Add-Result 'ffmpeg' 'instalado' $ffmpegTarget
            }
            finally {
                if (Test-Path $zip) { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
                if (Test-Path $extract) { Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue }
            }
        }
    }
    catch { Write-Host "   ERROR: $($_.Exception.Message)" -ForegroundColor Red; Add-Result 'ffmpeg' 'ERROR' $_.Exception.Message; $failed = $true }
}

# ---------- deep-filter ----------
if ($SkipDeepFilter) { Add-Result 'deep-filter' 'omitido' '' }
else {
    Write-Step 'DeepFilterNet (deep-filter.exe)'
    $dfTarget = Join-Path $ToolsFolder 'deep-filter.exe'
    try {
        if ((Test-Path $dfTarget) -and -not $Force) { Write-Host '   ya instalado'; Add-Result 'deep-filter' 'ya estaba' $dfTarget }
        else {
            Write-Host '   descargando (~26 MB)...'
            Save-Download $DeepFilterUrl $dfTarget
            Add-Result 'deep-filter' 'instalado' $dfTarget
        }
    }
    catch { Write-Host "   ERROR: $($_.Exception.Message)" -ForegroundColor Red; Add-Result 'deep-filter' 'ERROR' $_.Exception.Message; $failed = $true }
}

# ---------- modelo de Whisper ----------
if ($SkipWhisper) { Add-Result "whisper $WhisperModel" 'omitido' '' }
else {
    Write-Step "Modelo de Whisper ($WhisperModel)"
    $modelTarget = Join-Path $ToolsFolder "models\ggml-$WhisperModel.bin"
    try {
        if ((Test-Path $modelTarget) -and -not $Force) { Write-Host '   ya instalado'; Add-Result "whisper $WhisperModel" 'ya estaba' $modelTarget }
        else {
            $head = Invoke-WebRequest -Uri $ModelUrl -Method Head -UseBasicParsing
            $size = [int64]($head.Headers['Content-Length'] | Select-Object -First 1)
            Write-Host ("   descargando ({0:N0} MB, puede tardar)..." -f ($size / 1MB))
            Save-Download $ModelUrl $modelTarget
            Test-Sha256 $modelTarget (Get-LinkedSha256 $ModelUrl) 'el modelo'
            Add-Result "whisper $WhisperModel" 'instalado' $modelTarget
        }
    }
    catch { Write-Host "   ERROR: $($_.Exception.Message)" -ForegroundColor Red; Add-Result "whisper $WhisperModel" 'ERROR' $_.Exception.Message; $failed = $true }
}

# ---------- Comprobacion ----------
Write-Step 'Comprobacion'
foreach ($check in @(@{ N = 'ffmpeg'; P = (Join-Path $ToolsFolder 'ffmpeg.exe'); A = '-version' }, @{ N = 'deep-filter'; P = (Join-Path $ToolsFolder 'deep-filter.exe'); A = '--version' })) {
    $exe = if (Test-Path $check.P) { $check.P } else { (Get-Command $check.N -ErrorAction SilentlyContinue).Source }
    if ($exe) { Write-Host ("   {0,-12} {1}" -f $check.N, ((& $exe $check.A 2>&1 | Select-Object -First 1) -as [string])) }
    else { Write-Host ("   {0,-12} no disponible" -f $check.N) -ForegroundColor Yellow }
}

# Informativo: lo que la propia aplicacion necesita en cada equipo (no se instala aqui).
$runtimes = try { & dotnet --list-runtimes 2>$null } catch { $null }
if (-not ($runtimes -match 'Microsoft\.WindowsDesktop\.App 10\.')) {
    Write-Host ''
    Write-Host 'AVISO: no se detecta el runtime ".NET 10 Desktop". DeskPanel lo necesita (si no va empaquetado con la app):' -ForegroundColor Yellow
    Write-Host '       winget install Microsoft.DotNet.DesktopRuntime.10' -ForegroundColor Yellow
}
if (-not ($runtimes -match 'Microsoft\.AspNetCore\.App 10\.')) {
    Write-Host ''
    Write-Host 'AVISO: no se detecta el runtime "ASP.NET Core 10". La interfaz web de DeskPanel lo necesita (si no va empaquetado):' -ForegroundColor Yellow
    Write-Host '       winget install Microsoft.DotNet.AspNetCore.10' -ForegroundColor Yellow
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host
if ($failed) { Write-Host 'Terminado CON ERRORES: vuelve a ejecutar el script cuando haya conexion.' -ForegroundColor Red }
else { Write-Host 'Listo. DeskPanel ya puede procesar audio en este equipo.' -ForegroundColor Green }

if (-not $NoPause) { Read-Host 'Pulsa Intro para cerrar' | Out-Null }
if ($failed) { exit 1 }
