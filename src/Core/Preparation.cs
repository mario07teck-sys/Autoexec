using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Autoexec.Core;

/// <summary>Le travail lancé en administrateur (Autoexec.exe --preparer travail.json) : efface la clé puis y pose Windows
/// ou Linux, ou la remet à neuf. Il raconte où il en est dans etat.txt, que la fenêtre relit ; un fichier « annuler » l'arrête.</summary>
static class Preparation
{
    /// <summary>Edition : celle que l'installation prendra sans demander (Core, Professional, Education), ou vide pour qu'elle demande.
    /// Genre : « windows », « linux » (Image est un .iso, copié tel quel), « raspberry » (Image est un .img.xz, déplié sur la
    /// carte ; TailleImage et EmpreinteImage sont celles de l'image dépliée) ou « neuf » (clé vidée, sans Image).</summary>
    public record Travail(int Disque, string Chemin, long Taille, string Image, string Edition, string Reglages, bool ReglagesPE, string[] Pilotes, string[] Paquets,
        string Dossier, bool Anglais, string Genre = "windows", long TailleImage = 0, string EmpreinteImage = null);

    class Annule : Exception { }

    public const string FichierEtat = "etat.txt", FichierAnnuler = "annuler";
    public const long MaxFat32 = 4L * 1024 * 1024 * 1024 - 1;

    static string _dossier, _derniere = "";
    static bool _cleTouchee; // vrai dès que l'effacement de la clé a commencé

    public static int Executer(string fichierTravail)
    {
        FileStream verrou = null;
        try
        {
            var t = JsonSerializer.Deserialize<Travail>(File.ReadAllText(fichierTravail));
            _dossier = t.Dossier;
            Textes.Anglais = t.Anglais; // les messages d'erreur reviennent à la fenêtre : dans sa langue
            Journal.Dossier = Path.GetDirectoryName(t.Dossier.TrimEnd('\\'));
            // Un seul travail à la fois sur ce PC : un travail resté seul (sa fenêtre tuée) continue, et un second, lancé
            // par une nouvelle fenêtre, écrirait sur la même clé et dans le même dossier.
            try { verrou = new FileStream(Path.Combine(t.Dossier, "verrou"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                Journal.Ecrire("Préparation refusée : une autre est déjà en cours");
                Dire("erreur", 0, Textes.T("Une autre préparation est déjà en cours sur ce PC. Attends qu'elle soit finie."));
                _dossier = null; // rien à nettoyer : ce dossier sert à l'autre
                return 1;
            }
            Journal.Ecrire($"Préparation : disque {t.Disque}, {t.Taille} octets, {Path.GetFileName(t.Image) ?? "clé remise à neuf"}");
            Faire(t);
            Journal.Ecrire("Préparation terminée");
            return 0;
        }
        catch (Annule)
        {
            // la fenêtre doit savoir si la clé était déjà entamée : elle n'est alors plus utilisable telle quelle
            Journal.Ecrire(_cleTouchee ? "Préparation annulée, clé déjà effacée" : "Préparation annulée, clé intacte");
            Dire("erreur", 0, _cleTouchee ? "annule-efface" : "annule");
            return 2;
        }
        catch (Exception ex)
        {
            Journal.Ecrire("Préparation échouée : " + ex);
            Dire("erreur", 0, ex is InvalidOperationException ? ex.Message : Textes.T("Une erreur est survenue pendant la préparation."));
            return 1;
        }
        finally { Nettoyer(); verrou?.Dispose(); }
    }

    static void Faire(Travail t)
    {
        if (t.Genre is "linux" or "raspberry") { CopieExacte(t); return; }
        if (t.Genre == "neuf")
        {
            Dire("cle", 0);
            Vider(Effacer(t, "neuf"));
            Dire("fin", 100);
            return;
        }
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
        // Le .wim à découper est compressé vite sur une clé qui a la place (16 Go et plus), au maximum sur une clé de 8 Go.
        // Mesuré sur Windows 11, mêmes conditions : 4 min 51 s et 6,5 Go en rapide, contre 8 min 58 s et 6,0 Go au maximum.
        bool rapide = decoupe && t.Taille > 15_000_000_000;
        var install = decoupe ? Path.Combine(t.Dossier, "install.wim") : Path.Combine(sources, "install.esd");
        File.Delete(install);
        // part de la jauge : la première édition porte presque toutes les données, les suivantes n'ajoutent que leurs différences
        double finPremiere = rapide ? 74 : 65, finEditions = rapide ? 84 : 92;
        for (int i = 0; i < editions.Count; i++)
        {
            double suivantes = (finEditions - finPremiere) / Math.Max(1, editions.Count - 1);
            double debut = i == 0 ? 20 : finPremiere + suivantes * (i - 1), part = i == 0 ? finPremiere - 20 : suivantes;
            Dism($"/Export-Image /SourceImageFile:\"{t.Image}\" /SourceIndex:{editions[i]} /DestinationImageFile:\"{install}\" /Compress:{(rapide ? "fast" : decoupe ? "max" : "recovery")}",
                p => Dire("windows", debut + part * p / 100));
        }
        if (decoupe)
        {
            Dism($"/Split-Image /ImageFile:\"{install}\" /SWMFile:\"{Path.Combine(sources, "install.swm")}\" /FileSize:3800",
                p => Dire("windows", finEditions + p * (100 - finEditions) / 100));
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
            throw new InvalidOperationException(Textes.T("La clé est trop petite : il faut au moins {0} Go.", Math.Ceiling((total + Marge) / 1073741824.0)));
        Dire("finitions", 100);

        // 3. La clé n'est effacée qu'ici, juste après un dernier contrôle : même numéro, même matériel, même taille.
        Dire("cle", 0);
        char lettre = Effacer(t, "windows");
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

        // 5. Relecture de toute la clé, comparée fichier par fichier à ce qui vient d'y être copié : une clé qui rend autre
        //    chose que ce qu'on y écrit (clé usée, ou de fausse capacité) n'est pas dite « prête ».
        Dire("verif", 0);
        long relu = 0;
        try
        {
            foreach (var f in fichiers)
            {
                bool pareil = Identiques(f.FullName, Path.Combine($@"{lettre}:\", Path.GetRelativePath(media, f.FullName)),
                    n => { relu += n; Dire("verif", 100.0 * relu / Math.Max(1, total)); SiAnnule(); });
                if (pareil) continue;
                Journal.Ecrire($"Vérification : {Path.GetRelativePath(media, f.FullName)} diffère sur la clé");
                throw new InvalidOperationException(Textes.T("La clé ne garde pas ce qu'on y écrit. Essaie une autre clé."));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Journal.Ecrire("Relecture de la clé interrompue : " + ex.Message);
            throw new InvalidOperationException(Textes.T("La clé ne répond plus. Rebranche-la et recommence."));
        }
        Dire("fin", 100, lettre.ToString());
    }

    /// <summary>Un fichier et sa copie sur la clé ont-ils le même contenu ? La copie est lue sans passer par la mémoire de
    /// Windows, qui rendrait sinon ce qu'il vient d'écrire et non ce que la clé a gardé.</summary>
    public static bool Identiques(string source, string copie, Action<int> avance)
    {
        const int Lot = 4 << 20, Secteur = 4096;
        using var a = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        using var b = File.OpenHandle(copie, FileMode.Open, FileAccess.Read, FileShare.Read, (FileOptions)0x20000000); // FILE_FLAG_NO_BUFFERING
        long longueur = RandomAccess.GetLength(a);
        if (RandomAccess.GetLength(b) != longueur) return false;
        // une lecture sans tampon exige une mémoire qui commence sur une frontière de secteur : on la prend dans un tableau
        // que le ramasse-miettes ne déplace pas, à partir du premier octet bien placé
        byte[] x = new byte[Lot], brut = GC.AllocateUninitializedArray<byte>(Lot + Secteur, pinned: true);
        int debut = (int)((Secteur - Marshal.UnsafeAddrOfPinnedArrayElement(brut, 0).ToInt64() % Secteur) % Secteur);
        for (long position = 0; position < longueur;)
        {
            int n = 0, lu;
            while (n < Lot && (lu = RandomAccess.Read(a, x.AsSpan(n), position + n)) > 0) n += lu;
            // toujours un lot entier demandé (multiple du secteur) : la clé rend ce qui reste du fichier
            if (n == 0 || RandomAccess.Read(b, brut.AsSpan(debut, Lot), position) < n || !x.AsSpan(0, n).SequenceEqual(brut.AsSpan(debut, n))) return false;
            position += n;
            avance?.Invoke(n);
        }
        return true;
    }

    /// <summary>Efface la clé. « windows » : clé d'installation, FAT32 « AUTOEXEC » de 32 Go au plus, partition active.
    /// « neuf » : toute la clé, sans nom, en exFAT au-delà de 32 Go (Windows refuse d'y mettre du FAT32).
    /// « linux » : clé seulement vidée, sans partition ni lettre (rend alors '\0').</summary>
    static char Effacer(Travail t, string genre)
    {
        SiAnnule();
        bool neuf = genre == "neuf";
        string format = genre == "linux" ? null : neuf && t.Taille > 33_500_000_000 ? "exFAT" : "FAT32";
        char lettre = format == null ? '\0' : LettreLibre(); // s'il n'en reste aucune, autant le savoir avant d'effacer
        VerifierCle(t);
        _cleTouchee = true;
        bool prete = EffacerVite(t, format, neuf, ref lettre);
        // diskpart, plus lent, reste le filet de sécurité : c'est lui qui a fait toutes les clés jusqu'à la version 1.0
        for (int essai = 0; essai < 2 && !prete; essai++)
        {
            if (essai > 0) Thread.Sleep(3000); // clé tenue un instant par un autre programme : second essai complet
            VerifierCle(t);
            if (Diskpart(ScriptEffacer(t.Disque, neuf ? 0 : t.Taille, format != null)) != 0) continue;
            prete = format == null;
            // en deux temps : juste après la création de la partition, Windows n'a pas toujours rattaché son volume
            // (« aucun volume n'est sélectionné », vu sur une SanDisk 115 Go) ; on laisse quelques secondes, deux fois au besoin
            for (int passe = 0; passe < 2 && !prete; passe++)
            {
                Thread.Sleep(3000);
                lettre = LettreLibre(); // choisie au dernier moment : une lettre libre il y a une minute peut ne plus l'être
                prete = Diskpart(ScriptFormater(t.Disque, lettre, neuf ? format : null)) == 0;
            }
        }
        if (format != null)
        {
            var volume = new DriveInfo($"{lettre}:");
            for (int i = 0; prete && i < 40 && !(volume.IsReady && volume.DriveFormat == format); i++) Thread.Sleep(500);
            prete &= volume.IsReady && volume.DriveFormat == format;
        }
        if (!prete) throw new InvalidOperationException(Textes.T("La clé n'a pas pu être effacée. Débranche-la, rebranche-la et réessaie."));
        NoterPartition(t.Disque);
        return lettre;
    }

    /// <summary>Le même effacement par le service de stockage de Windows : une dizaine de secondes, contre une minute par
    /// diskpart, qui en met déjà vingt à démarrer (mesuré le 22/11/2026). Rend faux si Windows refuse : diskpart prend le relais.</summary>
    static bool EffacerVite(Travail t, string format, bool neuf, ref char lettre)
    {
        const string stockage = @"root\Microsoft\Windows\Storage";
        static ManagementObject Un(string requete)
        {
            using var s = new ManagementObjectSearcher(stockage, requete);
            return s.Get().Cast<ManagementObject>().FirstOrDefault() ?? throw new IOException("rien pour " + requete);
        }
        static void Appeler(ManagementObject objet, string methode, params (string Nom, object Valeur)[] reglages)
        {
            using var entree = objet.GetMethodParameters(methode);
            foreach (var (nom, valeur) in reglages) entree[nom] = valeur;
            using var sortie = objet.InvokeMethod(methode, entree, null);
            if (Convert.ToUInt32(sortie["ReturnValue"]) is var code and not 0) throw new IOException($"{methode} : code {code}");
        }
        var chrono = Stopwatch.StartNew();
        try
        {
            string disque = $"SELECT * FROM MSFT_Disk WHERE Number = {t.Disque}";
            // Sur une clé en MBR, « Clear » réécrit lui-même le premier secteur, code de démarrage des vieux PC compris, et efface
            // ce qui reste d'une ancienne table. Une clé en GPT (une clé Linux) passe donc d'abord en MBR, puis est vidée une
            // seconde fois. Jamais de passage par GPT : il laisse le premier secteur sans code de démarrage (mesuré le 22/11/2026).
            using (var d = Un(disque)) Appeler(d, "Clear", ("RemoveData", true), ("RemoveOEM", true));
            using (var d = Un(disque))
                if (Convert.ToInt32(d["PartitionStyle"]) == 2)
                {
                    Appeler(d, "ConvertStyle", ("PartitionStyle", (ushort)1));
                    using var mbr = Un(disque);
                    Appeler(mbr, "Clear", ("RemoveData", true), ("RemoveOEM", true));
                }
            using (var d = Un(disque))
            {
                // une clé formatée d'un bloc, sans table de partitions, garde son volume après « Clear » : diskpart sait la vider
                if (Convert.ToInt32(d["PartitionStyle"]) != 1 || Convert.ToInt32(d["NumberOfPartitions"]) != 0) throw new IOException("la clé n'est pas vide");
                if (format != null)
                    Appeler(d, "CreatePartition", !neuf && t.Taille > 33_500_000_000 ? ("Size", (object)(32000UL << 20)) : ("UseMaximumSize", (object)true),
                        ("MbrType", (ushort)(format == "FAT32" ? 12 : 7)), ("IsActive", !neuf));
            }
            if (format != null)
            {
                using var partition = Un($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {t.Disque}");
                // Windows met parfois quelques secondes à rattacher son volume à une partition toute neuve
                ManagementObject volume = null;
                for (int i = 0; i < 40 && (volume = partition.GetRelated("MSFT_Volume").Cast<ManagementObject>().FirstOrDefault()) == null; i++) Thread.Sleep(250);
                using (volume ?? throw new IOException("volume introuvable"))
                    Appeler(volume, "Format", neuf ? [("FileSystem", format), ("Full", false), ("Force", true)]
                        : [("FileSystem", format), ("FileSystemLabel", "AUTOEXEC"), ("Full", false), ("Force", true)]);
                lettre = LettreLibre(); // choisie au dernier moment
                Appeler(partition, "AddAccessPath", ("AccessPath", $"{lettre}:"));
                // dernier contrôle, lu sur la clé elle-même : table MBR, code de démarrage présent, plus de table GPT derrière
                var debut = new byte[4096];
                using (var brut = CreateFile($@"\\.\PhysicalDrive{t.Disque}", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    if (brut.IsInvalid || RandomAccess.Read(brut, debut, 0) < 1024) throw new IOException("premier secteur illisible");
                if (debut[510] != 0x55 || debut[511] != 0xAA || debut.Take(440).Count(o => o != 0) < 100 || Encoding.ASCII.GetString(debut, 512, 8) == "EFI PART")
                    throw new IOException("premier secteur inattendu");
            }
            Journal.Ecrire($"Clé effacée par le service de stockage en {chrono.Elapsed.TotalSeconds:0.0} s");
            return true;
        }
        catch (Exception ex) when (ex is ManagementException or IOException or COMException or InvalidCastException or UnauthorizedAccessException)
        {
            Journal.Ecrire($"Effacement par le service de stockage refusé après {chrono.Elapsed.TotalSeconds:0.0} s, diskpart prend le relais : {ex.Message}");
            return false;
        }
    }

    public const int Bloc = 32 << 20;

    /// <summary>Linux : le fichier .iso est copié tel quel sur la clé, secteur par secteur. C'est ce que demandent Ubuntu, Mint,
    /// Debian et Fedora : leur fichier est déjà l'image d'une clé qui démarre, sur les PC récents comme sur les anciens.</summary>
    static void CopieExacte(Travail t)
    {
        // ouvert avant d'effacer : un fichier devenu illisible ne doit rien coûter à la clé
        using var image = File.OpenHandle(t.Image);
        bool deplier = t.Genre == "raspberry";
        long taille = deplier ? t.TailleImage : RandomAccess.GetLength(image);
        if (taille + Bloc > t.Taille)
            throw new InvalidOperationException(Textes.T("La clé est trop petite : il faut au moins {0} Go.", Math.Ceiling((taille + Bloc) / 1073741824.0)));
        Dire("cle", 0);
        SiAnnule();
        // clé vidée de toutes ses partitions : Windows n'a alors plus aucun volume à protéger contre l'écriture directe
        Effacer(t, "linux");
        VerifierCle(t);
        Dire("cle", 100);
        try
        {
            using var cle = CreateFile($@"\\.\PhysicalDrive{t.Disque}", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (cle.IsInvalid) throw new IOException($"ouverture de la clé refusée (erreur Windows {Marshal.GetLastWin32Error()})");
            if (deplier)
            {
                using var deplie = new Xz(new FileStream(t.Image, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20));
                CopierFlux(deplie, cle, taille, t.EmpreinteImage);
            }
            else Copier(image, cle, taille);
            // La clé est éjectée, comme le fait Etcher. Tant qu'elle reste lisible, Windows la relit au moindre événement de
            // disque : il réécrit alors un secteur de sa table (vu le 22/11/2026 : Fedora refuse ensuite son propre contrôle
            // au démarrage), lui donne une lettre et propose de la formater.
#if !SANS_EJECTION // un essai qui doit relire la clé juste après se compile sans l'éjection (dotnet build -p:DefineConstants=SANS_EJECTION)
            if (!DeviceIoControl(cle, 0x2D4808, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) // IOCTL_STORAGE_EJECT_MEDIA
                Journal.Ecrire($"Éjection de la clé refusée (erreur Windows {Marshal.GetLastWin32Error()})");
            else Journal.Ecrire("Clé éjectée");
#endif
        }
        catch (InvalidDataException ex)
        {
            // le fichier téléchargé avait pourtant la bonne empreinte : réessayer donnerait la même chose, on ne le propose pas
            Journal.Ecrire("Image illisible : " + ex.Message);
            throw new InvalidOperationException(Textes.T("Cette image n'a pas pu être lue. La carte a été vidée."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Journal.Ecrire("Copie exacte interrompue : " + ex.Message);
            throw new InvalidOperationException(Textes.T("La clé ne répond plus. Rebranche-la et recommence."));
        }
        Dire("fin", 100);
    }

    /// <summary>Raspberry Pi : l'image arrive dépliée au fil de l'eau, on ne peut pas y revenir. Elle est copiée sur la carte
    /// pendant que son empreinte est calculée, puis la carte est relue et son empreinte calculée à son tour : les deux
    /// doivent être celle que Raspberry publie pour l'image dépliée. Le premier bloc part en dernier, comme pour Linux.</summary>
    public static void CopierFlux(Stream image, SafeFileHandle cle, long taille, string empreinte)
    {
        byte[] premier = new byte[Bloc], tampon = new byte[Bloc];
        byte[][] tampons = [tampon, new byte[Bloc]];
        Action<string> noter = _dossier == null ? _ => { } : Journal.Ecrire;
        int Remplir(byte[] vers)
        {
            int n = 0, lu;
            while (n < Bloc && (lu = image.Read(vers, n, Bloc - n)) > 0) n += lu;
            Array.Clear(vers, n, Bloc - n);
            return n;
        }
        static string Hexa(System.Security.Cryptography.IncrementalHash sha) => Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        static int Secteurs(int n) => (n + 4095) / 4096 * 4096; // un disque ne s'écrit et ne se relit que par secteurs entiers
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var chrono = Stopwatch.StartNew();
        int debut = Remplir(premier);
        sha.AppendData(premier, 0, debut);
        long fait = debut;
        // pendant qu'un bloc part sur la carte, le suivant est déjà déplié : les deux ne s'attendent pas l'un l'autre
        var lecture = Task.Run(() => Remplir(tampons[0]));
        try
        {
            for (long position = Bloc, i = 0; ; position += Bloc, i++)
            {
                int n = lecture.GetAwaiter().GetResult();
                if (n == 0) break;
                byte[] bloc = tampons[i % 2], autre = tampons[(i + 1) % 2];
                lecture = Task.Run(() => Remplir(autre));
                fait += n;
                if (fait > taille) break; // plus long que l'image annoncée : refusé juste après
                sha.AppendData(bloc, 0, n);
                RandomAccess.Write(cle, bloc.AsSpan(0, Secteurs(n)), position);
                Dire("copie", Math.Min(100, 100.0 * fait / taille));
                SiAnnule();
            }
        }
        finally { try { lecture.Wait(); } catch { } } // aucune lecture ne doit survivre à la fermeture du fichier
        // rien de tout cela n'est utilisable tant que le premier bloc manque : une image fausse ne donnera pas de carte
        if (fait != taille || Hexa(sha) != empreinte)
        {
            noter($"Image dépliée inattendue : {fait} octets pour {taille} attendus, ou empreinte différente");
            throw new InvalidDataException("image dépliée différente de celle publiée");
        }
        noter($"copie : {taille - Bloc} octets en {chrono.Elapsed.TotalSeconds:0} s");
        if (!FlushFileBuffers(cle)) noter($"Vidage de la clé non confirmé (erreur Windows {Marshal.GetLastWin32Error()}) : la relecture tranchera");

        chrono.Restart();
        sha.AppendData(premier, 0, debut);
        for (long position = Bloc; position < taille; position += Bloc)
        {
            int n = (int)Math.Min(Bloc, taille - position), lu = 0, r;
            while (lu < Secteurs(n) && (r = RandomAccess.Read(cle, tampon.AsSpan(lu, Secteurs(n) - lu), position + lu)) > 0) lu += r;
            if (lu < n) throw new IOException("relecture incomplète");
            sha.AppendData(tampon, 0, n);
            Dire("verif", Math.Min(100, 100.0 * (position + n) / taille));
            SiAnnule();
        }
        bool pareil = Hexa(sha) == empreinte;
        if (pareil)
        {
            RandomAccess.Write(cle, premier.AsSpan(0, Secteurs(debut)), 0);
            FlushFileBuffers(cle);
            int lu = 0, r;
            while (lu < Secteurs(debut) && (r = RandomAccess.Read(cle, tampon.AsSpan(lu, Secteurs(debut) - lu), lu)) > 0) lu += r;
            pareil = lu >= debut && premier.AsSpan(0, debut).SequenceEqual(tampon.AsSpan(0, debut));
        }
        noter($"verif : {taille - Bloc} octets en {chrono.Elapsed.TotalSeconds:0} s");
        if (pareil) return;
        noter("Vérification : la carte ne rend pas l'image qu'on y a écrite");
        throw new InvalidOperationException(Textes.T("La clé ne garde pas ce qu'on y écrit. Essaie une autre clé."));
    }

    /// <summary>Copie l'image sur la clé, puis relit la clé et la compare à l'image, bloc par bloc.
    /// Sans « ecrire » : la comparaison seule.</summary>
    public static void Copier(SafeFileHandle image, SafeFileHandle cle, long taille, bool ecrire = true)
    {
        byte[][] tampons = [new byte[Bloc], new byte[Bloc]];
        var relu = new byte[Bloc];
        Action<string> noter = _dossier == null ? _ => { } : Journal.Ecrire; // l'autotest n'écrit pas dans le journal de l'appli
        // un disque ne s'écrit et ne se relit que par secteurs entiers : la fin du dernier bloc est complétée par des zéros
        int Charger(byte[] tampon, long position)
        {
            int n = 0, lu;
            while (n < Bloc && (lu = RandomAccess.Read(image, tampon.AsSpan(n), position + n)) > 0) n += lu;
            Array.Clear(tampon, n, Bloc - n);
            return (n + 4095) / 4096 * 4096;
        }
        void Ecrire(byte[] tampon, int n, long position) => RandomAccess.Write(cle, tampon.AsSpan(0, n), position);
        void Comparer(byte[] tampon, int n, long position)
        {
            int fait = 0, lu;
            while (fait < n && (lu = RandomAccess.Read(cle, relu.AsSpan(fait, n - fait), position + fait)) > 0) fait += lu;
            if (fait == n && tampon.AsSpan(0, n).SequenceEqual(relu.AsSpan(0, n))) return;
            noter($"Vérification : la clé diffère de l'image dans le bloc qui commence à l'octet {position}");
            throw new InvalidOperationException(Textes.T("La clé ne garde pas ce qu'on y écrit. Essaie une autre clé."));
        }
        // Tous les blocs sauf le premier. Pendant qu'un bloc part sur la clé (ou en revient), le suivant est déjà lu sur le
        // disque du PC : les deux lectures ne s'attendent pas l'une l'autre.
        void Parcourir(string etape, Action<byte[], int, long> faire)
        {
            var chrono = Stopwatch.StartNew();
            var lecture = Task.FromResult(0);
            try
            {
                if (Bloc < taille) lecture = Task.Run(() => Charger(tampons[0], Bloc));
                for (long position = Bloc, i = 0; position < taille; position += Bloc, i++)
                {
                    int n = lecture.GetAwaiter().GetResult();
                    long suivante = position + Bloc;
                    var autre = tampons[(i + 1) % 2];
                    if (suivante < taille) lecture = Task.Run(() => Charger(autre, suivante));
                    faire(tampons[i % 2], n, position);
                    Dire(etape, Math.Min(100, 100.0 * suivante / taille));
                    SiAnnule();
                }
            }
            finally { try { lecture.Wait(); } catch { } } // aucune lecture ne doit survivre à la fermeture du fichier
            noter($"{etape} : {taille - Bloc} octets en {chrono.Elapsed.TotalSeconds:0} s");
        }
        // Le premier bloc porte la table des partitions : il part en dernier. Tant qu'il manque, Windows ne voit sur la clé
        // aucune partition : il n'en ouvre donc aucune (ce qui bloquerait l'écriture) et n'y écrit rien de lui-même (ce qui
        // fausserait la comparaison).
        if (ecrire)
        {
            Parcourir("copie", Ecrire);
            if (!FlushFileBuffers(cle)) noter($"Vidage de la clé non confirmé (erreur Windows {Marshal.GetLastWin32Error()}) : la relecture tranchera");
        }
        Parcourir("verif", Comparer);
        int premier = Charger(tampons[0], 0);
        if (ecrire)
        {
            Ecrire(tampons[0], premier, 0);
            FlushFileBuffers(cle);
        }
        Comparer(tampons[0], premier, 0);
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

    public static string ScriptEffacer(int disque, long taille, bool partition = true) => string.Join("\r\n",
        new[] { $"select disk {disque}", "clean" }
            // Windows refuse de formater en FAT32 au-delà de 32 Go : sur une grosse clé, la partition s'arrête là
            .Concat(partition ? ["convert mbr", "create partition primary" + (taille > 33_500_000_000 ? " size=32000" : "")] : [])
            .Concat(["exit", ""]));

    /// <summary>Sans « format » : la clé d'installation de Windows. Avec (FAT32 ou exFAT) : une clé remise à neuf, sans nom.</summary>
    public static string ScriptFormater(int disque, char lettre, string format = null) => string.Join("\r\n",
        new[] { $"select disk {disque}", "select partition 1" }
            .Concat(format == null ? ["format fs=fat32 quick label=AUTOEXEC", "active"] : [$"format fs={format.ToLowerInvariant()} quick"])
            .Concat([$"assign letter={lettre}", "exit", ""]));

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

    static void SiAnnule() { if (_dossier != null && File.Exists(Path.Combine(_dossier, FichierAnnuler))) throw new Annule(); }

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
    [DllImport("kernel32", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr entree, uint tailleEntree, IntPtr sortie, uint tailleSortie, out uint rendus, IntPtr attente);

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
