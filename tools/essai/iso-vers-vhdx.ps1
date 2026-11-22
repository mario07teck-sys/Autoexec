# Copie un fichier .iso tel quel au debut d'un disque virtuel neuf (plus grand que lui, comme une cle) : c'est, octet pour
# octet, ce que l'appli ecrit sur la cle puis verifie. Sert a demarrer une machine virtuelle sur une cle que Windows n'a pas relue.
param([Parameter(Mandatory)][string]$Iso, [Parameter(Mandatory)][string]$Vhdx, [int]$Go = 8)
$ErrorActionPreference = 'Stop'
if (Test-Path $Vhdx) { "ARRET : $Vhdx existe deja"; exit 1 }
$taille = [long]$Go * 1GB
New-VHD -Path $Vhdx -SizeBytes $taille -Dynamic | Out-Null
try {
  $v = Mount-VHD -Path $Vhdx -NoDriveLetter -Passthru | Get-Disk
  # garde-fou : on n'ecrit que sur le disque virtuel qu'on vient de creer, vide et de la taille demandee
  if ($v.BusType -ne 'File Backed Virtual' -or $v.Size -ne $taille -or $v.PartitionStyle -ne 'RAW') { "ARRET : disque virtuel inattendu ($($v.Number), $($v.BusType), $($v.PartitionStyle))"; exit 1 }
  $src = [IO.File]::OpenRead($Iso)
  $dst = [IO.File]::Open("\\.\PhysicalDrive$($v.Number)", 'Open', 'ReadWrite', 'ReadWrite')
  try {
    $b = New-Object byte[] (4MB); $total = 0L
    while (($n = $src.Read($b, 0, $b.Length)) -gt 0) { $m = [int]([math]::Ceiling($n / 4096) * 4096); if ($m -gt $n) { [Array]::Clear($b, $n, $m - $n) }; $dst.Write($b, 0, $m); $total += $n }
    $dst.Flush()
  }
  finally { $src.Close(); $dst.Close() }
  "$total octets copies vers le disque virtuel $($v.Number)"
}
finally { Dismount-VHD -Path $Vhdx -ErrorAction SilentlyContinue }
