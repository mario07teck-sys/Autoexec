using System.Management;

namespace Autoexec.Core;

/// <summary>Une clé USB branchée. Chemin = identité du matériel, revérifiée juste avant d'effacer.</summary>
record Cle(int Numero, string Nom, long Taille, string Chemin, bool Protegee)
{
    // même unité que l'Explorateur de Windows (1 Go = 1024³ octets), pour que la taille affichée soit celle que l'on connaît
    public string Go => Textes.T("{0} Go", Math.Round(Taille / 1073741824.0));
    public string Detail => Go + Textes.T(Protegee ? " · protégée en écriture" : " · tout sera effacé");
}

static class Cles
{
    // garde-fou de plus : une clé d'installation n'a pas besoin de moins de 7 Go, et au-delà de 300 Go ce n'est plus une clé
    public const long Mini = 7_000_000_000, Maxi = 300_000_000_000;

    const string Stockage = @"root\Microsoft\Windows\Storage";

    /// <summary>Clés USB seulement : jamais le disque du système, celui d'où tourne l'appli, ni un disque dur ou SSD externe.</summary>
    public static List<Cle> Lister()
    {
        // Windows range les clés en « Removable Media » et les disques durs/SSD externes en « External hard disk media ».
        // shortcut: de rares clés haut de gamme se déclarent comme des disques fixes et ne seront donc pas proposées
        // (même choix par défaut que Rufus) ; à ouvrir par un réglage si quelqu'un en a besoin.
        var amovibles = new HashSet<int>();
        using (var s = new ManagementObjectSearcher("SELECT Index, MediaType FROM Win32_DiskDrive"))
            foreach (ManagementObject d in s.Get())
                using (d)
                    if ((d["MediaType"] as string ?? "").StartsWith("Removable", StringComparison.OrdinalIgnoreCase))
                        amovibles.Add(Convert.ToInt32(d["Index"]));

        // disques à ne jamais proposer : ceux qui portent l'appli, ses données ou Windows (une appli lancée depuis une clé
        // ne doit pas pouvoir effacer cette clé)
        var lettres = new[] { AppContext.BaseDirectory, Journal.Dossier, Environment.SystemDirectory }
            .Select(c => char.ToUpperInvariant(Path.GetPathRoot(Path.GetFullPath(c))[0])).ToHashSet();
        var interdits = new HashSet<int>();
        using (var s = new ManagementObjectSearcher(Stockage, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
            foreach (ManagementObject p in s.Get())
                using (p)
                    if (lettres.Contains(char.ToUpperInvariant(Convert.ToChar(p["DriveLetter"] ?? '\0'))))
                        interdits.Add(Convert.ToInt32(p["DiskNumber"]));

        var cles = new List<Cle>();
        using var disques = new ManagementObjectSearcher(Stockage,
            "SELECT Number, FriendlyName, Size, Path, IsBoot, IsSystem, IsReadOnly FROM MSFT_Disk WHERE BusType = 7"); // 7 = USB
        foreach (ManagementObject d in disques.Get())
            using (d)
            {
                int numero = Convert.ToInt32(d["Number"]);
                long taille = Convert.ToInt64(d["Size"] ?? 0);
                if (!amovibles.Contains(numero) || interdits.Contains(numero) || (bool)(d["IsBoot"] ?? false) || (bool)(d["IsSystem"] ?? false)
                    || taille < Mini || taille > Maxi) continue;
                var nom = (d["FriendlyName"] as string ?? Textes.T("Clé USB")).Replace("USB ", "").Trim();
                cles.Add(new Cle(numero, nom, taille, d["Path"] as string ?? "", (bool)(d["IsReadOnly"] ?? false)));
            }
        return cles.OrderBy(c => c.Numero).ToList();
    }
}
