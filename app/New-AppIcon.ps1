<#
.SYNOPSIS
    Draws the Teams Captions icon (white "CC" on an indigo rounded square) into TeamsCaptions.ico.

.DESCRIPTION
    Each size is drawn separately, so small sizes stay sharp. Sizes under 256 are stored as
    32-bit bitmaps and 256 as PNG, which every Windows icon reader accepts. -Preview also
    writes a PNG showing every size, enlarged, on light and dark backgrounds.
#>
param(
    [string]$Path = (Join-Path $PSScriptRoot 'TeamsCaptions.ico'),
    [string]$Preview
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function New-RoundedRect([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $R
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconBitmap([int]$Size) {
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(0.5, $Size * 0.06)
    $side = $Size - 2 * $inset
    $back = New-RoundedRect $inset $inset $side $side ($Size * 0.2)
    $top = [System.Drawing.Color]::FromArgb(255, 0x5B, 0x5F, 0xC7)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x3E, 0x44, 0xA6)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $inset)), (New-Object System.Drawing.PointF(0, ($Size - $inset))), $top, $bottom)
    $g.FillPath($brush, $back)

    # "CC", scaled to a fixed box so every size has the same proportions.
    $text = New-Object System.Drawing.Drawing2D.GraphicsPath
    $font = New-Object System.Drawing.FontFamily('Segoe UI')
    $text.AddString('CC', $font, [int][System.Drawing.FontStyle]::Bold, 100, (New-Object System.Drawing.PointF(0, 0)), [System.Drawing.StringFormat]::GenericTypographic)
    $b = $text.GetBounds()
    $boxW = $Size * 0.66
    $boxH = $Size * 0.40
    $scale = [Math]::Min($boxW / $b.Width, $boxH / $b.Height)
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate(($Size - $b.Width * $scale) / 2, ($Size - $b.Height * $scale) / 2 + $Size * 0.01)
    $m.Scale($scale, $scale)
    $m.Translate(-$b.X, -$b.Y)
    $text.Transform($m)
    $g.FillPath([System.Drawing.Brushes]::White, $text)

    $g.Dispose()
    $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$Bmp) {
    $w = $Bmp.Width
    $h = $Bmp.Height
    $data = $Bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $w, $h)), 'ReadOnly', 'Format32bppArgb')
    $pixels = New-Object byte[] ($w * $h * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $Bmp.UnlockBits($data)
    $maskRow = [int]([Math]::Ceiling($w / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER: height counts the colour rows and the AND mask rows.
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($w * $h * 4 + $maskRow * $h)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $w * 4, $w * 4) }   # bottom-up BGRA
    $bw.Write((New-Object byte[] ($maskRow * $h)))                                   # empty AND mask; alpha does the work
    $bw.Flush()
    , $ms.ToArray()   # the comma keeps PowerShell from unrolling the bytes
}

$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    }
    else { $bytes = [byte[]](Get-DibBytes $bmp) }
    [pscustomobject]@{ Size = $s; Bytes = $bytes; Bitmap = $bmp }
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$img.Bytes.Length); $bw.Write([int]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img.Bytes, 0, $img.Bytes.Length) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
"Wrote $Path ($($ms.Length) bytes, sizes $($sizes -join ', '))"

# Read every size back, to prove the file is sound: WPF for all frames, and Windows Forms
# (which skips PNG frames) for the bitmap ones.
Add-Type -AssemblyName PresentationCore
$stream = [System.IO.File]::OpenRead($Path)
try {
    $decoder = New-Object System.Windows.Media.Imaging.IconBitmapDecoder($stream, 'None', 'OnLoad')
    $found = @($decoder.Frames | ForEach-Object { $_.PixelWidth }) | Sort-Object
    if (($found -join ',') -ne (($sizes | Sort-Object) -join ',')) { throw "WPF reads frames $($found -join ', ')." }
}
finally { $stream.Dispose() }
foreach ($s in $sizes | Where-Object { $_ -lt 256 }) {
    $icon = New-Object System.Drawing.Icon($Path, $s, $s)
    if ($icon.Width -ne $s) { throw "Size $s reads back as $($icon.Width)." }
    $bmp = $icon.ToBitmap()
    $centre = $bmp.GetPixel([int]($s / 2), [int]($s * 0.15))
    if ($centre.A -lt 200) { throw "Size $s reads back without its background." }
    $icon.Dispose()
}
"Read back all $($sizes.Count) sizes"

if ($Preview) {
    $zoom = 4
    $width = 24 + ($sizes | Where-Object { $_ -le 64 } | ForEach-Object { $_ * $zoom + 16 } | Measure-Object -Sum).Sum + 256 + 16
    $height = 2 * (256 + 32)
    $sheet = New-Object System.Drawing.Bitmap([int]$width, [int]$height)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = 'NearestNeighbor'
    $g.PixelOffsetMode = 'Half'
    foreach ($row in 0, 1) {
        $bg = if ($row -eq 0) { [System.Drawing.Color]::FromArgb(243, 243, 243) } else { [System.Drawing.Color]::FromArgb(32, 32, 32) }
        $y0 = $row * (256 + 32)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush($bg)), 0, $y0, $width, 256 + 32)
        $x = 16
        foreach ($img in $images) {
            $shown = if ($img.Size -le 64) { $img.Size * $zoom } else { 256 }
            $g.DrawImage($img.Bitmap, $x, $y0 + 16, $shown, $shown)
            $x += $shown + 16
        }
    }
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    "Wrote preview $Preview"
}
