using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autoexec.Core;

/// <summary>Raspberry Pi OS. La dernière version est lue dans la liste officielle que Raspberry publie pour son propre outil
/// (Raspberry Pi Imager) : rien n'est figé ici.</summary>
static class Raspberry
{
    /// <summary>Fichier : le .img.xz à télécharger, avec son empreinte. Taille et Empreinte : celles de l'image une fois
    /// dépliée, c'est-à-dire de ce qui doit se retrouver sur la carte.</summary>
    public record Image(ImageWindows Fichier, long Taille, string Empreinte);

    const string Liste = "https://downloads.raspberrypi.com/os_list_imagingutility_v4.json";

    public static async Task<Image> Chercher(bool bits64, bool sansBureau, CancellationToken ct)
    {
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            // « …-raspios-trixie-arm64.img.xz » : ni la version « full » ni les anciennes (« Legacy »), que la liste porte aussi
            var motif = new Regex(@"^https://downloads\.raspberrypi\.com/[\w./-]+/\d{4}-\d{2}-\d{2}-raspios-[a-z]+-"
                + (bits64 ? "arm64" : "armhf") + (sansBureau ? "-lite" : "") + @"\.img\.xz$");
            string url, taille, deplie, empreinte;
            using (var doc = JsonDocument.Parse(await Catalogue.Http.GetStringAsync(Liste, delai.Token)))
            {
                static string Champ(JsonElement e, string nom) => e.TryGetProperty(nom, out var v) ? v.ToString() : "";
                var f = Fiches(doc.RootElement.GetProperty("os_list"))
                    .Where(e => motif.IsMatch(Champ(e, "url")) && !Champ(e, "name").Contains("Legacy"))
                    .OrderBy(e => Champ(e, "release_date"), StringComparer.Ordinal).LastOrDefault();
                if (f.ValueKind == JsonValueKind.Undefined) throw new FormatException();
                (url, taille, deplie, empreinte) = (Champ(f, "url"), Champ(f, "image_download_size"), Champ(f, "extract_size"), Champ(f, "extract_sha256"));
            }
            // l'empreinte du fichier compressé est publiée à côté de lui
            var somme = Regex.Match(await Catalogue.Http.GetStringAsync(url + ".sha256", delai.Token), "^[0-9a-f]{64}").Value;
            if (somme.Length != 64 || !Regex.IsMatch(empreinte, "^[0-9a-f]{64}$")) throw new FormatException();
            // des tailles absurdes ne doivent pas mener jusqu'à l'effacement de la carte
            long compresse = long.Parse(taille), image = long.Parse(deplie);
            if (compresse < 100_000_000 || image <= compresse || image > 100_000_000_000) throw new FormatException();
            return new Image(new ImageWindows(url, compresse, somme, Path.GetFileName(url)), image, empreinte);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Textes.T("Le site de {0} ne répond pas. Vérifie la connexion Internet.", "Raspberry Pi"));
        }
        // la liste a changé de forme, ou un fichier n'est plus publié (404) : ce n'est pas une panne d'Internet
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or OverflowException or KeyNotFoundException or InvalidOperationException
                                       or System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
        {
            throw new InvalidOperationException(Textes.T("{0} est introuvable sur son site.", "Raspberry Pi OS"));
        }
    }

    /// <summary>La liste range ses systèmes dans des sous-listes : on les parcourt toutes.</summary>
    static IEnumerable<JsonElement> Fiches(JsonElement liste)
    {
        foreach (var e in liste.EnumerateArray())
            if (e.TryGetProperty("subitems", out var sous)) foreach (var x in Fiches(sous)) yield return x;
            else yield return e;
    }
}
