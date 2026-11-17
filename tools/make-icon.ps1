# アプリのアイコン (src/BandcampBenrify/Assets/app.ico と app.png) を描いて作る。
# 使い方: pwsh tools/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\BandcampBenrify\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # 背景: 角丸の正方形 (青緑のグラデーション)
    $m = [Math]::Max(1, $size * 0.04)
    $bg = New-RoundedRect $m $m ($size - 2 * $m) ($size - 2 * $m) ($size * 0.22)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.PointF 0, 0),
        (New-Object System.Drawing.PointF $size, $size),
        [System.Drawing.Color]::FromArgb(255, 0x2B, 0xB6, 0xD8),
        [System.Drawing.Color]::FromArgb(255, 0x0B, 0x5E, 0x7E))
    $g.FillPath($brush, $bg)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    if ($size -ge 32) {
        # キュー (3 本の線) + 再生マーク
        $lineH = $size * 0.075
        $x0 = $size * 0.20
        foreach ($i in 0..2) {
            $y = $size * (0.30 + 0.17 * $i)
            $w = $size * (0.30 - 0.04 * $i)
            $g.FillPath($white, (New-RoundedRect $x0 $y $w $lineH ($lineH / 2)))
        }

        $tri = @(
            (New-Object System.Drawing.PointF ($size * 0.56), ($size * 0.28)),
            (New-Object System.Drawing.PointF ($size * 0.56), ($size * 0.72)),
            (New-Object System.Drawing.PointF ($size * 0.84), ($size * 0.50)))
    }
    else {
        # 小さいサイズは再生マークだけ
        $tri = @(
            (New-Object System.Drawing.PointF ($size * 0.34), ($size * 0.24)),
            (New-Object System.Drawing.PointF ($size * 0.34), ($size * 0.76)),
            (New-Object System.Drawing.PointF ($size * 0.80), ($size * 0.50)))
    }

    $g.FillPolygon($white, [System.Drawing.PointF[]]$tri)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $assets 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO (各サイズを PNG のまま格納)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $pngs[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $w.Write($data) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $out.ToArray())
"wrote $(Join-Path $assets 'app.ico') ($($sizes -join ', ') px)"
