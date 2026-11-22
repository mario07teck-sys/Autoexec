# Compare (lecture seule) une image .iso avec le debut d'un disque (cle ou disque virtuel monte) : liste les secteurs differents.
param([Parameter(Mandatory)][string]$Iso, [Parameter(Mandatory)][int]$Disque, [int]$Max = 12)
Add-Type @'
using System;
using System.Collections.Generic;
using System.IO;
public static class Comparer {
  public static List<string> Faire(string iso, int disque, int max) {
    var l = new List<string>();
    using (var a = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
    using (var b = new FileStream("\\\\.\\PhysicalDrive" + disque, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20)) {
      byte[] x = new byte[1 << 20], y = new byte[1 << 20];
      long pos = 0, differents = 0; int n;
      while ((n = a.Read(x, 0, x.Length)) > 0) {
        int m = 0, r;
        while (m < n && (r = b.Read(y, m, ((n - m) + 511) / 512 * 512)) > 0) m += r;
        for (int s = 0; s < n; s += 512) {
          bool egal = true; int fin = Math.Min(s + 512, n), premier = -1, combien = 0;
          for (int i = s; i < fin; i++) if (x[i] != y[i]) { egal = false; if (premier < 0) premier = i - s; combien++; }
          if (!egal) { differents++; if (l.Count < max) l.Add("secteur " + ((pos + s) / 512) + " (octet " + (pos + s) + ") : " + combien + " octets differents, le premier a +" + premier + " ; image " + BitConverter.ToString(x, s + premier, Math.Min(16, fin - s - premier)) + " ; disque " + BitConverter.ToString(y, s + premier, Math.Min(16, fin - s - premier))); }
        }
        pos += n;
      }
      l.Add(pos + " octets compares, " + differents + " secteurs differents");
    }
    return l;
  }
}
'@
[Comparer]::Faire($Iso, $Disque, $Max)
