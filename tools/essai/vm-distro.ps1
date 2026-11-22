# Demarre deux machines virtuelles sur l'image exacte d'une distribution (ce que l'appli ecrit sur la cle) :
# une recente (UEFI, Secure Boot active) puis une ancienne (BIOS). Capture l'ecran au menu puis apres le demarrage.
# Les machines sont supprimees a la fin ; l'image exacte est gardee. Les captures vont dans <Dossier>\vues-<Nom>.
#   -Nom ubuntu   -Iso <fichier .iso>   -Dossier <dossier des disques virtuels>
param([Parameter(Mandatory)][string]$Nom, [Parameter(Mandatory)][string]$Iso, [Parameter(Mandatory)][string]$Dossier, [int]$Attente = 110)
$ErrorActionPreference = 'Stop'
$L = Split-Path -Parent $MyInvocation.MyCommand.Path
$T = $L
$D = $Dossier
$E = "$Dossier\vues-$Nom"; New-Item -ItemType Directory -Force $E | Out-Null
function Note($t) { $l = "$(Get-Date -Format HH:mm:ss) $t"; Add-Content "$E\vm.log" $l -Encoding utf8; $l }
$vhdx = "$D\exact-$Nom.vhdx"
if (-not (Test-Path $vhdx)) { Note (& "$L\iso-vers-vhdx.ps1" -Iso $Iso -Vhdx $vhdx -Go 8 | Out-String).Trim() }
foreach ($genre in 'uefi', 'bios') {
  $vm = "Autoexec-$Nom-$genre"
  if (Get-VM -Name $vm -ErrorAction SilentlyContinue) { Stop-VM -Name $vm -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -Name $vm -Force }
  if ($genre -eq 'uefi') {
    New-VM -Name $vm -Generation 2 -MemoryStartupBytes 6GB -NoVHD -Path $D | Out-Null
    Add-VMHardDiskDrive -VMName $vm -Path $vhdx
    Set-VMFirmware -VMName $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority -FirstBootDevice (Get-VMHardDiskDrive -VMName $vm)
  }
  else { New-VM -Name $vm -Generation 1 -MemoryStartupBytes 4GB -VHDPath $vhdx -Path $D | Out-Null }
  Set-VM -Name $vm -ProcessorCount 4 -StaticMemory -AutomaticCheckpointsEnabled $false
  Get-VMNetworkAdapter -VMName $vm | Remove-VMNetworkAdapter
  Start-VM -Name $vm
  Note "$vm demarree$(if ($genre -eq 'uefi') { ', Secure Boot ' + (Get-VMFirmware -VMName $vm).SecureBoot })"
  Start-Sleep -Seconds 20
  Note (& "$T\vm.ps1" -Nom $vm -Capture "$E\$genre-1.png")
  # un menu de demarrage sans compte a rebours attendrait : Entree lance le choix par defaut
  & "$T\vm.ps1" -Nom $vm -Touches ENTER
  Start-Sleep -Seconds $Attente
  Note (& "$T\vm.ps1" -Nom $vm -Capture "$E\$genre-2.png")
  Stop-VM -Name $vm -TurnOff -Force
  Remove-VM -Name $vm -Force
  Note "$vm supprimee"
}
