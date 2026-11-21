using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Autoexec.Core;

/// <summary>Le travail lancé en administrateur (Autoexec.exe --preparer travail.json) : efface la clé puis y pose Windows.
/// Il raconte où il en est dans etat.txt, que la fenêtre relit ; un fichier « annuler » l'arrête.</summary>
static class Preparation
{
    /// <summary>Edition : celle que l'installation prendra sans demander (Core, Professional, Education), ou vide pour qu'elle demande.</summary>
    public record Travail(int Disque, string Chemin, long Taille, string Image, string Edition, string Reglages, bool ReglagesPE, string[] Pilotes, string[] Paquets,
        string Dossier, bool Anglais);

    class Annule : Exception { }

    public const string FichierEtat = "etat.txt", FichierAnnuler = "annuler";
    public const long MaxFat32 = 4L * 1024 * 1024 * 1024 - 1;

    static string _dossier, _derniere = "";

    public static int Executer(string fichierTravail)
    {
        try
        {
            var t = JsonSerializer.Deserialize<Travail>(File.ReadAllText(fichierTravail));
            _dossier = t.Dossier;
            Textes.Anglais = t.Anglais; // les messages d'erreur reviennent à la fenêtre : dans sa langue
            Journal.Dossier = Path.GetDirectoryName(t.Dossier.TrimEnd('\\'));
            Journal.Ecrire($"Préparation : disque {t.Disque}, {t.Taille} octets, {Path.GetFileName(t.Image)}");
            Faire(t);
            Journal.Ecrire("Préparation terminée");
            return 0;
        }
        catch (Annule) { Journal.Ecrire("Préparation annulée"); Dire("erreur", 0, "annule"); return 2; }
        catch (Exception ex)
        {
            Journal.Ecrire("Préparation échouée : " + ex);
            Dire("erreur", 0, ex is InvalidOperationException ? ex.Message : Textes.T("Une erreur est survenue pendant la préparation."));
            return 1;
        }
        finally { Nettoyer(); }
    }

    static void Faire(Travail t)
    {
        Dire("windows", 0);
        VerifierCle(t); // inutile de préparer Windows si la clé n'est plus là
        var images = Wim.Images(t.Image);
        var (demarrage, editions) = Trier(images);
        if (!string.IsNullOrEmpty(t.Edition) && !images.Any(i => i.Edition == t.Edition))
            throw new InvalidOperationException(Textes.T("Cette édition n'est pas dans le fichier Windows."));

        // 1. Windows est d'abord sorti du fichier .esd sur le disque du PC. Tant que cette étape, la plus longue,
        //    n'a pas réussi (échec, annulation, Windows trop gros), la clé n'est pas touchée et garde son contenu.
        string media = Path.Combine(t.Dossier, "media"), sources = Path.Combine(media, "sources");
        Supprimer(media);
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(Path.Combine(t.Dossier, "scratch"));
        Dism($"/Apply-Image /ImageFile:\"{t.Image}\" /Index:1 /ApplyDir:\"{media}\"", p => Dire("windows", p * 0.05));
        Directory.CreateDirectory(sources);
        for (int i = 0; i < demarrage.Count; i++)
        {
            int n = i;
            Dism($"/Export-Image /SourceImageFile:\"{t.Image}\" /SourceIndex:{demarrage[n]} /DestinationImageFile:\"{Path.Combine(sources, "boot.wim")}\" /Compress:max"
                + (n == demarrage.Count - 1 ? " /Bootable" : ""), p => Dire("windows", 5 + (n + p / 100) * 15 / demarrage.Count));
        }
        // Une clé FAT32 n'accepte pas de fichier de 4 Go ou plus, et les éditions sortent presque aussi grosses que le fichier
        // téléchargé (mesuré : 4,684 Go pour 4,688). S'il dépasse, Windows passe donc par un .wim découpé en install.swm,
        // install2.swm… que l'installation sait relire (c'est aussi ce que fait Rufus). C'est le cas de Windows 11.
        bool decoupe = new FileInfo(t.Image).Length > MaxFat32;
        var install = decoupe ? Path.Combine(t.Dossier, "install.wim") : Path.Combine(sources, "install.esd");
        File.Delete(install);
        for (int i = 0; i < editions.Count; i++)
        {
            // la première édition porte presque toutes les données ; les suivantes n'ajoutent que leurs différences
            double debut = i == 0 ? 20 : 65 + 27.0 * (i - 1) / Math.Max(1, editions.Count - 1), part = i == 0 ? 45 : 27.0 / Math.Max(1, editions.Count - 1);
            Dism($"/Export-Image /SourceImageFile:\"{t.Image}\" /SourceIndex:{editions[i]} /DestinationImageFile:\"{install}\" /Compress:{(decoupe ? "max" : "recovery")}",
                p => Dire("windows", debut + part * p / 100));
        }
        if (decoupe)
        {
            Dism($"/Split-Image /ImageFile:\"{install}\" /SWMFile:\"{Path.Combine(sources, "install.swm")}\" /FileSize:3800", p => Dire("windows", 92 + p * 0.08));
            File.Delete(install);
        }
        Dire("windows", 100);

        // 2. Réglages et pilotes rejoignent Windows dans « media » : tout part ensuite sur la clé en une seule copie, et la
        //    clé n'est pas effacée si un pilote manque ou si elle est trop petite.
        Dire("finitions", 0);
        // ei.cfg : l'installation prend cette édition sans demander, même sur un PC vendu avec une autre (dont la clé de
        // produit, gravée dans le PC, déciderait sinon à la place de l'utilisateur)
        if (!string.IsNullOrEmpty(t.Edition))
            File.WriteAllText(Path.Combine(sources, "ei.cfg"), $"[EditionID]\r\n{t.Edition}\r\n[Channel]\r\nRetail\r\n[VL]\r\n0\r\n");
        if (t.Reglages != null)
        {
            // $OEM$\$$\Panther est recopié dans C:\Windows\Panther pendant l'installation : c'est là que Windows relit ses réglages
            var panther = Path.Combine(sources, @"$OEM$\$$\Panther");
            Directory.CreateDirectory(panther);
            File.Copy(t.Reglages, Path.Combine(panther, "unattend.xml"), true);
            // la passe windowsPE (PC sans TPM) se lit, elle, à la racine de la clé
            if (t.ReglagesPE) File.Copy(t.Reglages, Path.Combine(media, "autounattend.xml"), true);
        }
        foreach (var inf in t.Pilotes ?? [])
        {
            if (!Regex.IsMatch(inf, @"^oem\d+\.inf$")) continue; // seul un nom de pilote installé passe en ligne de commande
            // $WinPEDriver$ : l'installation de Windows y prend les pilotes et les installe dans le Windows final
            var dossier = Path.Combine(media, "$WinPEDriver$", Path.GetFileNameWithoutExtension(inf));
            Directory.CreateDirectory(dossier);
            int c = Lancer("pnputil.exe", $"/export-driver {inf} \"{dossier}\"", out var s);
            if (c == 0) continue;
            Journal.Ecrire($"Pilote {inf} non copié (code {c}) : {s}");
            throw new InvalidOperationException(Textes.T("Les pilotes réseau de ce PC n'ont pas pu être copiés. Réessaie, ou décoche les pilotes."));
        }
        // même dossier pour les pilotes des cartes les plus répandues (Pilotes.Courants), dépliés de leur .cab par expand.exe
        var paquets = (t.Paquets ?? []).Where(c => !c.Contains('"')).ToList(); // le chemin part entre guillemets en ligne de commande
        for (int i = 0; i < paquets.Count; i++)
        {
            SiAnnule();
            var dossier = Path.Combine(media, "$WinPEDriver$", Path.GetFileNameWithoutExtension(paquets[i]));
            Directory.CreateDirectory(dossier);
            int c = Lancer("expand.exe", $"-F:* \"{paquets[i]}\" \"{dossier}\"", out var s);
            Dire("finitions", 100.0 * (i + 1) / (paquets.Count + 1));
            if (c == 0 && Directory.EnumerateFiles(dossier, "*.inf", SearchOption.AllDirectories).Any()) continue;
            Journal.Ecrire($"Pilote {Path.GetFileName(paquets[i])} non déplié (code {c}) : {s}");
            throw new InvalidOperationException(Textes.T("Les pilotes n'ont pas pu être préparés. Réessaie, ou décoche les pilotes."));
        }
        var fichiers = Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        long total = fichiers.Sum(f => f.Length), copie = 0;
        // place utile sur la clé : sa partition (32 Go au plus), moins de quoi loger les tables FAT32 et les fins de blocs
        const long Marge = 96L << 20;
        if (total > Math.Min(t.Taille, 32000L << 20) - Marge)
            throw new InvalidOperationException(Textes.T("La clé est trop petite : il faut au moins {0} Go.", Math.Ceiling((total + Marge) / 1e9)));
        Dire("finitions", 100);

        // 3. La clé n'est effacée qu'ici, juste après un dernier contrôle : même numéro, même matériel, même taille.
        Dire("cle", 0);
        SiAnnule();
        char lettre = LettreLibre();
        bool prete = false;
        for (int essai = 0; essai < 2 && !prete; essai++)
        {
            if (essai > 0) Thread.Sleep(3000); // clé tenue un instant par un autre programme : second essai complet
            VerifierCle(t);
            if (Diskpart(ScriptEffacer(t.Disque, t.Taille)) != 0) continue;
            // en deux temps : juste après la création de la partition, Windows n'a pas toujours rattaché son volume
            // (« aucun volume n'est sélectionné », vu sur une SanDisk 115 Go) ; on laisse quelques secondes, deux fois au besoin
            for (int format = 0; format < 2 && !prete; format++)
            {
                Thread.Sleep(3000);
                prete = Diskpart(ScriptFormater(t.Disque, lettre)) == 0;
            }
        }
        var volume = new DriveInfo($"{lettre}:");
        for (int i = 0; prete && i < 40 && !(volume.IsReady && volume.DriveFormat == "FAT32"); i++) Thread.Sleep(500);
        if (!prete || !volume.IsReady || volume.DriveFormat != "FAT32")
            throw new InvalidOperationException(Textes.T("La clé n'a pas pu être effacée. Débranche-la, rebranche-la et réessaie."));
        NoterPartition(t.Disque);
        Dire("cle", 100);

        // 4. Copie d'un bloc sur la clé. Une clé qui décroche un instant ne fait pas tout perdre : le fichier en cours est repris.
        var buf = new byte[4 << 20];
        foreach (var f in fichiers)
        {
            var dest = Path.Combine($@"{lettre}:\", Path.GetRelativePath(media, f.FullName));
            for (int essai = 0; ; essai++)
            {
                long avant = copie;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                    using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        copie += n;
                        Dire("copie", 100.0 * copie / total);
                        SiAnnule();
                    }
                    break;
                }
                catch (IOException ex) when (essai < 2)
                {
                    Journal.Ecrire($"Copie de {f.Name} interrompue ({ex.Message}), nouvel essai");
                    copie = avant;
                    Thread.Sleep(2000);
                }
                catch (IOException ex)
                {
                    Journal.Ecrire($"Copie de {f.Name} abandonnée : {ex.Message}");
                    throw new InvalidOperationException(Textes.T("La clé ne répond plus. Rebranche-la et recommence."));
                }
            }
            if (new FileInfo(dest).Length != f.Length)
                throw new InvalidOperationException(Textes.T("La copie sur la clé est incomplète. Recommence, ou essaie une autre clé."));
        }
        Vider(lettre);
        Dire("fin", 100, lettre.ToString());
    }

    /// <summary>La clé visée est-elle toujours la même ? (Cles.Lister écarte déjà tout disque du système et tout disque dur.)</summary>
    static void VerifierCle(Travail t)
    {
        var cle = Cles.Lister().FirstOrDefault(c => c.Numero == t.Disque);
        if (cle == null || cle.Chemin != t.Chemin || cle.Taille != t.Taille)
            throw new InvalidOperationException(Textes.T("La clé a été retirée ou remplacée."));
        if (cle.Protegee) throw new InvalidOperationException(Textes.T("La clé est protégée en écriture."));
    }

    /// <summary>Note dans le journal ce que diskpart a réellement laissé : type de partition (12 = FAT32) et drapeau « active »,
    /// dont dépend le démarrage sur les vieux PC.</summary>
    static void NoterPartition(int disque)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                $"SELECT PartitionNumber, MbrType, IsActive, Size FROM MSFT_Partition WHERE DiskNumber = {disque}");
            foreach (ManagementObject p in s.Get())
                using (p) Journal.Ecrire($"Partition {p["PartitionNumber"]} : type {p["MbrType"]}, active {p["IsActive"]}, {p["Size"]} octets");
        }
        catch (Exception ex) { Journal.Ecrire("Lecture de la partition : " + ex.Message); }
    }

    /// <summary>Forme attendue du fichier de Microsoft : 1 = arborescence de la clé, deux images de démarrage, puis les éditions.</summary>
    public static (List<int> demarrage, List<int> editions) Trier(List<Wim.Image> images)
    {
        var demarrage = images.Where(i => i.Edition == "WindowsPE").Select(i => i.Index).ToList();
        var editions = images.Where(i => i.Index > 1 && i.Edition != "WindowsPE" && i.Edition != "").Select(i => i.Index).ToList();
        if (images.Count < 4 || images[0].Index != 1 || images[0].Edition == "WindowsPE" || demarrage.Count != 2 || editions.Count == 0)
            throw new InvalidOperationException(Textes.T("Le fichier Windows n'a pas la forme attendue."));
        return (demarrage, editions);
    }

    public static string ScriptEffacer(int disque, long taille) => string.Join("\r\n",
        $"select disk {disque}", "clean", "convert mbr",
        // Windows refuse de formater en FAT32 au-delà de 32 Go : sur une grosse clé, la partition s'arrête là
        "create partition primary" + (taille > 33_500_000_000 ? " size=32000" : ""), "exit", "");

    public static string ScriptFormater(int disque, char lettre) => string.Join("\r\n",
        $"select disk {disque}", "select partition 1", "format fs=fat32 quick label=AUTOEXEC", "active", $"assign letter={lettre}", "exit", "");

    static int Diskpart(string script)
    {
        var fichier = Path.Combine(_dossier, "cle.txt");
        File.WriteAllText(fichier, script);
        int code = Lancer("diskpart.exe", $"/s \"{fichier}\"", out var sortie);
        Journal.Ecrire($"diskpart → {code}\n{script.Trim()}\n{sortie.Trim()}");
        return code;
    }

    static readonly Regex Pourcent = new(@"(\d{1,3}\.\d)%");
    public static double? Pourcentage(string ligne) =>
        Pourcent.Match(ligne) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    static void Dism(string args, Action<double> avance)
    {
        var commande = $"/English {args} /ScratchDir:\"{Path.Combine(_dossier, "scratch")}\"";
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "dism.exe"), commande)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        var sortie = new StringBuilder();
        var ligne = new StringBuilder();
        int c;
        while ((c = p.StandardOutput.Read()) >= 0)
        {
            if (c != '\r' && c != '\n') { ligne.Append((char)c); continue; }
            var texte = ligne.ToString();
            ligne.Clear();
            if (Pourcentage(texte) is double pc) avance(pc);
            else if (texte.Trim().Length > 0) sortie.AppendLine(texte);
            if (File.Exists(Path.Combine(_dossier, FichierAnnuler)))
            {
                // on attend sa fin réelle : tant qu'il tient ses fichiers, le nettoyage du disque échouerait
                try { p.Kill(true); p.WaitForExit(15000); } catch { }
                throw new Annule();
            }
        }
        p.WaitForExit();
        if (p.ExitCode == 0) return;
        Journal.Ecrire($"dism {args} → {p.ExitCode}\n{sortie}");
        throw new InvalidOperationException(Textes.T("Windows n'a pas pu être préparé pour la clé."));
    }

    static int Lancer(string exe, string args, out string sortie)
    {
        // diskpart et pnputil écrivent dans la page de codes de la console : sans elle, les accents du journal sont illisibles
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        });
        sortie = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    static char LettreLibre()
    {
        var prises = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'D'; c <= 'Z'; c++) if (!prises.Contains(c)) return c;
        throw new InvalidOperationException(Textes.T("Il n'y a plus de lettre de lecteur libre."));
    }

    static void Dire(string etape, double pourcent, string texte = "")
    {
        if (_dossier == null) return;
        var ligne = $"{etape}|{Math.Round(pourcent):0}|{texte}";
        if (ligne == _derniere) return;
        _derniere = ligne;
        for (int i = 0; i < 5; i++)
            try { File.AppendAllText(Path.Combine(_dossier, FichierEtat), ligne + "\n"); return; }
            catch (IOException) { Thread.Sleep(40); }
    }

    static void SiAnnule() { if (File.Exists(Path.Combine(_dossier, FichierAnnuler))) throw new Annule(); }

    /// <summary>Les fichiers sortis par DISM appartiennent à l'administrateur : c'est à lui de les retirer.</summary>
    static void Nettoyer()
    {
        if (_dossier == null) return;
        Supprimer(Path.Combine(_dossier, "media"));
        Supprimer(Path.Combine(_dossier, "scratch"));
        try { File.Delete(Path.Combine(_dossier, "install.wim")); } catch { }
    }

    static void Supprimer(string dossier)
    {
        try
        {
            if (!Directory.Exists(dossier)) return;
            foreach (var f in Directory.EnumerateFiles(dossier, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dossier, true);
        }
        catch (Exception ex) { Journal.Ecrire($"Nettoyage de {dossier} : {ex.Message}"); }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string nom, uint acces, uint partage, IntPtr securite, uint creation, uint attributs, IntPtr modele);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool FlushFileBuffers(SafeFileHandle h);

    /// <summary>Force l'écriture de tout ce qui attend encore en mémoire : la clé peut être retirée juste après.</summary>
    static void Vider(char lettre)
    {
        using var h = CreateFile($@"\\.\{lettre}:", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!h.IsInvalid && FlushFileBuffers(h)) return;
        // on ne dit pas « terminé » sans cette confirmation : mieux vaut un échec clair qu'une clé peut-être incomplète
        Journal.Ecrire($"Vidage de {lettre}: non confirmé (erreur Windows {Marshal.GetLastWin32Error()})");
        throw new InvalidOperationException(Textes.T("La fin de l'écriture sur la clé n'a pas pu être confirmée. Recommence."));
    }
}
