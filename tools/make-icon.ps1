# Membuat src\ZaxLet\Assets\zaxlet.ico (7 ukuran, PNG di dalam ICO). Jalankan: powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\ZaxLet\Assets\zaxlet.ico'

function RoundRect($x, $y, $w, $h, $r) {
  $p = New-Object Drawing.Drawing2D.GraphicsPath
  $d = 2 * $r
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure(); return $p
}

function Frame([int]$S) {
  $u = $S / 256.0
  $bmp = New-Object Drawing.Bitmap $S, $S, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
  $sq = RoundRect (6 * $u) (6 * $u) (244 * $u) (244 * $u) (56 * $u)

  # latar: gradasi teal -> biru -> indigo
  $bg = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $S, $S), [Drawing.Color]::FromArgb(255, 45, 212, 191), [Drawing.Color]::FromArgb(255, 79, 70, 229))
  $g.FillPath($bg, $sq)
  # kilau kaca di separuh atas
  $g.SetClip($sq)
  $gloss = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF 0, ($S * 0.55)), [Drawing.Color]::FromArgb(70, 255, 255, 255), [Drawing.Color]::FromArgb(0, 255, 255, 255))
  $g.FillRectangle($gloss, 0, 0, $S, $S * 0.55)
  $g.ResetClip()

  # pill hitam bergaya notch (bahu cekung di atas)
  $pill = New-Object Drawing.Drawing2D.GraphicsPath
  $pill.AddLine(46 * $u, 6 * $u, 210 * $u, 6 * $u)
  $pill.AddArc(196 * $u, 6 * $u, 28 * $u, 28 * $u, 270, -90)
  $pill.AddLine(196 * $u, 20 * $u, 196 * $u, 46 * $u)
  $pill.AddArc(136 * $u, 16 * $u, 60 * $u, 60 * $u, 0, 90)
  $pill.AddLine(166 * $u, 76 * $u, 90 * $u, 76 * $u)
  $pill.AddArc(60 * $u, 16 * $u, 60 * $u, 60 * $u, 90, 90)
  $pill.AddLine(60 * $u, 46 * $u, 60 * $u, 20 * $u)
  $pill.AddArc(32 * $u, 6 * $u, 28 * $u, 28 * $u, 0, -90)
  $pill.CloseFigure()
  $g.SetClip($sq)
  $shadow = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(70, 0, 0, 0))
  $g.TranslateTransform(0, 5 * $u); $g.FillPath($shadow, $pill); $g.ResetTransform()
  $pb = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF 0, (6 * $u)), (New-Object Drawing.PointF 0, (76 * $u)), [Drawing.Color]::FromArgb(255, 6, 6, 9), [Drawing.Color]::FromArgb(255, 24, 24, 32))
  $g.FillPath($pb, $pill)
  $g.ResetClip()

  if ($S -ge 48) {
    # isi pill: jam di tengah, titik kiri, batang kanan
    $f = New-Object Drawing.Font('Segoe UI', (27 * $u), [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString('17.52', $f, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF (60 * $u), (20 * $u), (136 * $u), (44 * $u)), $sf)
    $g.FillEllipse((New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255, 34, 211, 90))), (72 * $u), (36 * $u), (12 * $u), (12 * $u))
  }

  # cincin usage (gauge) di bawah pill
  $cx = 128 * $u; $cy = 166 * $u; $r = 52 * $u; $pw = 18 * $u
  $rect = New-Object Drawing.RectangleF ($cx - $r), ($cy - $r), (2 * $r), (2 * $r)
  $track = New-Object Drawing.Pen([Drawing.Color]::FromArgb(70, 255, 255, 255), $pw)
  $arc = New-Object Drawing.Pen([Drawing.Color]::FromArgb(255, 255, 255, 255), $pw); $arc.StartCap = 'Round'; $arc.EndCap = 'Round'
  $g.DrawEllipse($track, $rect)
  $g.DrawArc($arc, $rect, -90, 250)

  $g.Dispose()
  return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { $b = Frame $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray() }
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
# pratinjau 256 px untuk dilihat
$prev = Frame 256; $prev.Save((Join-Path $PSScriptRoot 'icon-preview.png')); $prev.Dispose()
"ikon: $out ($((Get-Item $out).Length) byte)"
