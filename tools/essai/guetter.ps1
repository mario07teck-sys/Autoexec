# Note, avec l'heure, toute fenetre visible qui apparait a l'ecran pendant l'essai (titre, classe, processus).
# Sert a savoir si Windows fait surgir quelque chose (formater le disque ?, Explorateur, lecture automatique) pendant qu'Autoexec fait la cle.
# Lecture seule : ne ferme rien, ne clique rien. S'arrete quand etat.txt porte fin| ou erreur|, quand Autoexec se ferme, ou apres -MaxMinutes.
# Avec -Secondes : guette simplement pendant cette duree, sans regarder l'appli (pour un essai a la main).
param([Parameter(Mandatory)][string]$Journal, [int]$MaxMinutes = 120, [int]$Secondes = 0)
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Guet {
  delegate bool Proc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(Proc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static List<string> Visibles() {
    var l = new List<string>();
    EnumWindows((h, p) => {
      if (!IsWindowVisible(h)) return true;
      var t = new StringBuilder(300); GetWindowText(h, t, 300);
      var c = new StringBuilder(200); GetClassName(h, c, 200);
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (t.Length > 0 || c.ToString() == "#32770") l.Add(h.ToInt64() + "|" + pid + "|" + c + "|" + t);
      return true;
    }, IntPtr.Zero);
    return l;
  }
}
'@
$etat = "$env:LOCALAPPDATA\Autoexec\travail\etat.txt"
$debut = Get-Date
function Noter($t) { Add-Content $Journal ("{0} {1}" -f (Get-Date -Format HH:mm:ss), $t) -Encoding utf8 }
# une fenetre est reconnue a son numero, pas a son titre : un titre qui change (terminal, navigateur) n'est pas une fenetre qui surgit
$vues = @{}
foreach ($f in [Guet]::Visibles()) { $vues[$f.Split('|')[0]] = 1 }
Noter ("depart : {0} fenetres deja la" -f $vues.Count)
$derniereEtape = ''
$raison = 'delai depasse'
while (((Get-Date) - $debut).TotalMinutes -lt $MaxMinutes) {
  Start-Sleep -Milliseconds 400
  foreach ($f in [Guet]::Visibles()) {
    $m = $f.Split('|', 4)
    if (-not $vues.ContainsKey($m[0])) {
      $vues[$m[0]] = 1
      $nom = try { (Get-Process -Id ([int]$m[1]) -ErrorAction Stop).Name } catch { '?' }
      Noter ("NOUVELLE FENETRE  processus={0}  classe={1}  titre={2}" -f $nom, $m[2], $m[3])
    }
  }
  if ($Secondes -gt 0) { if (((Get-Date) - $debut).TotalSeconds -ge $Secondes) { $raison = 'duree ecoulee'; break }; continue }
  $ligne = ''
  # un etat.txt plus vieux que ce guet vient d'un essai precedent : on l'ignore
  if ((Test-Path $etat) -and (Get-Item $etat).LastWriteTime -ge $debut) { try { $ligne = @(Get-Content $etat -Tail 1 -ErrorAction Stop)[0] } catch { } }
  $etape = ($ligne -split '\|')[0]
  if ($etape -and $etape -ne $derniereEtape) { $derniereEtape = $etape; Noter "etape : $ligne" }
  if ($ligne -match '^(fin|erreur)\|') { Start-Sleep -Seconds 8; foreach ($f in [Guet]::Visibles()) { if (-not $vues.ContainsKey($f.Split('|')[0])) { $vues[$f.Split('|')[0]] = 1; Noter "NOUVELLE FENETRE (apres la fin)  $f" } }; $raison = "travail fini : $ligne"; break }
  if (-not (Get-Process Autoexec -ErrorAction SilentlyContinue)) { $raison = 'appli fermee'; break }
}
Noter "arret : $raison"
Get-Content $Journal -Encoding UTF8
