using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autoexec.Core;

/// <summary>Ce que la fenêtre doit savoir d'une distribution. Légère : il existe une version au bureau plus simple, pour un
/// PC ancien. Essai : la clé permet d'essayer sans installer (sinon c'est un installeur seul). SecureBoot : la clé démarre
/// Secure Boot activé. CarteGraphique : le fichier dépend de la carte graphique du PC à équiper.</summary>
record Distribution(string Nom, string Note, bool Legere = false, bool Essai = true, bool SecureBoot = true, bool CarteGraphique = false);

/// <summary>Les distributions Linux proposées. Rien n'est figé dans l'appli : chaque éditeur publie le nom, la taille et
/// l'empreinte SHA-256 de sa dernière version, et c'est là qu'on les lit.</summary>
static class Linux
{
    public static readonly Distribution[] Toutes =
    [
        new("Ubuntu", "Le plus répandu : on trouve de l'aide partout.", Legere: true),
        new("Linux Mint", "Le plus proche de Windows : idéal pour débuter.", Legere: true),
        new("Debian", "Sobre et très stable.", Legere: true),
        new("Fedora", "Les nouveautés en premier.", Legere: true),
        new("Zorin OS", "Très soigné, pensé pour ceux qui quittent Windows.", Legere: true),
        // vu en machine virtuelle : la clé démarre Secure Boot activé ; son bureau « live » sert à installer (Bazzite le dit
        // lui-même : pas de pilotes, pas pour jouer), d'où Essai: false
        new("Bazzite", "Pour jouer : Steam est déjà installé. Clé d'installation seulement.", Essai: false, CarteGraphique: true),
        new("Kali", "Pour tester la sécurité d'un réseau. Clé d'installation seulement.", Essai: false, SecureBoot: false),
        new("Arch", "Pour connaisseurs : tout s'installe à la main, au clavier.", Essai: false, SecureBoot: false),
    ];

    /// <summary>Les variantes de Bazzite, une par famille de carte graphique (c'est le choix que demande son site).</summary>
    public static readonly string[] CartesGraphiques = ["", "nvidia-open", "nvidia"];

    /// <summary>Variante : vide, « legere » (bureau Xfce, ou Zorin OS Lite), ou pour Bazzite une des CartesGraphiques.</summary>
    public static async Task<ImageWindows> Chercher(string nom, string variante, CancellationToken ct)
    {
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(TimeSpan.FromSeconds(40));
        bool legere = variante == "legere";
        try
        {
            return nom switch
            {
                "Ubuntu" => await Ubuntu(legere, delai.Token),
                "Linux Mint" => await Dernier("https://mirrors.kernel.org/linuxmint/stable/", "sha256sum.txt",
                    $@"^linuxmint-[\d.]+-{(legere ? "xfce" : "cinnamon")}-64bit\.iso$", delai.Token),
                "Debian" => await Sommes("https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/", "SHA256SUMS",
                    $@"^debian-live-[\d.]+-amd64-{(legere ? "xfce" : "gnome")}\.iso$", delai.Token),
                "Fedora" => await Fedora(legere, delai.Token),
                "Zorin OS" => await Dernier("https://mirrors.kernel.org/zorinos-isos/", "SHA256SUMS.txt",
                    $@"^Zorin-OS-[\d.]+-{(legere ? "Lite" : "Core")}-64-bit(-r\d+)?\.iso$", delai.Token),
                // Kali ne distribue son image « à essayer » que par torrent : en téléchargement direct, c'est son installeur
                "Kali" => await Sommes("https://cdimage.kali.org/current/", "SHA256SUMS", @"^kali-linux-[\d.]+-installer-amd64\.iso$", delai.Token),
                "Arch" => await Sommes("https://geo.mirror.pkgbuild.com/iso/latest/", "sha256sums.txt", @"^archlinux-[\d.]+-x86_64\.iso$", delai.Token),
                // le fichier « live » (celui que propose bazzite.gg) ; « bazzite-stable-amd64.iso », sans « live », est l'ancien
                // installeur seul, figé depuis octobre 2025
                "Bazzite" when CartesGraphiques.Contains(variante) => await Sommes("https://download.bazzite.gg/",
                    $"bazzite{(variante == "" ? "" : "-" + variante)}-stable-live-amd64.iso-CHECKSUM", @"^bazzite(-nvidia(-open)?)?-stable-live-amd64\.iso$", delai.Token),
                _ => null,
            } ?? throw new FormatException();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Textes.T("Le site de {0} ne répond pas. Vérifie la connexion Internet.", nom));
        }
        // le site a changé de forme, ou ne publie plus ce fichier (404) : ce n'est pas une panne d'Internet
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or OverflowException
                                       or HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
        {
            throw new InvalidOperationException(Textes.T("{0} est introuvable sur son site.", nom));
        }
    }

    static async Task<ImageWindows> Ubuntu(bool legere, CancellationToken ct)
    {
        // la version « longue durée » en cours : la dernière ligne « Version: 26.04.1 LTS » de ce fichier d'Ubuntu
        var serie = Regex.Matches(await Catalogue.Http.GetStringAsync("https://changelogs.ubuntu.com/meta-release-lts", ct),
            @"^Version: (\d+\.\d+)", RegexOptions.Multiline).LastOrDefault()?.Groups[1].Value;
        if (serie == null) return null;
        return legere
            ? await Sommes($"https://cdimage.ubuntu.com/xubuntu/releases/{serie}/release/", "SHA256SUMS", @"^xubuntu-[\d.]+-desktop-amd64\.iso$", ct)
            : await Sommes($"https://releases.ubuntu.com/{serie}/", "SHA256SUMS", @"^ubuntu-[\d.]+-desktop-amd64\.iso$", ct);
    }

    /// <summary>Un miroir rangé par version (un dossier « 22.3/ », « 18/ »…) : le dossier le plus récent, puis son fichier d'empreintes.</summary>
    // shortcut: Linux Mint et Zorin OS n'ont pas de serveur de fichiers à eux, seulement des miroirs ; fichier et empreinte
    // viennent de celui de kernel.org, en HTTPS. À renforcer par la signature du fichier d'empreintes si ce miroir n'inspire plus confiance.
    static async Task<ImageWindows> Dernier(string miroir, string liste, string motif, CancellationToken ct)
    {
        // le dossier le plus récent peut ne porter encore qu'une version d'essai (« Beta ») : on prend alors le précédent
        foreach (var version in Regex.Matches(await Catalogue.Http.GetStringAsync(miroir, ct), @"href=""(\d+(\.\d+)?)/""")
                     .Select(m => m.Groups[1].Value).Distinct().OrderByDescending(Numero).Take(2))
            try { if (await Sommes($"{miroir}{version}/", liste, motif, ct) is { } image) return image; }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { } // dossier encore sans fichier d'empreintes
        return null;
    }

    static async Task<ImageWindows> Fedora(bool legere, CancellationToken ct)
    {
        // releases.json : toutes les versions publiées par Fedora, avec lien, taille et empreinte (les « 45 Beta » sont écartées)
        using var doc = JsonDocument.Parse(await Catalogue.Http.GetStringAsync("https://fedoraproject.org/releases.json", ct));
        static string Champ(JsonElement e, string nom) => e.TryGetProperty(nom, out var v) ? v.ToString() : "";
        var f = doc.RootElement.EnumerateArray()
            .Where(e => Champ(e, "arch") == "x86_64" && Champ(e, "subvariant") == (legere ? "Xfce" : "Workstation") && int.TryParse(Champ(e, "version"), out _)
                        && Regex.IsMatch(Champ(e, "link"), @"^https://download\.fedoraproject\.org/[\w./-]+\.iso$") && !Champ(e, "link").Contains("/test/")
                        && Regex.IsMatch(Champ(e, "sha256"), "^[0-9a-f]{64}$"))
            .OrderBy(e => int.Parse(Champ(e, "version"))).LastOrDefault();
        return f.ValueKind == JsonValueKind.Undefined ? null
            : new ImageWindows(Champ(f, "link"), long.Parse(Champ(f, "size")), Champ(f, "sha256"), Path.GetFileName(Champ(f, "link")));
    }

    /// <summary>Le numéro de version porté par un nom : « 26.04.1 » dans ubuntu-26.04.1-desktop-amd64.iso, « 23 » dans
    /// linuxmint-23-cinnamon-64bit.iso (une version sans point existe : Linux Mint 22). Un nom sans aucun nombre vaut 0.</summary>
    public static Version Numero(string nom)
    {
        var n = Regex.Match(nom, @"\d+(\.\d+)*").Value;
        return n == "" ? new Version(0, 0) : Version.Parse(n.Contains('.') ? n : n + ".0");
    }

    /// <summary>La révision d'un même numéro : 3 dans Zorin-OS-18-Core-64-bit-r3.iso, 0 sans « -rN ».</summary>
    static int Revision(string nom) => Regex.Match(nom, @"-r(\d+)\.iso$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    /// <summary>Lit un fichier d'empreintes (« empreinte  nom » par ligne) et rend la version la plus récente du fichier
    /// voulu, pris dans le même dossier. Le nom, qui vient du site, ne passe que s'il a exactement la forme attendue.</summary>
    static async Task<ImageWindows> Sommes(string dossier, string liste, string motif, CancellationToken ct)
    {
        var (empreinte, nom) = Regex.Matches(await Catalogue.Http.GetStringAsync(dossier + liste, ct), @"^([0-9a-f]{64}) [ *](\S+)\r?$", RegexOptions.Multiline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Where(l => Regex.IsMatch(l.Item2, motif))
            .OrderBy(l => Numero(l.Item2)).ThenBy(l => Revision(l.Item2)).LastOrDefault();
        if (nom == null) return null;
        // la taille n'est pas dans le fichier d'empreintes : on la demande au serveur, sans rien télécharger
        using var rep = await Catalogue.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, dossier + nom), ct);
        // un serveur qui répond mal un instant est une panne, pas « ce fichier n'existe pas » : sinon Dernier() prendrait
        // en silence la version précédente
        rep.EnsureSuccessStatusCode();
        return rep.Content.Headers.ContentLength is long taille ? new ImageWindows(dossier + nom, taille, empreinte, nom) : null;
    }
}
