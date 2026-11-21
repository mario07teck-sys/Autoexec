using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autoexec.Core;

/// <summary>Mise à jour de l'exe unique depuis les « releases » GitHub du dépôt public : au lancement, si une version plus
/// récente existe, elle est téléchargée, vérifiée, mise à la place de l'exe en cours, puis relancée.</summary>
static class MiseAJour
{
    public const string Depot = "mario07teck-sys/Autoexec", Fichier = "Autoexec.exe";

    public record Publication(Version Numero, string Url, long Taille, string Sha256);

    public static Version Actuelle { get; } = Norme(typeof(MiseAJour).Assembly.GetName().Version);
    static Version Norme(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>La version publiée si elle est plus récente que celle-ci, sinon null (à jour, pas de réseau, pas de publication).</summary>
    public static async Task<Publication> Chercher(CancellationToken ct)
    {
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Depot}/releases/latest");
        req.Headers.UserAgent.ParseAdd("Autoexec/" + Actuelle); // GitHub refuse une demande sans nom de client
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var rep = await Catalogue.Http.SendAsync(req, delai.Token);
        if (!rep.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await rep.Content.ReadAsStringAsync(delai.Token));
        return Lire(doc.RootElement) is { } v && v.Numero > Actuelle ? v : null;
    }

    /// <summary>N'accepte qu'un Autoexec.exe servi par GitHub lui-même, avec l'empreinte SHA-256 que GitHub publie pour lui.</summary>
    public static Publication Lire(JsonElement publication)
    {
        var nom = publication.TryGetProperty("tag_name", out var t) ? t.GetString() : "";
        if (!Version.TryParse(Regex.Match(nom ?? "", @"\d+\.\d+(\.\d+)?").Value, out var numero)) return null;
        if (!publication.TryGetProperty("assets", out var fichiers)) return null;
        foreach (var f in fichiers.EnumerateArray())
        {
            var url = f.GetProperty("browser_download_url").GetString() ?? "";
            var empreinte = f.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : "";
            if (f.GetProperty("name").GetString() == Fichier && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "github.com"
                && Regex.IsMatch(empreinte, "^sha256:[0-9a-f]{64}$"))
                return new Publication(Norme(numero), url, f.GetProperty("size").GetInt64(), empreinte[7..]);
        }
        return null;
    }

    /// <summary>Télécharge la nouvelle version à côté de l'exe, la vérifie, l'échange avec l'exe en cours et la lance.
    /// Windows laisse renommer un exe qui tourne, pas l'écraser : l'ancien devient « .ancien », retiré au lancement suivant.</summary>
    public static async Task Installer(Publication v, Action<double> avance, CancellationToken annulation)
    {
        string exe = Environment.ProcessPath, nouveau = exe + ".nouveau", ancien = exe + ".ancien";
        // une connexion qui traîne ne doit pas retenir l'appli : passé ce délai, la mise à jour attendra le prochain lancement
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(annulation);
        delai.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = delai.Token;
        using (var rep = await Catalogue.Http.GetAsync(v.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            rep.EnsureSuccessStatusCode();
            await using var src = await rep.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(nouveau, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buf = new byte[1 << 20];
            long fait = 0;
            for (int n; (n = await src.ReadAsync(buf, ct)) > 0;)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                avance(100.0 * (fait += n) / v.Taille);
            }
        }
        await using (var f = File.OpenRead(nouveau))
            if (f.Length != v.Taille || Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant() != v.Sha256)
                throw new InvalidDataException("mise à jour téléchargée différente de celle publiée");
        File.Move(exe, ancien, true);
        try { File.Move(nouveau, exe); }
        catch { File.Move(ancien, exe); throw; } // on ne reste jamais sans exe
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>Retire les restes d'une mise à jour précédente (ou interrompue).</summary>
    public static void Nettoyer()
    {
        foreach (var reste in new[] { ".ancien", ".nouveau" })
            try { File.Delete(Environment.ProcessPath + reste); } catch { }
    }
}
