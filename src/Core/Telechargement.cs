using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Autoexec.Core;

static class Telechargement
{
    /// <summary>Télécharge vers dest en reprenant dest.part après une coupure, puis vérifie la taille et l'empreinte.</summary>
    /// <returns>vrai si le serveur a bien repris un fichier commencé au lieu de tout renvoyer.</returns>
    public static async Task<bool> Recuperer(ImageWindows img, string dest, Action<long, long> avance, Action<long, long> verifie, CancellationToken ct)
    {
        // déjà là (gardé d'une fois précédente) et intact : rien à télécharger
        if (File.Exists(dest) && new FileInfo(dest).Length == img.Taille && await Empreinte(dest, img.Empreinte.Length, verifie, ct) == img.Empreinte) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        var part = dest + ".part";
        bool repris = false;
        int sansProgres = 0;
        long tailleAuDernierEchec = -1;
        while (true)
        {
            try
            {
                long deja = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (deja > img.Taille) { File.Delete(part); deja = 0; }
                if (deja < img.Taille)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, img.Url);
                    if (deja > 0) req.Headers.Range = new RangeHeaderValue(deja, null);
                    using var rep = await Garde(t => Catalogue.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, t), ct);
                    if (rep.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        File.Delete(part); // le serveur ne veut pas de cette reprise : on repartira de zéro
                        throw new IOException("reprise refusée par le serveur");
                    }
                    rep.EnsureSuccessStatusCode();
                    if (rep.StatusCode == HttpStatusCode.PartialContent) repris |= deja > 0;
                    else deja = 0; // le serveur renvoie tout : on repart de zéro
                    await using var src = await rep.Content.ReadAsStreamAsync(ct);
                    await using var f = new FileStream(part, deja > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                    var buf = new byte[1 << 20];
                    while (deja < img.Taille)
                    {
                        int n = await Garde(t => src.ReadAsync(buf, t).AsTask(), ct);
                        if (n == 0) throw new IOException("le serveur a coupé avant la fin");
                        await f.WriteAsync(buf.AsMemory(0, n), ct);
                        deja += n;
                        avance(deja, img.Taille);
                    }
                }
                break;
            }
            // un fichier que le serveur dit absent (404, 410) ne reviendra pas en insistant : cette erreur-là remonte tout de suite
            catch (Exception ex) when ((ex is IOException || ex is HttpRequestException { StatusCode: not (HttpStatusCode.NotFound or HttpStatusCode.Gone) })
                                       && !ct.IsCancellationRequested)
            {
                // on n'abandonne qu'après huit coupures de suite sans un octet de plus ; dès que ça avance, le compte repart
                long taillePart = File.Exists(part) ? new FileInfo(part).Length : 0;
                sansProgres = taillePart > tailleAuDernierEchec ? 1 : sansProgres + 1;
                tailleAuDernierEchec = taillePart;
                if (sansProgres > 8) throw;
                Journal.Ecrire($"Téléchargement interrompu ({ex.Message}), reprise à {taillePart} octets");
                await Task.Delay(3000, ct);
            }
        }
        if (new FileInfo(part).Length != img.Taille || await Empreinte(part, img.Empreinte.Length, verifie, ct) != img.Empreinte)
        {
            File.Delete(part);
            throw new InvalidDataException(Textes.T("Le fichier téléchargé est abîmé. Réessaie."));
        }
        File.Move(part, dest, true);
        return repris;
    }

    /// <summary>Une attente réseau figée depuis 60 s devient une coupure (donc une reprise), sans se confondre avec une annulation voulue.</summary>
    static async Task<T> Garde<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var garde = CancellationTokenSource.CreateLinkedTokenSource(ct);
        garde.CancelAfter(TimeSpan.FromSeconds(60));
        try { return await action(garde.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("connexion figée"); }
    }

    /// <summary>SHA-256 quand l'empreinte attendue fait 64 caractères, SHA-1 sinon.</summary>
    public static async Task<string> Empreinte(string fichier, int longueur, Action<long, long> avance, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(longueur == 64 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        await using var f = new FileStream(fichier, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var buf = new byte[1 << 20];
        long lu = 0;
        int n;
        while ((n = await f.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            lu += n;
            avance?.Invoke(lu, f.Length);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
