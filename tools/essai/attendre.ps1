# Attend une etape de l'essai reel sans rien toucher, et note l'avancement avec l'heure dans un journal propre a ce lancement.
#   -Jusqua travail : jusqu'a ce que le travail en administrateur ait demarre (etat.txt existe)
#   -Jusqua fin     : jusqu'a la fin du travail (ligne fin| ou erreur| dans etat.txt)
# S'arrete aussi si l'appli se ferme, si le journal de l'appli note un echec, ou apres -MaxMinutes.
param([ValidateSet('travail', 'fin')][string]$Jusqua = 'travail', [int]$MaxMinutes = 40, [Parameter(Mandatory)][string]$Journal)
$app = "$env:LOCALAPPDATA\Autoexec"
$etat = "$app\travail\etat.txt"
$debut = Get-Date
$tailleJournal = if (Test-Path "$app\journal.log") { (Get-Item "$app\journal.log").Length } else { 0 }
function Noter($t) { Add-Content $Journal ("{0} {1}" -f (Get-Date -Format HH:mm:ss), $t) -Encoding utf8 }
Noter "attente : $Jusqua (max $MaxMinutes min)"
$raison = 'delai depasse'
$dernier = ''
while (((Get-Date) - $debut).TotalMinutes -lt $MaxMinutes) {
  Start-Sleep -Seconds 5
  $lignes = @()
  # un etat.txt plus vieux que cette attente vient d'un essai precedent : on l'ignore
  if ((Test-Path $etat) -and (Get-Item $etat).LastWriteTime -ge $debut) { try { $lignes = @(Get-Content $etat -Encoding UTF8 -ErrorAction Stop) } catch { } }
  $fin = $lignes | Where-Object { $_ -match '^(fin|erreur)\|' } | Select-Object -Last 1
  if ($Jusqua -eq 'travail' -and $lignes.Count -gt 0) { $raison = 'travail demarre'; break }
  if ($fin) { $raison = "travail fini : $fin"; break }
  if (-not (Get-Process Autoexec -ErrorAction SilentlyContinue)) { $raison = 'appli fermee'; break }
  if ((Test-Path "$app\journal.log") -and (Get-Item "$app\journal.log").Length -gt $tailleJournal -and $lignes.Count -eq 0) {
    $nouveau = Get-Content "$app\journal.log" -Encoding UTF8 -Tail 5 | Out-String
    if ($nouveau -match 'chec') { $raison = 'echec note dans le journal'; break }
  }
  # une ligne par changement visible : taille telechargee (par tranche de 0,5 Go) ou derniere ligne d'etat
  $part = Get-ChildItem "$app\cache" -Filter *.part -ErrorAction SilentlyContinue | Select-Object -First 1
  $vu = if ($lignes.Count) { $lignes[-1] } elseif ($part) { 'telecharge {0:N1} Go' -f ([math]::Floor($part.Length / 536870912) / 2) } else { '' }
  if ($vu -and $vu -ne $dernier) { $dernier = $vu; Noter $vu }
}
Noter "arret : $raison"
"raison : $raison ; duree {0:N1} min" -f ((Get-Date) - $debut).TotalMinutes
"--- journal de l'attente"
Get-Content $Journal -Encoding UTF8 -Tail 25
"--- journal de l'appli"
if (Test-Path "$app\journal.log") { Get-Content "$app\journal.log" -Encoding UTF8 -Tail 12 }
