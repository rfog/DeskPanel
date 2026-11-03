<#
.SYNOPSIS
    Genera el paquete de DeskPanel para copiar a otro ordenador (dist\DeskPanel-win-<arquitectura>[-autonomo].zip).

.DESCRIPTION
    Publica la aplicacion en Release para Windows (x64 o ARM64), sin los motores de Whisper de otros sistemas, anade un
    LEEME-INSTALACION.txt y lo comprime. Incluye VerificaFicheros.exe y el script Install-AudioTools.ps1.

    La arquitectura es la del equipo donde se ejecuta el script, salvo que se indique -Arquitectura (se puede generar el
    paquete ARM64 desde un PC x64 y al reves). Un paquete x64 funciona tambien en Windows ARM, pero emulado y pidiendo los
    runtimes de .NET x64: para un equipo ARM conviene el paquete arm64.

    Dos variantes:
      (por defecto)     necesita los runtimes ".NET 10 Desktop" y "ASP.NET Core 10" en el otro equipo; ocupa unos 90 MB.
      -SelfContained    lleva .NET dentro y no necesita instalar nada de .NET; ocupa unos 250 MB.

    El otro equipo sigue necesitando el runtime de WebView2 (viene con Windows 11 y con Edge actualizado).

.PARAMETER SelfContained
    Incluye el runtime de .NET en el paquete.

.PARAMETER Arquitectura
    x64 o arm64. Por defecto, la del equipo donde se ejecuta el script.

.PARAMETER NoZip
    Solo deja la carpeta, sin comprimirla.
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [ValidateSet('x64', 'arm64')]
    [string]$Arquitectura = $(if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }),
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$rid = "win-$Arquitectura"
$profile = if ($SelfContained) { "DeskPanel-$rid-autonomo" } else { "DeskPanel-$rid" }
$out = Join-Path $root "dist\$profile"
$zip = Join-Path $root "dist\$profile.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }   # sin restos de publicaciones anteriores
Write-Host "Publicando ($profile)..."
$project = Join-Path $root 'src\DeskPanel.App\DeskPanel.App.csproj'
if ($SelfContained) {
    # Como propiedad global (no en un perfil): asi tambien llega a VerificaFicheros, y el SDK (NETSDK1150) no admite
    # que un ejecutable autonomo referencie a otro que no lo es.
    & dotnet publish $project -c Release -r $rid --self-contained true -o $out --nologo -v q
}
else {
    # Runtime and output as global properties too (not through the publish profile): that way they also reach
    # VerificaFicheros, whose exe would otherwise be built for the architecture of the machine running the script.
    & dotnet publish $project -c Release -r $rid --self-contained false -o $out --nologo -v q
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo (codigo $LASTEXITCODE)" }

$net = if ($SelfContained) { 'No hace falta instalar .NET: va incluido.' } else { "Instala los runtimes `".NET 10 Desktop`" y `"ASP.NET Core 10`" de la MISMA arquitectura que este paquete ($Arquitectura; el segundo es para la interfaz web):  winget install Microsoft.DotNet.DesktopRuntime.10 --architecture $Arquitectura  y  winget install Microsoft.DotNet.AspNetCore.10 --architecture $Arquitectura" }
$readme = @"
DeskPanel - instalacion en otro equipo
======================================

1. Copia esta carpeta donde quieras (por ejemplo C:\Programas\DeskPanel). No hace falta instalador ni administrador.
2. $net
3. WebView2 (visor de PDF, vista previa de feeds, generacion de PDF): viene con Windows 11. En Windows 10 sin Edge
   actualizado:  winget install Microsoft.EdgeWebView2Runtime
4. Ejecuta DeskPanel.exe. Al primer arranque crea %LOCALAPPDATA%\DeskPanel\machine.json y deskpanel.json.
   - En Configurar > General elige la carpeta de configuracion (la sincronizada, para compartir ajustes).
   - En machine.json (es propio de cada equipo) define "Roots": los alias {Cloud}, {Nas}... con la ruta real de ESTE
     equipo, o copia el machine.json del otro ordenador y ajusta las rutas.
5. Procesado de audio (opcional): en Configurar > Audio pulsa "Descargar herramientas", o ejecuta
   assets\tools\Install-AudioTools.ps1 (descarga ffmpeg, DeepFilterNet y el modelo de Whisper; no pide administrador).
6. Claves de IA (Gemini/Claude): se guardan cifradas por usuario y equipo; hay que escribirlas de nuevo aqui.
   Los indices de busqueda tambien son locales (puedes copiar la carpeta %LOCALAPPDATA%\DeskPanel\Indexes).

Si Windows muestra "Smart App Control bloqueo esta aplicacion" o SmartScreen avisa: los programas no estan firmados.
 - SmartScreen: "Mas informacion" > "Ejecutar de todas formas", o clic derecho en el .zip > Propiedades > Desbloquear
   antes de descomprimir.
 - Smart App Control (Windows 11) no deja ejecutar binarios sin firmar: hay que desactivarlo (Seguridad de Windows >
   Control de aplicaciones y del navegador) o firmar los ejecutables con un certificado de confianza.
"@
[IO.File]::WriteAllText((Join-Path $out 'LEEME-INSTALACION.txt'), $readme, (New-Object Text.UTF8Encoding($true)))

$files = Get-ChildItem $out -Recurse -File
Write-Host ("Carpeta: {0}  ({1} ficheros, {2:N0} MB)" -f $out, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

if (-not $NoZip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("Zip: {0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}
