# Fait une image exacte (secteur par secteur) d'une cle USB dans un disque virtuel .vhdx, pour demarrer une machine Hyper-V dessus.
# La cle n'est que lue. A la fin, l'image est relue et son empreinte comparee a celle de la cle.
#   -Disque : numero de la cle (Get-Disk)   -Vhdx : fichier a creer (ne doit pas exister)
#   -Limite : pour un essai court, ne copier que les premiers octets
param([Parameter(Mandatory)][int]$Disque, [Parameter(Mandatory)][string]$Vhdx, [long]$Limite = 0)
$ErrorActionPreference = 'Stop'
$cle = Get-Disk -Number $Disque
if ($cle.BusType -ne 'USB' -or $cle.IsBoot -or $cle.IsSystem) { "ARRET : le disque $Disque n'est pas une cle USB"; exit 1 }
if (Test-Path $Vhdx) { "ARRET : $Vhdx existe deja"; exit 1 }
# on copie du debut de la cle a la fin de sa derniere partition, arrondi au bloc de 4 Mo
$fin = (Get-Partition -DiskNumber $Disque | ForEach-Object { $_.Offset + $_.Size } | Measure-Object -Maximum).Maximum
if ($Limite -gt 0 -and $Limite -lt $fin) { $fin = $Limite }
$fin = [long]([math]::Ceiling($fin / 4MB) * 4MB)
$taille = [long]([math]::Ceiling($fin / 1GB) * 1GB)
Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
public static class ImageCle {
  [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern SafeFileHandle CreateFile(string nom, uint acces, uint partage, IntPtr securite, uint creation, uint attributs, IntPtr modele);
  static FileStream Ouvrir(int disque, bool ecriture) {
    SafeFileHandle h = CreateFile(@"\\.\PhysicalDrive" + disque, ecriture ? 0xC0000000u : 0x80000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
    if (h.IsInvalid) throw new IOException("Disque " + disque + " : erreur Windows " + Marshal.GetLastWin32Error());
    return new FileStream(h, ecriture ? FileAccess.ReadWrite : FileAccess.Read, 4096);
  }
  static void Lire(FileStream f, byte[] b) {
    int n = 0;
    while (n < b.Length) { int r = f.Read(b, n, b.Length - n); if (r <= 0) throw new IOException("Lecture courte"); n += r; }
  }
  static bool Vide(byte[] b) { for (int i = 0; i < b.Length; i++) if (b[i] != 0) return false; return true; }
  static string Hex(byte[] h) { return BitConverter.ToString(h).Replace("-", ""); }
  // Renvoie "empreinte de la cle, empreinte de l'image relue, octets ecrits".
  public static string Copier(int source, int cible, long longueur) {
    const int Bloc = 4 << 20;
    byte[] b = new byte[Bloc], premier = new byte[Bloc];
    long ecrits = Bloc;
    string empreinteSource, empreinteCible;
    using (FileStream s = Ouvrir(source, false))
    using (FileStream c = Ouvrir(cible, true)) {
      using (SHA256 sha = SHA256.Create()) {
        for (long p = 0; p < longueur; p += Bloc) {
          Lire(s, b);
          sha.TransformBlock(b, 0, Bloc, null, 0);
          if (p == 0) { Buffer.BlockCopy(b, 0, premier, 0, Bloc); continue; }
          // un bloc de zeros n'est pas ecrit : le disque virtuel reste petit et se relit quand meme en zeros
          if (!Vide(b)) { c.Position = p; c.Write(b, 0, Bloc); ecrits += Bloc; }
          if (p % (4096L << 20) == 0) Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " lu " + (p >> 30) + " Go, ecrit " + (ecrits >> 20) + " Mo");
        }
        sha.TransformFinalBlock(b, 0, 0);
        empreinteSource = Hex(sha.Hash);
      }
      // le premier bloc porte la table de partitions : ecrit en dernier, Windows ne decouvre le volume qu'une fois l'image complete
      c.Position = 0; c.Write(premier, 0, Bloc); c.Flush(true);
      Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " copie finie, relecture de l'image");
      using (SHA256 sha = SHA256.Create()) {
        c.Position = 0;
        for (long p = 0; p < longueur; p += Bloc) { Lire(c, b); sha.TransformBlock(b, 0, Bloc, null, 0); }
        sha.TransformFinalBlock(b, 0, 0);
        empreinteCible = Hex(sha.Hash);
      }
    }
    return empreinteSource + " " + empreinteCible + " " + ecrits;
  }
}
'@
"$(Get-Date -Format HH:mm:ss) cle : disque $Disque ($($cle.FriendlyName)), $fin octets a copier vers $Vhdx"
New-VHD -Path $Vhdx -SizeBytes $taille -Dynamic | Out-Null
try {
  $v = Mount-VHD -Path $Vhdx -NoDriveLetter -Passthru | Get-Disk
  # garde-fou : on n'ecrit que sur le disque virtuel qu'on vient de creer, vide et de la taille demandee
  if ($v.BusType -ne 'File Backed Virtual' -or $v.Size -ne $taille -or $v.PartitionStyle -ne 'RAW' -or $v.Number -eq $Disque) { "ARRET : disque virtuel inattendu ($($v.Number), $($v.BusType), $($v.PartitionStyle))"; exit 1 }
  $r = [ImageCle]::Copier($Disque, $v.Number, $fin) -split ' '
}
finally { Dismount-VHD -Path $Vhdx -ErrorAction SilentlyContinue }
"$(Get-Date -Format HH:mm:ss) cle   : $($r[0])"
"$(Get-Date -Format HH:mm:ss) image : $($r[1])"
"ecrit : {0:N1} Go ; fichier : {1:N1} Go" -f ($r[2] / 1GB), ((Get-Item $Vhdx).Length / 1GB)
if ($r[0] -eq $r[1]) { 'IMAGE IDENTIQUE A LA CLE' } else { 'IMAGE DIFFERENTE DE LA CLE'; exit 1 }
