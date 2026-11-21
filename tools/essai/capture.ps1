# Capture la fenetre principale d'un processus (et elle seule) par PrintWindow : rien d'autre de l'ecran n'est pris.
# -Ecran : capture a la place le rectangle de l'ecran que la fenetre occupe, apres l'avoir mise devant. C'est le seul moyen
#          de voir ce que PrintWindow ne rend pas : une liste deroulante ouverte, les angles arrondis poses par Windows.
param([string]$Processus = 'Autoexec', [Parameter(Mandatory)][string]$Sortie, [int]$Id = 0, [switch]$Ecran)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Fen {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
[Fen]::SetProcessDPIAware() | Out-Null
$p = Get-Process $Processus -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and ($Id -eq 0 -or $_.Id -eq $Id) } | Select-Object -First 1
if (-not $p) { "pas de fenetre pour $Processus"; exit 1 }
$r = New-Object Fen+RECT
[Fen]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
if ($Ecran) {
  [Fen]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 500
  $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
  $g.Dispose()
} else {
  $hdc = $g.GetHdc()
  [Fen]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc); $g.Dispose()
}
$bmp.Save($Sortie, [System.Drawing.Imaging.ImageFormat]::Png)
"$w x $h -> $Sortie"
