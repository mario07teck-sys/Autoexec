# Pilote la machine virtuelle d'essai (Hyper-V) sans souris ni fenetre : creation, capture de son ecran, frappes au clavier.
#   -Creer -Cle <cle.vhdx> -Dossier <dossier>  cree la machine : UEFI, Secure Boot, TPM, sans reseau, disque vide de 64 Go ; demarre sur l'image de la cle
#   -Capture <fichier.png>                     capture l'ecran de la machine (et lui seul)
#   -Touches 'TAB,ENTER,ALT+N,SHIFT+TAB'       frappe ces touches dans l'ordre
#   -Texte 'test'                              tape un texte ; la machine le lit comme un clavier americain : s'en tenir aux lettres communes a AZERTY (pas a q w z m)
#   -SansTpm (avec -Creer)                     machine sans TPM ni Secure Boot : celle que Windows 11 refuse sans le reglage "PC sans TPM"
param([string]$Nom = 'Autoexec-essai', [switch]$Creer, [switch]$SansTpm, [string]$Cle, [string]$Dossier, [string]$Capture, [string]$Touches, [string]$Texte, [int]$Pause = 400)
$ErrorActionPreference = 'Stop'
$ns = 'root\virtualization\v2'
if ($Creer) {
  New-VM -Name $Nom -Generation 2 -MemoryStartupBytes 6GB -NoVHD -Path $Dossier | Out-Null
  Set-VM -Name $Nom -ProcessorCount 4 -StaticMemory -AutomaticCheckpointsEnabled $false -CheckpointType Standard
  Get-VMNetworkAdapter -VMName $Nom | Remove-VMNetworkAdapter
  New-VHD -Path "$Dossier\$Nom-systeme.vhdx" -SizeBytes 64GB -Dynamic | Out-Null
  Add-VMHardDiskDrive -VMName $Nom -Path $Cle
  Add-VMHardDiskDrive -VMName $Nom -Path "$Dossier\$Nom-systeme.vhdx"
  $disqueCle = Get-VMHardDiskDrive -VMName $Nom | Where-Object Path -eq $Cle
  if ($SansTpm) { Set-VMFirmware -VMName $Nom -EnableSecureBoot Off -FirstBootDevice $disqueCle }
  else {
    Set-VMFirmware -VMName $Nom -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows -FirstBootDevice $disqueCle
    Set-VMKeyProtector -VMName $Nom -NewLocalKeyProtector
    Enable-VMTPM -VMName $Nom
  }
  Get-VM -Name $Nom | Select-Object Name, State, Generation, ProcessorCount, @{n='MemoireGo';e={$_.MemoryStartup / 1GB}} | Format-List
  Get-VMHardDiskDrive -VMName $Nom | Select-Object ControllerLocation, Path | Format-Table -AutoSize
  exit 0
}
$sys = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$Nom'"
if (-not $sys) { "pas de machine $Nom"; exit 1 }
if ($Touches -or $Texte) {
  $clavier = Get-CimInstance -Namespace $ns -ClassName Msvm_Keyboard | Where-Object SystemName -eq $sys.Name
  $codes = @{ ENTER = 13; TAB = 9; ESC = 27; SPACE = 32; BACK = 8; DEL = 46; HOME = 36; END = 35; LEFT = 37; UP = 38; RIGHT = 39; DOWN = 40; ALT = 18; SHIFT = 16; CTRL = 17; WIN = 91; F5 = 116; F10 = 121 }
  function Code($t) { if ($codes.ContainsKey($t)) { $codes[$t] } elseif ($t.Length -eq 1) { [int][char]$t.ToUpper() } else { throw "touche inconnue : $t" } }
  function Appel($methode, $code) { Invoke-CimMethod -InputObject $clavier -MethodName $methode -Arguments @{ keyCode = [uint32]$code } | Out-Null }
  foreach ($t in ($Touches -split ',' | Where-Object { $_ })) {
    $parts = $t.Trim().ToUpper() -split '\+'
    $tenues = @($parts | Select-Object -SkipLast 1)
    foreach ($m in $tenues) { Appel PressKey (Code $m) }
    Appel TypeKey (Code $parts[-1])
    foreach ($m in $tenues) { Appel ReleaseKey (Code $m) }
    Start-Sleep -Milliseconds $Pause
  }
  if ($Texte) { Invoke-CimMethod -InputObject $clavier -MethodName TypeText -Arguments @{ asciiText = $Texte } | Out-Null; Start-Sleep -Milliseconds $Pause }
}
if ($Capture) {
  Add-Type -AssemblyName System.Drawing
  $tete = Get-CimInstance -Namespace $ns -ClassName Msvm_VideoHead | Where-Object SystemName -eq $sys.Name | Select-Object -First 1
  $w = if ($tete.CurrentHorizontalResolution) { [int]$tete.CurrentHorizontalResolution } else { 1024 }
  $h = if ($tete.CurrentVerticalResolution) { [int]$tete.CurrentVerticalResolution } else { 768 }
  $svc = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
  $reglages = Get-CimAssociatedInstance -InputObject $sys -ResultClassName Msvm_VirtualSystemSettingData -Association Msvm_SettingsDefineState
  $r = Invoke-CimMethod -InputObject $svc -MethodName GetVirtualSystemThumbnailImage -Arguments @{ TargetSystem = $reglages; WidthPixels = [uint16]$w; HeightPixels = [uint16]$h }
  if ($r.ReturnValue -ne 0 -or -not $r.ImageData) { "capture refusee (code $($r.ReturnValue))"; exit 1 }
  # l'image arrive en 16 bits par point (RGB565), ligne par ligne
  $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format16bppRgb565)
  $d = $bmp.LockBits((New-Object Drawing.Rectangle 0, 0, $w, $h), 'WriteOnly', $bmp.PixelFormat)
  for ($y = 0; $y -lt $h; $y++) { [Runtime.InteropServices.Marshal]::Copy([byte[]]$r.ImageData, $y * $w * 2, [IntPtr]($d.Scan0.ToInt64() + $y * $d.Stride), $w * 2) }
  $bmp.UnlockBits($d)
  $bmp.Save($Capture, [Drawing.Imaging.ImageFormat]::Png)
  "$w x $h -> $Capture"
}
