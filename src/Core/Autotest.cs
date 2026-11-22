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

        foreach (var d in Linux.Toutes)
            foreach (var variante in d.CarteGraphique ? Linux.CartesGraphiques : d.Legere ? ["", "legere"] : [""])
                await Essai($"{d.Nom}{(variante == "" ? "" : $" ({variante})")}, sur son site", async () =>
                {
                    var img = await Linux.Chercher(d.Nom, variante, default);
                    // la plus petite, Arch, pèse 1,3 Go
                    Exiger(img.Taille > 1_000_000_000 && Regex.IsMatch(img.Empreinte, "^[0-9a-f]{64}$") && Regex.IsMatch(img.Nom, @"^[\w.-]+\.iso$"), "fiche inattendue : " + img.Nom);
                    // deux morceaux lus à distance : la reprise marche, c'est bien un disque optique (« CD001 ») et il porte
                    // aussi une table de partitions (55 AA), sans laquelle une copie exacte ne démarrerait pas sur une clé
                    var debut = await Task.Run(() => Portion(img.Url, 0, 512));
                    var iso = await Task.Run(() => Portion(img.Url, 32768, 6));
                    Exiger(debut[510] == 0x55 && debut[511] == 0xAA, "pas de table de partitions");
                    Exiger(Encoding.ASCII.GetString(iso, 1, 5) == "CD001", "ce n'est pas une image de disque");
                    return $"{img.Nom}, {img.Taille / 1e9:0.00} Go, {new Uri(img.Url).Host}";
                });

        foreach (bool bits64 in new[] { true, false })
            foreach (bool sansBureau in new[] { false, true })
                await Essai($"Raspberry Pi OS {(bits64 ? "64" : "32")} bits{(sansBureau ? ", sans bureau" : "")}, sur son site", async () =>
                {
                    var pi = await Raspberry.Chercher(bits64, sansBureau, default);
                    Exiger(pi.Fichier.Taille > 300_000_000 && pi.Taille > pi.Fichier.Taille && Regex.IsMatch(pi.Fichier.Nom, @"^[\w.-]+\.img\.xz$"), "fiche inattendue : " + pi.Fichier.Nom);
                    // le début du vrai fichier, déplié par l'appli : c'est bien l'image d'une carte (table de partitions, 55 AA)
                    var debut = new byte[512];
                    using (var xz = new Xz(new MemoryStream(await Task.Run(() => Portion(pi.Fichier.Url, 0, 1 << 20)))))
                        xz.ReadExactly(debut);
                    Exiger(debut[510] == 0x55 && debut[511] == 0xAA, "pas de table de partitions");
                    return $"{pi.Fichier.Nom}, {pi.Fichier.Taille / 1e9:0.00} Go, {pi.Taille / 1e9:0.00} Go une fois déplié";
                });

        await Essai("Lecture d'un fichier .xz", () => Task.Run(() =>
        {
            // un petit fichier fait par le compresseur de référence : ce qui en sort doit être le texte d'origine, à l'octet près
            var attendu = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"ligne {i} : Autoexec prepare la cle\n")));
            var fichier = Convert.FromBase64String("/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4DfRAV1dADYaSRpQbGUJcyzGgXc5QO6S7ByeMJ+sqbdHWx1G+RqnTcsK4/uNVW+ObHiSZHZycUCcZkga2sZLR+Xo6ovA9xaltRtppeyQ2bq3z1lQTkMq/Am/L5Mf7wVhhAs4y6RvM+fPMOIr3kMvldZySaZ3KaliQnvf4JpceOQ9KtRTasmrzlUL5AyTsIBuURWh8R+z2rv+Sk7aVDgZ/9ZFT/LlYi7oFga1Cy1zFqDye3NOxw5t9pTh65zeTY234VGSdVI/IjIKjUc/fdQpA8YnOrvVIFgsb+FSnWtduI+T6AVrErKjtuMdHwMtngNLmYb55EFKtOsir2xv2iDyD/gxlXOM8SSMurish+Whtvf7SenBKSi29dCWw1YKvXr/vnT37QqubcHIAUVtK5pyrwFdPKfFnfwurll9C2xPzXLwPN8XsPyXL8Oqw8CvIfa9mdyYoIxse68Tst/m47OgcSRsCUUAAAAA6yre4csl4YkAAfkC0m8AAGMy3kSxxGf7AgAAAAAEWVo=");
            var sorti = new MemoryStream();
            using (var xz = new Xz(new MemoryStream(fichier))) xz.CopyTo(sorti, 777);
            Exiger(sorti.ToArray().AsSpan().SequenceEqual(attendu), "le texte déplié diffère de l'original");
            // deux flux mis bout à bout, séparés par des zéros (le format le permet) : le texte doit sortir deux fois
            sorti.SetLength(0);
            using (var xz = new Xz(new MemoryStream([.. fichier, 0, 0, 0, 0, .. fichier]))) xz.CopyTo(sorti);
            Exiger(sorti.ToArray().AsSpan().SequenceEqual([.. attendu, .. attendu]), "deux flux à la suite mal lus");
            // un fichier abîmé ne doit jamais passer pour bon : ici, coupé au milieu
            bool refuse = false;
            try { using var xz = new Xz(new MemoryStream(fichier, 0, fichier.Length / 2)); xz.CopyTo(Stream.Null); } catch (InvalidDataException) { refuse = true; }
            Exiger(refuse, "un fichier coupé a été accepté");
            return $"{fichier.Length} octets dépliés en {attendu.Length}, identiques ; deux flux à la suite ; fichier coupé refusé";
        }));

        await Essai("Copie d'une image dépliée et relecture", () => Task.Run(() =>
        {
            // comme pour un Raspberry Pi : l'image arrive au fil de l'eau, la « carte » est un fichier
            string cle = Path.Combine(Path.GetTempPath(), "autoexec-carte.bin");
            var octets = new byte[(70 << 20) + 512];
            new Random(13).NextBytes(octets);
            var empreinte = Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();
            File.Delete(cle);
            try
            {
                using (var c = File.OpenHandle(cle, FileMode.Create, FileAccess.ReadWrite))
                    Preparation.CopierFlux(new MemoryStream(octets), c, octets.Length, empreinte);
                var ecrit = File.ReadAllBytes(cle);
                Exiger(ecrit.AsSpan(0, octets.Length).SequenceEqual(octets), "la copie diffère de l'image");
                // une image qui n'a pas l'empreinte publiée ne doit pas donner de carte : son premier bloc n'est jamais écrit
                File.Delete(cle);
                bool refuse = false;
                try
                {
                    using var c = File.OpenHandle(cle, FileMode.Create, FileAccess.ReadWrite);
                    Preparation.CopierFlux(new MemoryStream(octets), c, octets.Length, new string('0', 64));
                }
                catch (InvalidDataException) { refuse = true; }
                Exiger(refuse && File.ReadAllBytes(cle).AsSpan(0, 512).IndexOfAnyExcept((byte)0) < 0, "une image à la mauvaise empreinte a été écrite");
                return $"{octets.Length} octets, identiques ; mauvaise empreinte refusée, début de carte laissé vide";
            }
            finally { File.Delete(cle); }
        }));

        await Essai("Numéros de version Linux", () => Task.Run(() =>
        {
            Exiger(Linux.Numero("linuxmint-23-cinnamon-64bit.iso") == new Version(23, 0), "version sans point");
            Exiger(Linux.Numero("ubuntu-24.04.5.1-desktop-amd64.iso") > Linux.Numero("ubuntu-24.04.4-desktop-amd64.iso"), "version à quatre nombres");
            Exiger(Linux.Numero("debian-live-13.10.0-amd64-gnome.iso") > Linux.Numero("debian-live-13.8.0-amd64-gnome.iso"), "13.10 après 13.8");
            Exiger(Linux.Numero("Zorin-OS-18.1-Core-64-bit.iso") > Linux.Numero("Zorin-OS-18-Core-64-bit-r3.iso"), "18.1 après 18");
            Exiger(Linux.Toutes.Select(d => d.Nom).Distinct().Count() == Linux.Toutes.Length, "deux distributions du même nom");
            return "sans point, à quatre nombres, 13.10 après 13.8, 18.1 après 18";
        }));

        await Essai("Copie exacte et relecture", () => Task.Run(() =>
        {
            // une fausse image de deux blocs et demi, plus une fin qui ne tombe pas sur un secteur ; la « clé » est un fichier
            string image = Path.Combine(Path.GetTempPath(), "autoexec-image.bin"), cle = Path.Combine(Path.GetTempPath(), "autoexec-cle.bin");
            var octets = new byte[(80 << 20) + 1000];
            new Random(7).NextBytes(octets);
            File.WriteAllBytes(image, octets);
            File.Delete(cle);
            try
            {
                using (var i = File.OpenHandle(image))
                using (var c = File.OpenHandle(cle, FileMode.Create, FileAccess.ReadWrite))
                    Preparation.Copier(i, c, octets.Length);
                var ecrit = File.ReadAllBytes(cle);
                Exiger(ecrit.Length == (octets.Length + 4095) / 4096 * 4096, $"longueur {ecrit.Length}");
                Exiger(ecrit.AsSpan(0, octets.Length).SequenceEqual(octets) && ecrit.AsSpan(octets.Length).IndexOfAnyExcept((byte)0) < 0, "la copie diffère de l'image");
                // la relecture doit voir un seul bit de travers, dans le premier bloc (écrit à part) comme dans les suivants
                foreach (int position in new[] { 10, 50 << 20 })
                {
                    octets[position] ^= 1;
                    File.WriteAllBytes(image, octets);
                    octets[position] ^= 1;
                    bool refuse = false;
                    try
                    {
                        using var i = File.OpenHandle(image);
                        using var c = File.OpenHandle(cle);
                        Preparation.Copier(i, c, octets.Length, ecrire: false);
                    }
                    catch (InvalidOperationException) { refuse = true; }
                    Exiger(refuse, $"un bit de travers à l'octet {position} n'a pas été vu");
                }
                return $"{octets.Length} octets, identiques ; un bit de travers est vu";
            }
            finally { File.Delete(image); File.Delete(cle); }
        }));

        await Essai("Relecture d'un fichier copié", () => Task.Run(() =>
        {
            // la relecture d'une clé Windows : un fichier de deux lots et quelques, qui ne finit pas sur un secteur
            string source = Path.Combine(Path.GetTempPath(), "autoexec-source.bin"), copie = Path.Combine(Path.GetTempPath(), "autoexec-copie.bin");
            var octets = new byte[(9 << 20) + 777];
            new Random(11).NextBytes(octets);
            try
            {
                File.WriteAllBytes(source, octets);
                File.WriteAllBytes(copie, octets);
                long vus = 0;
                Exiger(Preparation.Identiques(source, copie, n => vus += n) && vus == octets.Length, "une copie fidèle a été dite différente");
                octets[^5] ^= 1;
                File.WriteAllBytes(copie, octets);
                Exiger(!Preparation.Identiques(source, copie, null), "un bit de travers en fin de fichier n'a pas été vu");
                File.WriteAllBytes(copie, octets.AsSpan(0, octets.Length - 1).ToArray());
                Exiger(!Preparation.Identiques(source, copie, null), "un fichier plus court n'a pas été vu");
                File.WriteAllBytes(source, []);
                File.WriteAllBytes(copie, []);
                Exiger(Preparation.Identiques(source, copie, null), "deux fichiers vides ont été dits différents");
                return $"{octets.Length} octets : fidèle, un bit de travers, plus court, vide";
            }
            finally { File.Delete(source); File.Delete(copie); }
        }));

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
            // clé remise à neuf : toute la clé, sans nom ni partition active
            Exiger(!Preparation.ScriptEffacer(2, 0).Contains("size=")
                && Preparation.ScriptFormater(2, 'G', "exFAT") == "select disk 2\r\nselect partition 1\r\nformat fs=exfat quick\r\nassign letter=G\r\nexit\r\n", "clé à neuf");
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
                Exiger(Regex.IsMatch(p.Nom, @"^[a-z0-9-]+\.cab$") && Regex.IsMatch(p.Empreinte, "^[0-9a-f]{40}$"), "fiche inattendue : " + p.Nom);
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
        if (img.Taille < 3_000_000_000 || !Regex.IsMatch(img.Empreinte, "^[0-9a-f]{40}$") || !img.Nom.EndsWith(".esd"))
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
