# Demarre des machines virtuelles sur l'image exacte d'une distribution (ce que l'appli ecrit sur la cle) :
# recente avec Secure Boot (uefi), recente sans Secure Boot (uefi-sans), ancienne (bios). Capture l'ecran au menu, puis
# deux fois apres le demarrage. Les machines sont supprimees a la fin, et l'image exacte aussi sauf -Garder.
# Les captures vont dans <Dossier>\vues-<Nom>.
#   -Nom ubuntu   -Iso <fichier .iso>   -Dossier <dossier des disques virtuels>   [-Genres uefi,bios]   [-Attente 110]
param([Parameter(Mandatory)][string]$Nom, [Parameter(Mandatory)][string]$Iso, [Parameter(Mandatory)][string]$Dossier, [int]$Attente = 110,
      [string[]]$Genres = @('uefi', 'uefi-sans', 'bios'), [switch]$Garder)
$ErrorActionPreference = 'Stop'
$T = Split-Path -Parent $MyInvocation.MyCommand.Path
$D = $Dossier
$E = "$Dossier\vues-$Nom"; New-Item -ItemType Directory -Force $E | Out-Null
function Note($t) { $l = "$(Get-Date -Format HH:mm:ss) $t"; Add-Content "$E\vm.log" $l -Encoding utf8; $l }
$vhdx = "$D\exact-$Nom.vhdx"
# un disque virtuel plus grand que le fichier, comme une cle
$go = [int][math]::Ceiling((Get-Item $Iso).Length / 1GB) + 2
if (-not (Test-Path $vhdx)) { Note (& "$T\iso-vers-vhdx.ps1" -Iso $Iso -Vhdx $vhdx -Go $go | Out-String).Trim() }
foreach ($genre in $Genres) {
  $vm = "Autoexec-$Nom-$genre"
  try {
    if (Get-VM -Name $vm -ErrorAction SilentlyContinue) { Stop-VM -Name $vm -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -Name $vm -Force }
    if ($genre -eq 'bios') { New-VM -Name $vm -Generation 1 -MemoryStartupBytes 6GB -VHDPath $vhdx -Path $D | Out-Null }
    else {
      New-VM -Name $vm -Generation 2 -MemoryStartupBytes 6GB -NoVHD -Path $D | Out-Null
      Add-VMHardDiskDrive -VMName $vm -Path $vhdx
      if ($genre -eq 'uefi') { Set-VMFirmware -VMName $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority -FirstBootDevice (Get-VMHardDiskDrive -VMName $vm) }
      else { Set-VMFirmware -VMName $vm -EnableSecureBoot Off -FirstBootDevice (Get-VMHardDiskDrive -VMName $vm) }
    }
    Set-VM -Name $vm -ProcessorCount 4 -StaticMemory -AutomaticCheckpointsEnabled $false
    Get-VMNetworkAdapter -VMName $vm | Remove-VMNetworkAdapter
    Start-VM -Name $vm
    Note "$vm demarree$(if ($genre -ne 'bios') { ', Secure Boot ' + (Get-VMFirmware -VMName $vm).SecureBoot })"
    Start-Sleep -Seconds 20
    Note (& "$T\vm.ps1" -Nom $vm -Capture "$E\$genre-1.png")
    # un menu de demarrage sans compte a rebours attendrait : Entree lance le choix par defaut
    & "$T\vm.ps1" -Nom $vm -Touches ENTER | Out-Null
    Start-Sleep -Seconds ([int]($Attente / 2))
    Note (& "$T\vm.ps1" -Nom $vm -Capture "$E\$genre-2.png")
    Start-Sleep -Seconds ([int]($Attente / 2))
    Note (& "$T\vm.ps1" -Nom $vm -Capture "$E\$genre-3.png")
  }
  catch { Note "ERREUR $vm : $($_.Exception.Message)" }
  finally {
    if (Get-VM -Name $vm -ErrorAction SilentlyContinue) { Stop-VM -Name $vm -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -Name $vm -Force; Note "$vm supprimee" }
  }
}
if (-not $Garder) { Remove-Item $vhdx -Force -ErrorAction SilentlyContinue; Note "image exacte supprimee" }
