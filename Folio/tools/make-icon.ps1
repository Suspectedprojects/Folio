# Draws Folio's folder icon at every standard size and packs them into folio.ico.
# Usage: powershell -File tools\make-icon.ps1   (run from the Folio folder)
param([string]$Out = (Join-Path $PSScriptRoot '..\folio.ico'), [string]$Preview)
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Color([string]$hex) { return [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)   # design on a 256 grid

    # Back panel + tab
    $back = RoundRect 14 50 228 166 18
    $tab = RoundRect 14 30 96 44 16
    $backBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 30)), ([System.Drawing.PointF]::new(0, 216)), (Color '#E59A10'), (Color '#C97C06')
    $g.FillPath($backBrush, $tab)
    $g.FillPath($backBrush, $back)

    # Two sheets of paper peeking out (a nod to thumbnails)
    if ($size -ge 24) {
        $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#E9EEF5')), (RoundRect 38 60 150 100 8))
        $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#FFFFFF')), (RoundRect 60 70 158 100 8))
        if ($size -ge 48) {
            # tiny landscape on the front sheet
            $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#5AA9F0')), (RoundRect 72 82 134 52 5))
            $hill = New-Object System.Drawing.Drawing2D.GraphicsPath
            $hill.AddPolygon([System.Drawing.PointF[]]@([System.Drawing.PointF]::new(72, 134), [System.Drawing.PointF]::new(116, 102), [System.Drawing.PointF]::new(148, 124), [System.Drawing.PointF]::new(170, 110), [System.Drawing.PointF]::new(206, 134)))
            $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#3E8E5A')), $hill)
        }
    }

    # Front panel
    $front = RoundRect 14 124 228 92 18
    $frontBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 124)), ([System.Drawing.PointF]::new(0, 216)), (Color '#FFD45C'), (Color '#F5AE1E')
    $g.FillPath($frontBrush, $front)

    # Soft highlight along the front panel's top edge
    if ($size -ge 32) {
        $hl = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(140, 255, 247, 214)), 5
        $g.DrawLine($hl, 30, 128, 226, 128)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = Draw $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    , $ms.ToArray()
}

# ICO container: header, directory, then PNG-compressed images
$Out = [System.IO.Path]::GetFullPath($Out)
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
[System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($Out, '.png'), $pngs[-1])  # 256px PNG for the README

# Preview sheet for checking the design
$sheet = New-Object System.Drawing.Bitmap 760, 300
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear((Color '#F3F3F3'))
$g.FillRectangle((New-Object System.Drawing.SolidBrush (Color '#202020')), 0, 150, 760, 150)
$x = 10
foreach ($s in 16, 24, 32, 48, 64, 128) {
    $b = Draw $s
    $g.DrawImage($b, $x, 75 - $s / 2, $s, $s)
    $g.DrawImage($b, $x, 225 - $s / 2, $s, $s)
    $x += $s + 24
}
$big = Draw 256
$g.DrawImage($big, $x, 22, 256, 256)
if ($Preview) { $sheet.Save([System.IO.Path]::GetFullPath($Preview)) }
"wrote $Out"

