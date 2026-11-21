using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Autoexec.Core;

/// <summary>Autoexec.exe --autotest : vérifie tout ce qui se vérifie sans droits d'administrateur ni clé à effacer.
/// Rapport dans %TEMP%\autoexec-autotest.txt ; code de sortie = nombre d'échecs.</summary>
static class Autotest
{
    public static async Task<int> Lancer()
    {
        var rapport = new StringBuilder();
        int echecs = 0;
        async Task Essai(string nom, Func<Task<string>> essai)
        {
            try { rapport.AppendLine($"OK    {nom} : {await essai()}"); }
            catch (Exception ex) { echecs++; rapport.AppendLine($"ÉCHEC {nom} : {ex.GetType().Name} : {ex.Message}"); }
        }
        void Exiger(bool vrai, string quoi) { if (!vrai) throw new InvalidOperationException(quoi); }

        ImageWindows w11 = null, w10 = null;
        await Essai("Catalogue Windows 11", async () => { w11 = await Catalogue.Chercher(11, "fr-fr", default); return Decrire(w11); });
        await Essai("Catalogue Windows 10", async () => { w10 = await Catalogue.Chercher(10, "fr-fr", default); return Decrire(w10); });

        await Essai("Langues de Windows", async () =>
        {
            foreach (int windows in new[] { 11, 10 })
            {
                var doc = await Catalogue.Document(windows, default);
                foreach (var langue in Catalogue.Langues) Decrire(Catalogue.Choisir(doc, langue));
            }
            Exiger(Catalogue.Langues.Contains(Catalogue.LangueDuPc()), "langue du PC hors liste");
            return $"{Catalogue.Langues.Length} langues, pour Windows 11 et Windows 10 ; ce PC : {Catalogue.LangueDuPc()}";
        });

        await Essai("Traduction anglaise", () => Task.Run(() =>
        {
            // un texte traduit garde les mêmes emplacements {0}, {1}… que son texte français
            static string Trous(string s) => string.Concat(Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
            foreach (var (fr, en) in Textes.En) Exiger(en.Length > 0 && Trous(fr) == Trous(en), "traduction à revoir : " + fr);
            bool avant = Textes.Anglais;
            Textes.Anglais = true;
            var rendu = Textes.T("La clé est trop petite : il faut au moins {0} Go.", 8);
            Textes.Anglais = avant;
            Exiger(rendu == "The drive is too small: at least 8 GB is needed.", rendu);
            return $"{Textes.En.Count} textes";
        }));

        await Essai("Mise à jour par GitHub", async () =>
        {
            static MiseAJour.Publication Lue(string json) => MiseAJour.Lire(System.Text.Json.JsonDocument.Parse(json).RootElement);
            string Fiche(string nom, string url, string empreinte) =>
                $$"""{"tag_name":"v9.4.1","assets":[{"name":"{{nom}}","browser_download_url":"{{url}}","size":123,"digest":"{{empreinte}}"}]}""";
            string bonne = "sha256:" + new string('a', 64), lien = "https://github.com/a/b/releases/download/v9.4.1/Autoexec.exe";
            Exiger(Lue(Fiche("Autoexec.exe", lien, bonne)) is { Taille: 123 } v && v.Numero == new Version(9, 4, 1) && v.Sha256.Length == 64, "publication non lue");
            Exiger(Lue(Fiche("Autoexec.exe", "https://ailleurs.example/Autoexec.exe", bonne)) == null, "un exe servi ailleurs que chez GitHub a été accepté");
            Exiger(Lue(Fiche("Autoexec.exe", lien, "")) == null, "un exe sans empreinte a été accepté");
            Exiger(Lue(Fiche("Autre.exe", lien, bonne)) == null, "un autre fichier a été accepté");
            var publiee = await MiseAJour.Chercher(default);
            return $"version {MiseAJour.Actuelle} ; " + (publiee == null ? "rien de plus récent sur GitHub" : $"version {publiee.Numero} publiée");
        });

        foreach (var (nom, img) in new[] { ("Windows 11", w11), ("Windows 10", w10) })
            await Essai($"Images du fichier {nom}, lues à distance", () => Task.Run(() =>
            {
                Exiger(img != null, "catalogue absent");
                var images = Wim.Images((position, taille) => Portion(img.Url, position, taille));
                var (demarrage, editions) = Preparation.Trier(images);
                return $"{images.Count} images ; démarrage {string.Join(",", demarrage)} ; éditions "
                    + string.Join(" ", images.Where(i => editions.Contains(i.Index)).Select(i => i.Edition));
            }));

        await Essai("Téléchargement repris après coupure", async () =>
        {
            var url = "https://go.microsoft.com/fwlink/?LinkId=841361";
            var octets = await Catalogue.Http.GetByteArrayAsync(url);
            var dest = Path.Combine(Path.GetTempPath(), "autoexec-reprise.bin");
            File.Delete(dest);
            await File.WriteAllBytesAsync(dest + ".part", octets.AsSpan(0, octets.Length / 2).ToArray());
            var img = new ImageWindows(url, octets.Length, Convert.ToHexString(SHA1.HashData(octets)).ToLowerInvariant(), "reprise");
            bool repris = await Telechargement.Recuperer(img, dest, (_, _) => { }, null, default);
            Exiger(File.ReadAllBytes(dest).AsSpan().SequenceEqual(octets), "fichier différent de l'original");
            // un fichier abîmé doit être refusé
            await File.WriteAllBytesAsync(dest + ".part", new byte[octets.Length]);
            File.Delete(dest);
            bool refuse = false;
            try { await Telechargement.Recuperer(img, dest, (_, _) => { }, null, default); } catch (InvalidDataException) { refuse = true; }
            Exiger(refuse && !File.Exists(dest), "un fichier abîmé a été accepté");
            File.Delete(dest);
            // ce petit serveur n'est pas celui du fichier Windows : on dit ce qu'il a vraiment fait
            // (la reprise chez le serveur du fichier Windows est prouvée par la lecture à distance ci-dessus)
            return $"{octets.Length} octets, " + (repris ? "seconde moitié seule téléchargée" : "serveur sans reprise, fichier repris de zéro") + ", fichier abîmé refusé";
        });

        await Essai("Fichier de réglages", () => Task.Run(() =>
        {
            Exiger(Reglages.Fabriquer(false, false, false) == null, "rien demandé doit donner rien");
            var tout = Reglages.Fabriquer(true, true, true).ToString();
            foreach (var mot in new[] { "windowsPE", "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "specialize", "BypassNRO", "oobeSystem", "HideOnlineAccountScreens", "ProtectYourPC", "wcm:action=\"add\"" })
                Exiger(tout.Contains(mot), "il manque " + mot);
            var compte = Reglages.Fabriquer(true, false, false).ToString();
            Exiger(!compte.Contains("windowsPE") && !compte.Contains("ProtectYourPC") && compte.Contains("BypassNRO"), "compte local seul");
            var tpm = Reglages.Fabriquer(false, false, true).ToString();
            Exiger(tpm.Contains("windowsPE") && !tpm.Contains("BypassNRO") && !tpm.Contains("oobeSystem"), "TPM seul");
            return "3 combinaisons";
        }));

        await Essai("Script d'effacement de la clé", () => Task.Run(() =>
        {
            var petite = Preparation.ScriptEffacer(3, 16_000_000_000);
            var grosse = Preparation.ScriptEffacer(2, 123_000_000_000);
            var format = Preparation.ScriptFormater(3, 'F');
            Exiger(petite.StartsWith("select disk 3\r\nclean\r\n") && !petite.Contains("size="), "petite clé");
            Exiger(grosse.StartsWith("select disk 2\r\nclean\r\n") && grosse.Contains("create partition primary size=32000"), "grosse clé");
            Exiger(format.StartsWith("select disk 3\r\nselect partition 1\r\nformat fs=fat32 quick label=AUTOEXEC\r\n") && format.Contains("assign letter=F"), "format");
            Exiger(!format.Contains("clean") && Regex.Matches(grosse + format, "select disk").Count == 2, "un seul disque visé par script");
            Exiger(Preparation.Pourcentage("[===========       42.5%            ]") == 42.5 && Preparation.Pourcentage("Deployment Image Servicing") == null, "progression DISM");
            return "petite et grosse clé";
        }));

        await Essai("Pilotes réseau de ce PC", () => Task.Run(() =>
        {
            var pilotes = Pilotes.Reseau();
            Exiger(pilotes.All(p => Regex.IsMatch(p.Inf, @"^oem\d+\.inf$")), "nom de pilote inattendu");
            return pilotes.Count == 0 ? "aucun" : string.Join(" ; ", pilotes.Select(p => $"{p.Nom} ({p.Inf})"));
        }));

        await Essai("Pilotes des cartes courantes, chez Microsoft", async () =>
        {
            Exiger(Pilotes.Courants.Select(p => p.Nom).Distinct().Count() == Pilotes.Courants.Length, "deux paquets du même nom");
            foreach (var p in Pilotes.Courants)
            {
                Exiger(Regex.IsMatch(p.Nom, @"^[a-z0-9-]+\.cab$") && Regex.IsMatch(p.Sha1, "^[0-9a-f]{40}$"), "fiche inattendue : " + p.Nom);
                Exiger(new Uri(p.Url).Host.EndsWith(".windowsupdate.com"), p.Nom + " pointe ailleurs que chez Microsoft");
                // sans télécharger : le fichier existe toujours, à la taille notée
                using var rep = await Catalogue.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, p.Url));
                Exiger(rep.IsSuccessStatusCode && rep.Content.Headers.ContentLength == p.Taille, $"{p.Nom} : code {(int)rep.StatusCode}, {rep.Content.Headers.ContentLength} octets");
            }
            return $"{Pilotes.Courants.Length} paquets, {Pilotes.Courants.Sum(p => p.Taille) / 1e6:0} Mo";
        });

        await Essai("Clés USB branchées", () => Task.Run(() =>
        {
            var cles = Cles.Lister();
            return cles.Count == 0 ? "aucune" : string.Join(" ; ", cles.Select(c => $"disque {c.Numero} {c.Nom} {c.Go}"));
        }));

        rapport.AppendLine(echecs == 0 ? "TOUT EST BON" : $"{echecs} ÉCHEC(S)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "autoexec-autotest.txt"), rapport.ToString());
        return echecs;
    }

    static string Decrire(ImageWindows img)
    {
        if (img.Taille < 3_000_000_000 || !Regex.IsMatch(img.Sha1, "^[0-9a-f]{40}$") || !img.Nom.EndsWith(".esd"))
            throw new InvalidOperationException("fiche du catalogue inattendue");
        return $"{img.Nom}, {img.Taille / 1e9:0.00} Go, {new Uri(img.Url).Host}";
    }

    /// <summary>Lit un morceau du fichier chez Microsoft sans le télécharger en entier (prouve aussi que la reprise est possible).</summary>
    static byte[] Portion(string url, long position, int taille)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(position, position + taille - 1);
        using var rep = Catalogue.Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
        if (rep.StatusCode != HttpStatusCode.PartialContent) throw new InvalidOperationException($"le serveur ne sait pas reprendre ({(int)rep.StatusCode})");
        var octets = rep.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (octets.Length != taille) throw new InvalidOperationException("morceau incomplet");
        return octets;
    }
}
