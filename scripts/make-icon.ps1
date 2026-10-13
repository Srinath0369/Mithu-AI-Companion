# Draws Mithu's face into assets\Mithu.ico with WPF, so the icon is vector-made and matches the app.
# Sizes 16-128 are classic 32-bit bitmaps (every Windows API reads them); 256 is PNG, like Windows' own icons.
# Run again after changing the design: powershell -File scripts\make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\assets\Mithu.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

function Draw([int]$size) {
    $midnight = [Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(0x0A, 0x0E, 0x16))
    $rim = [Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(0x27, 0x32, 0x44))
    $cyan = [Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(0x2D, 0xF3, 0xF3))
    $visual = [Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $s = $size / 64.0
    # Face plate with the app's proportions, fitted into the square; eyes and smile as in the app.
    $plate = [Windows.Rect]::new(2 * $s, 7.5 * $s, 60 * $s, 49 * $s)
    $rimPen = if ($size -ge 32) { [Windows.Media.Pen]::new($rim, [Math]::Max(1, 1.6 * $s)) } else { $null }
    $dc.DrawRoundedRectangle($midnight, $rimPen, $plate, 21 * $s, 21 * $s)
    $dc.DrawEllipse($cyan, $null, [Windows.Point]::new(21 * $s, 26.5 * $s), 5.2 * $s, 7 * $s)
    $dc.DrawEllipse($cyan, $null, [Windows.Point]::new(43 * $s, 26.5 * $s), 5.2 * $s, 7 * $s)
    $smile = [Windows.Media.Geometry]::Parse(("M {0},{1} Q {2},{3} {4},{1}" -f (24.5 * $s), (40 * $s), (32 * $s), (46.5 * $s), (39.5 * $s)))
    $pen = [Windows.Media.Pen]::new($cyan, [Math]::Max(1.2, 3.4 * $s))
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    $dc.DrawGeometry($null, $pen, $smile)
    $dc.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function Png($bitmap) {
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = [IO.MemoryStream]::new(); $encoder.Save($ms)
    return , $ms.ToArray()
}

# 32-bit DIB: BITMAPINFOHEADER, bottom-up BGRA rows (straight alpha), then an all-zero AND mask.
function Dib($bitmap, [int]$size) {
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $bitmap.CopyPixels($pixels, $stride, 0)
    $maskRow = [int][Math]::Ceiling($size / 32.0) * 4
    $ms = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($ms)
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($stride * $size + $maskRow * $size)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $i = $y * $stride + $x * 4
            $a = $pixels[$i + 3]
            if ($a -gt 0 -and $a -lt 255) {
                # Pbgra32 is premultiplied; icons want straight alpha.
                for ($c = 0; $c -lt 3; $c++) { $pixels[$i + $c] = [byte][Math]::Min(255, [Math]::Round($pixels[$i + $c] * 255.0 / $a)) }
            }
            $w.Write($pixels, $i, 4)
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($size in $sizes) {
    $bitmap = Draw $size
    $images += , $(if ($size -ge 256) { Png $bitmap } else { Dib $bitmap $size })
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$file = [IO.File]::Create($Out)
$w = [IO.BinaryWriter]::new($file)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {                                   # ICONDIRENTRY
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"Wrote $Out ($((Get-Item $Out).Length) bytes, sizes $($sizes -join ', '))"
