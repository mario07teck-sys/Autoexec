# Genere l'icone d'Autoexec (src\Assets\autoexec.ico), dessin vectoriel WPF rendu a chaque taille.
# En dessous de 48 px le dessin est simplifie (chevron plus epais, cle-curseur sans details) pour rester lisible.
# Script en ASCII : PowerShell 5.1 lit en ANSI un .ps1 sans BOM.
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot
$assets = Join-Path $root 'src\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function C($hex, $a = 255) { $c = [Windows.Media.ColorConverter]::ConvertFromString($hex); $c.A = $a; $c }
function B($hex) { $b = New-Object Windows.Media.SolidColorBrush (C $hex); $b.Freeze(); $b }

# Dessine le logo dans un carre de cote $s ; les coordonnees sont celles de la planche (grille de 512)
function Draw-Logo($dc, [double]$s) {
  $k = $s / 512
  # (pas de nom d'une lettre : « R » est un alias de PowerShell et passerait devant la fonction)
  function Add-Rect($x, $y, $w, $h, $r, $fill, $pen = $null) {
    $dc.DrawRoundedRectangle($fill, $pen, (New-Object Windows.Rect ($x * $k), ($y * $k), ($w * $k), ($h * $k)), $r * $k, $r * $k)
  }
  function Add-Chevron($x1, $y1, $x2, $y2, $y3, $w) {
    $g = New-Object Windows.Media.StreamGeometry
    $ctx = $g.Open(); $ctx.BeginFigure((New-Object Windows.Point ($x1 * $k), ($y1 * $k)), $false, $false)
    $ctx.LineTo((New-Object Windows.Point ($x2 * $k), ($y2 * $k)), $true, $true)
    $ctx.LineTo((New-Object Windows.Point ($x1 * $k), ($y3 * $k)), $true, $true); $ctx.Close()
    $pen = New-Object Windows.Media.Pen (B '#BDAEF5'), ($w * $k)
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    $dc.DrawGeometry($null, $pen, $g)
  }
  if ($s -lt 48) {
    Add-Rect 8 8 496 496 112 (B '#1D1A2B')
    Add-Rect 16 16 480 480 104 $null (New-Object Windows.Media.Pen (B '#3B3555'), (16 * $k))
    Add-Chevron 112 146 246 256 366 72
    Add-Rect 278 306 118 84 14 (B '#BFF3D6')
    Add-Rect 386 318 50 60 8 (B '#F5FFFA')
  } else {
    Add-Rect 16 16 480 480 108 (B '#1D1A2B')
    $reflet = New-Object Windows.Media.LinearGradientBrush
    $reflet.StartPoint = New-Object Windows.Point 0, 0; $reflet.EndPoint = New-Object Windows.Point 1, 1
    $reflet.GradientStops.Add((New-Object Windows.Media.GradientStop (C '#FFFFFF' 36), 0))
    $reflet.GradientStops.Add((New-Object Windows.Media.GradientStop (C '#FFFFFF' 0), 0.6))
    Add-Rect 16 16 480 480 108 $reflet
    Add-Rect 18 18 476 476 106 $null (New-Object Windows.Media.Pen (B '#3B3555'), (4 * $k))
    Add-Chevron 112 164 226 256 348 50
    Add-Rect 258 304 116 64 18 (B '#BFF3D6')
    Add-Rect 366 314 54 44 7 (B '#F5FFFA')
    Add-Rect 390 322 13 10 2 (B '#1D1A2B')
    Add-Rect 390 340 13 10 2 (B '#1D1A2B')
  }
}

function Render([int]$s) {
  $v = New-Object Windows.Media.DrawingVisual
  $dc = $v.RenderOpen(); Draw-Logo $dc $s; $dc.Close()
  $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
  $bmp.Render($v); $bmp
}
function Png($bmp) {
  $e = New-Object Windows.Media.Imaging.PngBitmapEncoder
  $e.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $ms = New-Object IO.MemoryStream; $e.Save($ms); , $ms.ToArray()
}
# jusqu'a 48 px : entrees DIB classiques (lues par tous les composants Windows) ; au-dela : PNG
function Dib($bmp) {
  $s = $bmp.PixelWidth; $cv = New-Object Windows.Media.Imaging.FormatConvertedBitmap $bmp, ([Windows.Media.PixelFormats]::Bgra32), $null, 0
  $px = New-Object byte[] ($s * $s * 4); $cv.CopyPixels($px, $s * 4, 0)
  $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
  $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]0); $w.Write([uint32]($s * $s * 4)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
  for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($px, $y * $s * 4, $s * 4) }
  $mask = [Math]::Ceiling($s / 32) * 4; $w.Write((New-Object byte[] ($mask * $s)))
  $w.Flush(); , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($sz in $sizes) { $r = Render $sz; if ($sz -le 48) { , (Dib $r) } else { , (Png $r) } }
$ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $d = $sizes[$i] % 256
  $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$off)
  $off += $images[$i].Length
}
foreach ($p in $images) { $bw.Write([byte[]]$p) }
$bw.Flush(); [IO.File]::WriteAllBytes((Join-Path $assets 'autoexec.ico'), $ms.ToArray())

# apercu de controle : toutes les tailles cote a cote sur fond sombre puis clair
$apercu = Join-Path $root 'obj\icone-apercu.png'
New-Item -ItemType Directory -Force (Split-Path $apercu) | Out-Null
$v = New-Object Windows.Media.DrawingVisual
$dc = $v.RenderOpen()
$dc.DrawRectangle((B '#171B20'), $null, (New-Object Windows.Rect 0, 0, 760, 300))
$dc.DrawRectangle((B '#E7ECF1'), $null, (New-Object Windows.Rect 0, 300, 760, 100))
$x = 20
foreach ($sz in 256, 128, 96, 64, 48) { $dc.DrawImage((Render $sz), (New-Object Windows.Rect $x, 20, $sz, $sz)); $x += $sz + 16 }
$x = 20
foreach ($sz in 48, 40, 32, 24, 20, 16) { $dc.DrawImage((Render $sz), (New-Object Windows.Rect $x, 326, $sz, $sz)); $x += $sz + 20 }
$dc.Close()
$bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap 760, 400, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($v)
[IO.File]::WriteAllBytes($apercu, (Png $bmp))
"Icone generee : $(Join-Path $assets 'autoexec.ico')"
