<#
.SYNOPSIS
    Genera el icono de DeskPanel (assets\icons\deskpanel.ico) dibujandolo con GDI+.

.DESCRIPTION
    Cuadrado redondeado en el verde azulado de la aplicacion con una rejilla 2x2 de "fichas" (el panel de acciones) y una
    de ellas resaltada en amarillo (la accion elegida). Se dibuja a mano en cada tamano en lugar de reducir uno grande,
    para que los tamanos pequenos (16-32 px, bandeja y barra de tareas) queden nitidos.

    Vuelve a ejecutarlo tras cambiar los colores o proporciones de abajo. No necesita nada instalado.

.PARAMETER OutFile
    Fichero .ico de salida.

.PARAMETER PreviewPng
    Opcional: guarda una imagen con el icono a varios tamanos, para revisarlo a simple vista.

.PARAMETER WebIconsDir
    Opcional: escribe tambien icon-192.png, icon-512.png y apple-touch-icon.png (para la interfaz web movil).
    El de Apple va sin esquinas transparentes porque iOS las rellena de negro.
#>
[CmdletBinding()]
param(
    [string]$OutFile,
    [string]$PreviewPng,
    [string]$WebIconsDir
)

$ErrorActionPreference = 'Stop'

# El valor por defecto no va en el bloque param: $PSScriptRoot esta vacio ahi en Windows PowerShell 5.1.
if (-not $OutFile) { $OutFile = Join-Path $PSScriptRoot '..\assets\icons\deskpanel.ico' }
Add-Type -AssemblyName System.Drawing

$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$Top = [Drawing.ColorTranslator]::FromHtml('#1E8A95')
$Bottom = [Drawing.ColorTranslator]::FromHtml('#0D464E')
$TileColor = [Drawing.Color]::FromArgb(246, 255, 255, 255)
$HighlightColor = [Drawing.ColorTranslator]::FromHtml('#FFD866')

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.01, 2 * $r)
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$s, [bool]$square = $false) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([Drawing.Color]::Transparent)

    # Fondo: cuadrado redondeado con degradado vertical.
    $margin = [Math]::Max(0, [Math]::Round($s * 0.03))
    $body = $s - 2 * $margin
    if ($square) { $margin = 0; $body = $s }
    $bg = New-RoundedRect $margin $margin $body $body $(if ($square) { 0.01 } else { $s * 0.22 })
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush ((New-Object Drawing.Rectangle 0, 0, $s, $s)), $Top, $Bottom, 90
    $g.FillPath($brush, $bg)

    # Rejilla 2x2. En tamanos pequenos las medidas se redondean a pixeles enteros para que los bordes queden limpios.
    $m = $s * 0.215; $gap = $s * 0.075
    if ($s -le 48) { $m = [Math]::Round($m); $gap = [Math]::Max(1, [Math]::Round($gap)) }
    $tile = ($s - 2 * $m - $gap) / 2
    if ($s -le 48) { $tile = [Math]::Floor($tile) ; $m = [Math]::Floor(($s - 2 * $tile - $gap) / 2) }
    $radius = [Math]::Max(1, $s * 0.065)

    for ($row = 0; $row -lt 2; $row++) {
        for ($col = 0; $col -lt 2; $col++) {
            $x = $m + $col * ($tile + $gap)
            $y = $m + $row * ($tile + $gap)
            $color = if ($row -eq 0 -and $col -eq 1) { $HighlightColor } else { $TileColor }
            $path = New-RoundedRect $x $y $tile $tile $radius
            $fill = New-Object Drawing.SolidBrush $color
            $g.FillPath($fill, $path)
            $fill.Dispose(); $path.Dispose()
        }
    }

    $brush.Dispose(); $bg.Dispose(); $g.Dispose()
    return $bmp
}

# --- PNG de cada tamano ---
$images = foreach ($s in $Sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    [pscustomobject]@{ Size = $s; Bitmap = $bmp; Png = $ms.ToArray() }
}

# --- Contenedor ICO con entradas PNG (soportado desde Windows Vista) ---
$outPath = [IO.Path]::GetFullPath($OutFile)
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($outPath)) | Out-Null
$stream = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $stream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { [byte]0 } else { [byte]$img.Size }   # 0 significa 256
    $w.Write($dim); $w.Write($dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$img.Png.Length); $w.Write([uint32]$offset)
    $offset += $img.Png.Length
}
foreach ($img in $images) { $w.Write($img.Png) }
$w.Flush()
[IO.File]::WriteAllBytes($outPath, $stream.ToArray())
Write-Host "Icono escrito: $outPath ($($images.Count) tamanos, $([Math]::Round((Get-Item $outPath).Length / 1KB)) KB)"

# --- Vista previa opcional: sobre fondo claro y oscuro, con los tamanos pequenos ampliados x4 ---
if ($PreviewPng) {
    $sheet = New-Object Drawing.Bitmap 760, 330
    $g = [Drawing.Graphics]::FromImage($sheet)
    $g.Clear([Drawing.Color]::White)
    $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#202124'))), 380, 0, 380, 330)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
    foreach ($base in 0, 380) {
        $g.DrawImage(($images | Where-Object Size -eq 256).Bitmap, $base + 10, 10, 160, 160)   # 256 reducido a 160
        $x = $base + 185; $y = 14
        foreach ($s in 16, 24, 32, 48) {
            $g.DrawImage(($images | Where-Object Size -eq $s).Bitmap, $x, $y, $s * 3, $s * 3)   # ampliado x3 para ver los pixeles
            $x += $s * 3 + 8
            if ($x -gt $base + 340) { $x = $base + 185; $y += 160 }
        }
        # tamano real (1:1) de los pequenos
        $x = $base + 14; $y = 200
        foreach ($s in 16, 20, 24, 32, 40, 48) { $g.DrawImage(($images | Where-Object Size -eq $s).Bitmap, $x, $y, $s, $s); $x += $s + 14 }
    }
    $g.Dispose()
    $sheet.Save([IO.Path]::GetFullPath($PreviewPng), [Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    Write-Host "Vista previa: $PreviewPng"
}

if ($WebIconsDir) {
    New-Item -ItemType Directory -Force -Path $WebIconsDir | Out-Null
    $webIcons = @(
        @{ File = 'icon-192.png'; Size = 192; Square = $false },
        @{ File = 'icon-512.png'; Size = 512; Square = $false },
        @{ File = 'apple-touch-icon.png'; Size = 180; Square = $true })
    foreach ($spec in $webIcons) {
        $webBitmap = New-IconBitmap $spec.Size $spec.Square
        $webBitmap.Save((Join-Path ([IO.Path]::GetFullPath($WebIconsDir)) $spec.File), [Drawing.Imaging.ImageFormat]::Png)
        $webBitmap.Dispose()
    }
    Write-Host "Iconos web escritos en: $WebIconsDir"
}

foreach ($img in $images) { $img.Bitmap.Dispose() }
