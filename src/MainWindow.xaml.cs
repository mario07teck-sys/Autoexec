using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Autoexec.Core;
using Microsoft.Win32;

namespace Autoexec;

/// <summary>Une ligne de l'écran de progression. Etat : attente, encours, fait ou erreur.</summary>
public class Etape : INotifyPropertyChanged
{
    string _etat = "attente", _droite = "";
    public string Code { get; init; }
    public string Texte { get; init; }
    public string Etat { get => _etat; set { _etat = value; PropertyChanged?.Invoke(this, new(nameof(Etat))); } }
    public string Droite { get => _droite; set { _droite = value; PropertyChanged?.Invoke(this, new(nameof(Droite))); } }
    public event PropertyChangedEventHandler PropertyChanged;
}

public partial class MainWindow : Window
{
    static readonly string Cache = Path.Combine(Journal.Dossier, "cache"), DossierPilotes = Path.Combine(Cache, "pilotes"), DossierTravail = Path.Combine(Journal.Dossier, "travail");

    readonly ObservableCollection<Cle> _cles = new();
    readonly ObservableCollection<Etape> _etapes = new();
    readonly DispatcherTimer _attente = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly DispatcherTimer _suivi = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Stopwatch _chrono = new();
    // lancée dès l'ouverture : l'interrogation de Windows prend quelques secondes
    readonly Task<List<Pilote>> _recherchePilotes = Task.Run(() =>
    {
        try { return Pilotes.Reseau(); }
        catch (Exception ex) { Journal.Ecrire("Pilotes : " + ex.Message); return new List<Pilote>(); }
    });
    CancellationTokenSource _cts;
    Cle _nommee; // la clé nommée dans la question « Effacer … ? »
    Action _surTick;
    bool _enCours, _verification, _cleEffacee;
    long _fait, _total, _depart;
    string _erreurTravail;
    static bool _miseAJourCherchee; // une fois par lancement, pas à chaque changement de langue
    static string _genre; // « windows », « linux », « raspberry » ou « neuf » : gardé quand la fenêtre se rouvre pour changer de langue
    Distribution _distribution = Linux.Toutes[0];

    public MainWindow()
    {
        InitializeComponent();
        if (_genre != null) Choisir(_genre);
        Liste.ItemsSource = _cles;
        ListeEtapes.ItemsSource = _etapes;
        BoutonLangue.Content = Textes.Anglais ? "FR" : "EN";
        // chaque langue de Windows sous son propre nom, comme Windows les présente
        Langue.ItemsSource = Catalogue.Langues.Select(l => new KeyValuePair<string, string>(l, NomDeLangue(l)))
            .OrderBy(p => p.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
        Langue.SelectedValue = Catalogue.LangueDuPc();
        _attente.Tick += async (_, _) => { _attente.Stop(); await Rafraichir(); };
        _suivi.Tick += (_, _) => _surTick?.Invoke();
        Loaded += async (_, _) =>
        {
            _ = MettreAJour();
            await Rafraichir();
            var pilotes = await _recherchePilotes;
            OptPilotes.ToolTip = pilotes.Count == 0 ? Textes.T("Ceux des cartes les plus courantes.")
                : Textes.T("Ceux des cartes les plus courantes, et ceux de ce PC :") + "\n" + string.Join("\n", pilotes.Select(p => p.Nom));
        };
    }

    static string NomDeLangue(string code)
    {
        var nom = CultureInfo.GetCultureInfo(code).NativeName;
        return char.ToUpper(nom[0]) + nom[1..];
    }

    /// <summary>Au lancement : s'il existe une version plus récente sur GitHub, elle s'installe toute seule et l'appli se relance.</summary>
    async Task MettreAJour()
    {
        if (_miseAJourCherchee) return;
        _miseAJourCherchee = true;
        try
        {
            MiseAJour.Nettoyer();
            // jamais par-dessus un travail ou une confirmation déjà engagés : ce sera pour le prochain lancement
            if (await MiseAJour.Chercher(default) is not { } version || _enCours || Confirmation.Visibility == Visibility.Visible) return;
            Journal.Ecrire($"Mise à jour : {MiseAJour.Actuelle} → {version.Numero}");
            _enCours = true;
            PageMiseAJour.Visibility = Visibility.Visible;
            await MiseAJour.Installer(version, p => JaugeMiseAJour.Value = p, default);
            _enCours = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Journal.Ecrire("Mise à jour : " + ex.Message);
            PageMiseAJour.Visibility = Visibility.Collapsed;
            _enCours = false;
        }
    }

    void Langue_Click(object sender, RoutedEventArgs e)
    {
        if (_enCours) return;
        Textes.Choisir(!Textes.Anglais);
        // les textes sont lus à l'ouverture de la fenêtre : on en ouvre une autre, au même endroit
        var fenetre = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = Left, Top = Top };
        Application.Current.MainWindow = fenetre;
        fenetre.Show();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this);
        // angles arrondis de Windows 11 (sans effet sur Windows 10) et cadre sombre, comme les autres applis
        int rond = 2, sombre = 1;
        DwmSetWindowAttribute(source.Handle, 33, ref rond, 4);
        DwmSetWindowAttribute(source.Handle, 20, ref sombre, 4);
        source.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool traite) =>
        {
            // WM_DEVICECHANGE : une clé arrive ou part ; on attend que Windows ait fini de la reconnaître
            if (msg == 0x0219 && !_enCours) { _attente.Stop(); _attente.Start(); }
            return IntPtr.Zero;
        });
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr fenetre, int attribut, ref int valeur, int taille);

    async Task Rafraichir()
    {
        List<Cle> trouvees;
        try { trouvees = await Task.Run(Cles.Lister); }
        catch (Exception ex) { Journal.Ecrire("Liste des clés : " + ex.Message); trouvees = new(); }
        trouvees.RemoveAll(c => !Cles.Proposee(c, _genre));
        var choisie = (Liste.SelectedItem as Cle)?.Chemin;
        _cles.Clear();
        foreach (var c in trouvees) _cles.Add(c);
        // une seule clé : elle est choisie d'office ; plusieurs : à l'utilisateur de désigner celle qui sera effacée
        Liste.SelectedItem = _cles.FirstOrDefault(c => c.Chemin == choisie) ?? (_cles.Count == 1 ? _cles[0] : null);
        SansCle.Visibility = _cles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Liste.Visibility = _cles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BoutonPreparer.IsEnabled = Liste.SelectedItem is Cle { Protegee: false };
    }

    void Liste_SelectionChanged(object sender, SelectionChangedEventArgs e) => BoutonPreparer.IsEnabled = Liste.SelectedItem is Cle { Protegee: false };

    void Windows_Change(object sender, RoutedEventArgs e)
    {
        bool dix = Win10.IsChecked == true;
        NoteWin10.Visibility = dix ? Visibility.Visible : Visibility.Collapsed;
        OptTpm.IsEnabled = !dix; // seul Windows 11 exige un TPM
    }

    static Visibility Si(bool vrai) => vrai ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Une distribution est choisie : sa phrase d'aide, et ce qu'elle propose (version légère, ou choix de la carte graphique).</summary>
    void Linux_Change(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string nom }) _distribution = Linux.Toutes.First(d => d.Nom == nom);
        if (OptLegere == null) return; // la fenêtre se construit encore
        NoteLinux.Text = Textes.T(_distribution.Note);
        Carte.Visibility = Si(_distribution.CarteGraphique);
        // montré seulement quand la version légère existe : un interrupteur grisé mais coché promettrait ce qu'on ne fait pas
        OptLegere.Visibility = Si(_genre == "linux" && _distribution.Legere);
    }

    void Raspberry_Change(object sender, RoutedEventArgs e)
    {
        if (NoteRaspberry == null) return; // la fenêtre se construit encore
        NoteRaspberry.Text = Textes.T(PiAncien.IsChecked == true ? "Pour les Pi 1, Pi 2, Zero et Zero W." : "Aussi pour les Pi 400, 500 et Zero 2 W.");
    }

    void Choix_Click(object sender, RoutedEventArgs e) => Choisir((string)((FrameworkElement)sender).Tag);

    void Choisir(string genre)
    {
        _genre = genre;
        bool pi = genre == "raspberry";
        BlocWindows.Visibility = ReglagesWindows.Visibility = Si(genre == "windows");
        BlocLinux.Visibility = Si(genre == "linux");
        BlocRaspberry.Visibility = OptSansBureau.Visibility = Si(pi);
        BlocReglages.Visibility = Si(genre != "neuf");
        NoteNeuf.Visibility = Si(genre == "neuf");
        // un Raspberry Pi démarre le plus souvent sur une carte SD : sur sa page, on parle de carte
        TitreCle.Text = Textes.T(pi ? "Carte SD ou clé USB" : "Clé USB");
        TexteSansCle.Text = Textes.T(pi ? "Branche une carte SD" : "Branche une clé USB");
        BoutonPreparer.Content = Textes.T(genre == "neuf" ? "Remettre la clé à neuf" : pi ? "Préparer la carte" : "Préparer la clé");
        BoutonConfirmer.Content = Textes.T(genre == "neuf" ? "Effacer" : "Effacer et préparer");
        Linux_Change(null, null);
        Raspberry_Change(null, null);
        Montrer(PageAccueil);
        _ = Rafraichir(); // la plus petite clé proposée dépend de ce qu'on veut en faire
    }

    void Changer_Click(object sender, RoutedEventArgs e)
    {
        if (_enCours || Confirmation.Visibility == Visibility.Visible) return; // pas de retour en arrière sous une question ouverte
        _genre = null;
        Montrer(PageChoix);
    }

    void Preparer_Click(object sender, RoutedEventArgs e)
    {
        if (Liste.SelectedItem is not Cle { Protegee: false } cle) return;
        TitreConfirmation.Text = Textes.T("Effacer « {0} » ({1}) ?", cle.Nom, cle.Go);
        _nommee = cle;
        Confirmation.Visibility = Visibility.Visible;
        BoutonConfirmer.Focus();
    }

    void Refuser_Click(object sender, RoutedEventArgs e) => Confirmation.Visibility = Visibility.Collapsed;

    async void Confirmer_Click(object sender, RoutedEventArgs e)
    {
        Confirmation.Visibility = Visibility.Collapsed;
        // seule la clé nommée dans la question est effacée : si une autre a pris sa place entre-temps, on ne fait rien
        if (Liste.SelectedItem is Cle cle && cle == _nommee && !_enCours) await Travailler(cle);
    }

    async Task Travailler(Cle cle)
    {
        string genre = _genre, distribution = genre == "raspberry" ? "Raspberry Pi OS" : _distribution.Nom;
        bool win = genre == "windows", linux = genre == "linux", pi = genre == "raspberry", copie = linux || pi; // copie : l'image va telle quelle sur la clé
        bool pi64 = PiAncien.IsChecked != true, sansBureau = OptSansBureau.IsChecked == true;
        var choisie = _distribution;
        // Bazzite : une image par famille de carte graphique ; les autres : la version légère quand elle existe
        string variante = choisie.CarteGraphique ? (string)((ComboBoxItem)Carte.SelectedItem).Tag : choisie.Legere && OptLegere.IsChecked == true ? "legere" : "";
        int windows = Win10.IsChecked == true ? 10 : 11;
        // les réglages de Windows ne valent que pour Windows
        bool compte = win && OptCompte.IsChecked == true, questions = win && OptQuestions.IsChecked == true, tpm = win && windows == 11 && OptTpm.IsChecked == true,
             garder = OptGarder.IsChecked == true, pilotes = win && OptPilotes.IsChecked == true;
        string langue = (string)Langue.SelectedValue, edition = (string)((ComboBoxItem)Edition.SelectedItem).Tag;
        _etapes.Clear();
        // Windows : la clé n'est effacée qu'une fois Windows prêt sur le disque du PC, un échec avant ne lui coûte rien.
        // Linux : le fichier téléchargé est déjà l'image de la clé, il n'y a rien à préparer.
        (string, string)[] etapes = win ? [("telechargement", "Téléchargement"), ("windows", "Préparation de Windows"),
                ("finitions", "Pilotes et réglages"), ("cle", "Effacement de la clé"), ("copie", "Copie sur la clé"), ("verif", "Vérification de la clé")]
            : linux ? [("telechargement", "Téléchargement"), ("cle", "Effacement de la clé"), ("copie", "Copie sur la clé"), ("verif", "Vérification de la clé")]
            : pi ? [("telechargement", "Téléchargement"), ("cle", "Effacement de la carte"), ("copie", "Copie sur la carte"), ("verif", "Vérification de la carte")]
            : [("cle", "Effacement de la clé")];
        foreach (var (code, texte) in etapes) _etapes.Add(new Etape { Code = code, Texte = Textes.T(texte) });
        TitreTravail.Text = win ? Textes.T("Windows {0} sur {1}", windows, cle.Nom) : copie ? Textes.T("{0} sur {1}", distribution, cle.Nom) : cle.Nom;
        TitreFin.Text = Textes.T(pi ? "La carte est prête" : "La clé est prête");
        TexteFin.Text = win ? Textes.T("Redémarre le PC dessus pour installer Windows.")
            : pi ? Textes.T("Retire-la, mets-la dans le Raspberry Pi, puis allume-le.")
            // Kali et Arch ne se téléchargent qu'en clé d'installation : on ne promet pas d'essai
            : linux ? Textes.T(choisie.Essai ? "Retire-la, puis démarre un PC dessus pour essayer ou installer {0}." : "Retire-la, puis démarre un PC dessus pour installer {0}.", distribution)
            : Textes.T("Elle est vide, comme une clé neuve.");
        // Windows ne sait pas lire une clé Linux, et il en réécrit un secteur dès qu'il la revoit : Fedora, qui se contrôle
        // elle-même au démarrage, s'en aperçoit (vu le 22/11/2026)
        NoteFin.Text = pi ? Textes.T("Au premier démarrage, il lui faut un écran et un clavier.")
            : distribution == "Fedora"
            ? Textes.T("Évite de la rebrancher sous Windows : il la modifie, et Fedora la dirait abîmée. Si cela arrive, choisis « Start Fedora » au démarrage.")
            : Textes.T("Sous Windows, cette clé ne se lit pas. S'il propose de la formater, réponds Annuler.")
              // vu le 22/11/2026 sur une machine ancienne (BIOS), avec la clé comme avec le DVD officiel : l'entrée par
              // défaut de Mint n'affiche pas son bureau, la deuxième ligne du menu si
              + (distribution == "Linux Mint" ? " " + Textes.T("Si l'écran reste noir au démarrage, choisis « compatibility mode ».") : "")
              // vu le 22/11/2026 en machine virtuelle, Secure Boot activé : après l'installation, l'écran bleu de MokManager
              // (clavier américain) ; sans cette étape, Bazzite installé ne démarre pas Secure Boot activé (sa documentation)
              + (distribution == "Bazzite" ? " " + Textes.T("Après l'installation, un écran bleu peut s'afficher : choisis « Enroll MOK », « Continue », « Yes », puis tape universalblue. Sur un clavier français, tape Q à la place du A.") : "")
              + (choisie.SecureBoot ? "" : " " + Textes.T("Si le PC refuse de démarrer dessus, coupe « Secure Boot » dans ses réglages."));
        NoteFin.Visibility = Si(copie);
        MessageErreur.Visibility = Visibility.Collapsed;
        BoutonAnnuler.Content = Textes.T("Annuler");
        BoutonAnnuler.IsEnabled = true;
        Reste.Text = "";
        Jauge.Value = 0;
        Montrer(PageTravail);

        _enCours = true;
        _cts = new();
        _erreurTravail = null;
        _cleEffacee = false;
        SetThreadExecutionState(0x80000001); // pas de mise en veille pendant le travail
        string fichier = null;
        Raspberry.Image raspberry = null;
        bool reussi = false;
        try
        {
            var ct = _cts.Token;
            var paquets = new List<string>();
            if (genre != "neuf") // une clé remise à neuf n'a rien à télécharger
            {
                Avancer("telechargement", 0);
                if (pi) raspberry = await Raspberry.Chercher(pi64, sansBureau, ct);
                var img = pi ? raspberry.Fichier : linux ? await Linux.Chercher(distribution, variante, ct) : await Catalogue.Chercher(windows, langue, ct);
                // Linux : la taille du fichier est celle qu'il prendra sur la clé (pour un Raspberry Pi, celle de l'image une
                // fois dépliée). Trop petite, autant le dire avant de télécharger, et dans l'unité de la liste des clés.
                long surCle = pi ? raspberry.Taille : img.Taille;
                if (copie && surCle + Preparation.Bloc > cle.Taille)
                    throw new InvalidOperationException(Textes.T("La clé est trop petite : il faut au moins {0} Go.", Math.Ceiling((surCle + Preparation.Bloc) / 1073741824.0)));
                fichier = Path.Combine(Cache, img.Nom);

                // place nécessaire : le fichier (s'il n'est pas déjà là), puis, pour Windows, sa version dépliée avant l'envoi
                // sur la clé. Un Windows à découper passe deux fois sur le disque (mesuré : 13 Go pour un fichier de 4,7 Go).
                Directory.CreateDirectory(Cache);
                long deja = new[] { fichier, fichier + ".part" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
                // les pilotes des cartes courantes : leurs .cab, puis leur contenu déplié (mesuré : 290 Mo pour 86)
                long lot = pilotes ? Pilotes.Courants.Sum(p => p.Taille) : 0;
                // (3,5 fois depuis que le .wim à découper est compressé vite : il sort à 6,5 Go au lieu de 6,0)
                long besoin = img.Taille - deja + (win ? img.Taille * (img.Taille > Preparation.MaxFat32 ? 7 : 3) / 2 : 0) + lot * 5, libre = new DriveInfo(Path.GetPathRoot(Cache)).AvailableFreeSpace;
                if (libre < besoin)
                    throw new InvalidOperationException(Textes.T("Il manque {0} Go de place sur le disque {1}", Math.Ceiling((besoin - libre) / 1073741824.0), Path.GetPathRoot(Cache)));

                // une seule jauge pour Windows puis les pilotes : elle ne repart pas de zéro entre les deux
                _fait = 0; _total = img.Taille + lot; _depart = -1; _verification = false;
                _surTick = SuivreTelechargement;
                _suivi.Start();
                await Telechargement.Recuperer(img, fichier,
                    (f, _) => { _verification = false; Interlocked.Exchange(ref _fait, f); },
                    (f, _) => { _verification = true; Interlocked.Exchange(ref _fait, f); }, ct);
                long avant = img.Taille;
                foreach (var paquet in pilotes ? Pilotes.Courants : [])
                {
                    var dest = Path.Combine(DossierPilotes, paquet.Nom);
                    long debut = avant;
                    try
                    {
                        await Telechargement.Recuperer(paquet, dest, (f, _) => { _verification = false; Interlocked.Exchange(ref _fait, debut + f); }, null, ct);
                        paquets.Add(dest);
                    }
                    // un pilote que Microsoft a retiré ne doit pas empêcher de faire la clé : les autres restent bons
                    catch (HttpRequestException ex) when (ex.StatusCode != null) { Journal.Ecrire($"Pilote {paquet.Nom} introuvable chez Microsoft (code {(int)ex.StatusCode})"); }
                    avant += paquet.Taille;
                }
                _suivi.Stop();
            }

            Directory.CreateDirectory(DossierTravail);
            foreach (var f in new[] { Preparation.FichierEtat, Preparation.FichierAnnuler }) File.Delete(Path.Combine(DossierTravail, f));
            string reglages = null;
            if (Reglages.Fabriquer(compte, questions, tpm) is { } doc) doc.Save(reglages = Path.Combine(DossierTravail, "reglages.xml"));
            // la liste des pilotes se remplit en arrière-plan à l'ouverture : on l'attend, sinon un clic rapide les oublierait
            var travail = new Preparation.Travail(cle.Numero, cle.Chemin, cle.Taille, fichier, edition, reglages, tpm,
                pilotes ? (await _recherchePilotes).Select(p => p.Inf).ToArray() : [], paquets.ToArray(), DossierTravail, Textes.Anglais, genre,
                raspberry?.Taille ?? 0, raspberry?.Empreinte);
            var fichierTravail = Path.Combine(DossierTravail, "travail.json");
            File.WriteAllText(fichierTravail, JsonSerializer.Serialize(travail));

            Avancer(win ? "windows" : "cle", 0, Textes.T("Windows demande ton autorisation."));
            Process p;
            try
            {
                // seul ce travail tourne en administrateur : Windows affiche sa demande d'autorisation ici
                p = await Task.Run(() => Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--preparer \"{fichierTravail}\"")
                    { UseShellExecute = true, Verb = "runas" }));
            }
            catch (Win32Exception ex)
            {
                // 1223 = demande refusée ou restée sans réponse
                Journal.Ecrire($"Autorisation non obtenue (code {ex.NativeErrorCode}) : {ex.Message}");
                throw new InvalidOperationException(Textes.T("Windows n'a pas donné l'autorisation. Sans elle, la clé ne peut pas être effacée."));
            }
            using (p)
            {
                _surTick = LireEtat;
                _suivi.Start();
                var annuler = Path.Combine(DossierTravail, Preparation.FichierAnnuler);
                using (ct.Register(() => { try { File.WriteAllText(annuler, ""); } catch { } }))
                    await p.WaitForExitAsync(); // même annulé, on laisse le travail s'arrêter proprement
                _suivi.Stop();
                LireEtat();
                // une clé terminée reste terminée, même si « Annuler » a été cliqué à la dernière seconde
                if (p.ExitCode != 0)
                {
                    if (ct.IsCancellationRequested || p.ExitCode == 2) throw new OperationCanceledException();
                    throw new InvalidOperationException(_erreurTravail ?? Textes.T("La préparation de la clé a échoué."));
                }
            }
            reussi = true;
            Montrer(PageFin);
        }
        // arrêt demandé avant que la clé soit entamée : comme si de rien n'était
        catch (OperationCanceledException) when (!_cleEffacee) { Montrer(PageAccueil); }
        catch (Exception ex)
        {
            // arrêt demandé après le début de l'effacement : la clé n'est plus utilisable telle quelle, il faut le dire
            bool arret = ex is OperationCanceledException;
            Journal.Ecrire(arret ? "Arrêt demandé alors que la clé était déjà effacée" : "Échec : " + ex);
            if (_etapes.FirstOrDefault(e => e.Etat == "encours") is { } active) { active.Etat = "erreur"; active.Droite = ""; }
            MessageErreur.Text = arret ? Textes.T("Arrêté. La clé a déjà été effacée : il faudra la refaire.")
                : ex is InvalidOperationException or InvalidDataException or TimeoutException
                ? ex.Message : Textes.T("Ça n'a pas marché. Vérifie la connexion Internet et réessaie.");
            MessageErreur.Visibility = Visibility.Visible;
            BoutonAnnuler.Content = Textes.T("Retour");
            Reste.Text = "";
        }
        finally
        {
            _suivi.Stop();
            _enCours = false;
            BoutonAnnuler.IsEnabled = true;
            SetThreadExecutionState(0x80000000);
            // le fichier Windows est supprimé une fois la clé faite ; après un échec il reste, pour ne pas tout retélécharger
            if (reussi && !garder && fichier != null) try { File.Delete(fichier); Directory.Delete(DossierPilotes, true); } catch { }
            _cts.Dispose();
            _cts = null;
            await Rafraichir();
        }
    }

    /// <summary>Tant que l'appli est ouverte, Windows ne doit rien ouvrir quand une clé apparaît (Explorateur, « que faire de ce
    /// disque ? », « Formater le disque ? ») : la lecture automatique de l'utilisateur est coupée au lancement, puis remise
    /// comme elle était à la fermeture (Rufus fait de même). L'état d'origine est gardé à part : si l'appli est tuée, il est
    /// remis à la fermeture de son prochain lancement.</summary>
    internal static void LectureAuto(bool couper)
    {
        try
        {
            using var windows = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers");
            using var memo = Registry.CurrentUser.CreateSubKey(@"Software\Autoexec");
            if (couper)
            {
                if (memo.GetValue("LectureAuto") == null) memo.SetValue("LectureAuto", windows.GetValue("DisableAutoplay") ?? 0, RegistryValueKind.DWord);
                windows.SetValue("DisableAutoplay", 1, RegistryValueKind.DWord);
            }
            else if (memo.GetValue("LectureAuto") is int avant)
            {
                windows.SetValue("DisableAutoplay", avant, RegistryValueKind.DWord);
                memo.DeleteValue("LectureAuto");
            }
        }
        catch (Exception ex) { Journal.Ecrire("Lecture automatique : " + ex.Message); }
    }

    void SuivreTelechargement()
    {
        long fait = Interlocked.Read(ref _fait);
        if (_verification) { Avancer("telechargement", 100.0 * fait / _total, Textes.T("Vérification du fichier…")); return; }
        if (fait == 0) return;
        var texte = Textes.T("{0} Go sur {1} Go", $"{fait / 1073741824.0:0.0}", $"{_total / 1073741824.0:0.0}");
        if (_depart < 0) { _depart = fait; _chrono.Restart(); }
        else if (_chrono.Elapsed.TotalSeconds > 5 && fait > _depart)
        {
            double secondes = (_total - fait) / ((fait - _depart) / _chrono.Elapsed.TotalSeconds);
            texte += secondes < 90 ? Textes.T(" · encore une minute environ") : Textes.T(" · encore {0} minutes environ", Math.Ceiling(secondes / 60));
        }
        Avancer("telechargement", 100.0 * fait / _total, texte);
    }

    /// <summary>Relit la dernière ligne « étape|pourcent|texte » écrite par le travail en administrateur.</summary>
    void LireEtat()
    {
        string derniere = null;
        try
        {
            using var f = new FileStream(Path.Combine(DossierTravail, Preparation.FichierEtat), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(f);
            for (string l; (l = r.ReadLine()) != null;) if (l.Split('|').Length == 3) derniere = l;
        }
        catch (IOException) { return; } // pas encore écrit
        if (derniere == null) return;
        var m = derniere.Split('|');
        if (m[0] == "erreur") { if (m[2] == "annule-efface") _cleEffacee = true; else if (m[2] != "annule") _erreurTravail = m[2]; }
        else if (m[0] == "fin") foreach (var e in _etapes) { e.Etat = "fait"; e.Droite = ""; }
        else Avancer(m[0], double.Parse(m[1], CultureInfo.InvariantCulture));
    }

    void Avancer(string code, double pourcent, string reste = null)
    {
        bool avant = true;
        foreach (var e in _etapes)
        {
            if (e.Code == code) { avant = false; e.Etat = "encours"; e.Droite = $"{pourcent:0} %"; }
            else if (avant) { e.Etat = "fait"; e.Droite = ""; }
        }
        Jauge.Value = pourcent;
        Reste.Text = reste ?? (code == "windows" ? Textes.T("C'est l'étape la plus longue.") : "");
    }

    void Annuler_Click(object sender, RoutedEventArgs e)
    {
        if (!_enCours) { Montrer(PageAccueil); return; }
        BoutonAnnuler.IsEnabled = false;
        Reste.Text = Textes.T("Arrêt en cours…");
        _cts?.Cancel();
    }

    void Retour_Click(object sender, RoutedEventArgs e) => Montrer(PageAccueil);

    void Montrer(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { PageChoix, PageAccueil, PageTravail, PageFin })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        // la flèche de retour au choix prend la place du logo, sur la seule page où l'on peut encore changer d'avis
        BoutonRetour.Visibility = page == PageAccueil ? Visibility.Visible : Visibility.Collapsed;
        Logo.Visibility = page == PageAccueil ? Visibility.Collapsed : Visibility.Visible;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // par sécurité, la fenêtre ne se ferme pas pendant le téléchargement ni la préparation : pour arrêter, il y a « Annuler »
        if (_enCours)
        {
            e.Cancel = true;
            if (BoutonAnnuler.IsEnabled) Reste.Text = Textes.T("Pour arrêter, clique sur Annuler.");
        }
        base.OnClosing(e);
    }

    [DllImport("kernel32")]
    static extern uint SetThreadExecutionState(uint etat);
}
