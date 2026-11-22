# Fait revenir une cle que l'appli vient d'ejecter (fin d'une cle Linux), sans la debrancher : demande a la cle de se
# "recharger", l'inverse de l'ejection. Windows relit alors la cle (il peut ouvrir "Formater le disque ?" : voir refermer.ps1).
#   -Disque : numero de la cle (Get-Disk la montre a 0 octet, "No Media")
param([Parameter(Mandatory)][int]$Disque)
Add-Type @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class Media {
  [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern SafeFileHandle CreateFile(string nom, uint acces, uint partage, IntPtr securite, uint creation, uint attributs, IntPtr modele);
  [DllImport("kernel32", SetLastError = true)]
  static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr entree, uint tailleEntree, IntPtr sortie, uint tailleSortie, out uint rendus, IntPtr attente);
  public static string Charger(int disque) {
    using (var h = CreateFile("\\\\.\\PhysicalDrive" + disque, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
      if (h.IsInvalid) return "ouverture refusee, erreur " + Marshal.GetLastWin32Error();
      uint n;
      // IOCTL_STORAGE_LOAD_MEDIA
      return DeviceIoControl(h, 0x2D480C, IntPtr.Zero, 0, IntPtr.Zero, 0, out n, IntPtr.Zero) ? "rechargement accepte" : "rechargement refuse, erreur " + Marshal.GetLastWin32Error();
    }
  }
}
'@
$d = Get-Disk -Number $Disque
if ($d.BusType -ne 'USB' -or $d.IsBoot -or $d.IsSystem) { "ARRET : le disque $Disque n'est pas une cle USB"; exit 1 }
"avant : $($d.FriendlyName), $($d.Size) octets, $($d.OperationalStatus) ; $([Media]::Charger($Disque))"
$t0 = Get-Date
do { Start-Sleep -Seconds 1; $d = Get-Disk -Number $Disque } until ($d.Size -gt 0 -or ((Get-Date) - $t0).TotalSeconds -gt 15)
"apres : $($d.Size) octets, $($d.OperationalStatus), table $($d.PartitionStyle), $($d.NumberOfPartitions) partitions"
if ($d.Size -eq 0) { exit 1 }
