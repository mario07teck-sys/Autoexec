using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autoexec.Core;

/// <summary>Les distributions Linux proposées. Rien n'est figé dans l'appli : chaque éditeur publie le nom, la taille et
/// l'empreinte SHA-256 de sa dernière version, et c'est là qu'on les lit. « Légère » = le même système avec le bureau Xfce,
/// qui demande moins de mémoire (pour un PC ancien).</summary>
static class Linux
{
    public static readonly string[] Noms = ["Ubuntu", "Linux Mint", "Debian", "Fedora"];

    public static async Task<ImageWindows> Chercher(string nom, bool legere, CancellationToken ct)
    {
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            return nom switch
            {
                "Ubuntu" => await Ubuntu(legere, delai.Token),
                "Linux Mint" => await Mint(legere, delai.Token),
                "Debian" => await Sommes("https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/", "SHA256SUMS",
                    $@"^debian-live-[\d.]+-amd64-{(legere ? "xfce" : "gnome")}\.iso$", delai.Token),
                "Fedora" => await Fedora(legere, delai.Token),
                _ => null,
            } ?? throw new FormatException();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Textes.T("Le site de {0} ne répond pas. Vérifie la connexion Internet.", nom));
        }
        // le site a changé de forme, ou ne publie plus ce fichier : ce n'est pas une panne d'Internet
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or OverflowException)
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

    static async Task<ImageWindows> Mint(bool legere, CancellationToken ct)
    {
        // shortcut: Linux Mint n'a pas de serveur central, seulement des miroirs ; fichier et empreinte viennent de celui de
        // kernel.org, en HTTPS. À renforcer par la signature de sha256sum.txt.gpg si ce miroir n'inspire plus confiance.
        const string miroir = "https://mirrors.kernel.org/linuxmint/stable/";
        var version = Regex.Matches(await Catalogue.Http.GetStringAsync(miroir, ct), @"href=""(\d+(\.\d+)?)/""")
            .Select(m => m.Groups[1].Value).OrderBy(Numero).LastOrDefault();
        return version == null ? null
            : await Sommes($"{miroir}{version}/", "sha256sum.txt", $@"^linuxmint-[\d.]+-{(legere ? "xfce" : "cinnamon")}-64bit\.iso$", ct);
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
    /// linuxmint-23-cinnamon-64bit.iso (une version sans point existe : Linux Mint 22).</summary>
    public static Version Numero(string nom)
    {
        var n = Regex.Match(nom, @"\d+(\.\d+)*").Value;
        return Version.Parse(n.Contains('.') ? n : n + ".0");
    }

    /// <summary>Lit le fichier d'empreintes d'un dossier (« empreinte  nom » par ligne) et rend la version la plus récente du
    /// fichier voulu. Le nom, qui vient du site, ne passe que s'il a exactement la forme attendue.</summary>
    static async Task<ImageWindows> Sommes(string dossier, string liste, string motif, CancellationToken ct)
    {
        var (empreinte, nom) = Regex.Matches(await Catalogue.Http.GetStringAsync(dossier + liste, ct), @"^([0-9a-f]{64}) [ *](\S+)\r?$", RegexOptions.Multiline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Where(l => Regex.IsMatch(l.Item2, motif))
            .OrderBy(l => Numero(l.Item2)).LastOrDefault();
        if (nom == null) return null;
        // la taille n'est pas dans le fichier d'empreintes : on la demande au serveur, sans rien télécharger
        using var rep = await Catalogue.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, dossier + nom), ct);
        return rep.IsSuccessStatusCode && rep.Content.Headers.ContentLength is long taille ? new ImageWindows(dossier + nom, taille, empreinte, nom) : null;
    }
}
