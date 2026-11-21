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
    Action _surTick;
    bool _enCours, _verification;
    long _fait, _total, _depart;
    string _erreurTravail;
    static bool _miseAJourCherchee; // une fois par lancement, pas à chaque changement de langue

    public MainWindow()
    {
        InitializeComponent();
        LectureAuto(false);
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

    void Preparer_Click(object sender, RoutedEventArgs e)
    {
        if (Liste.SelectedItem is not Cle { Protegee: false } cle) return;
        TitreConfirmation.Text = Textes.T("Effacer « {0} » ({1}) ?", cle.Nom, cle.Go);
        Confirmation.Visibility = Visibility.Visible;
        BoutonConfirmer.Focus();
    }

    void Refuser_Click(object sender, RoutedEventArgs e) => Confirmation.Visibility = Visibility.Collapsed;

    async void Confirmer_Click(object sender, RoutedEventArgs e)
    {
        Confirmation.Visibility = Visibility.Collapsed;
        if (Liste.SelectedItem is Cle cle && !_enCours) await Travailler(cle);
    }

    async Task Travailler(Cle cle)
    {
        int windows = Win10.IsChecked == true ? 10 : 11;
        bool compte = OptCompte.IsChecked == true, questions = OptQuestions.IsChecked == true, tpm = windows == 11 && OptTpm.IsChecked == true,
             garder = OptGarder.IsChecked == true, pilotes = OptPilotes.IsChecked == true;
        string langue = (string)Langue.SelectedValue, edition = (string)((ComboBoxItem)Edition.SelectedItem).Tag;
        _etapes.Clear();
        // la clé n'est effacée qu'une fois Windows prêt sur le disque du PC : un échec avant ne lui coûte rien
        foreach (var (code, texte) in new[] { ("telechargement", "Téléchargement"), ("windows", "Préparation de Windows"),
                     ("finitions", "Pilotes et réglages"), ("cle", "Effacement de la clé"), ("copie", "Copie sur la clé") })
            _etapes.Add(new Etape { Code = code, Texte = Textes.T(texte) });
        TitreTravail.Text = Textes.T("Windows {0} sur {1}", windows, cle.Nom);
        MessageErreur.Visibility = Visibility.Collapsed;
        BoutonAnnuler.Content = Textes.T("Annuler");
        BoutonAnnuler.IsEnabled = true;
        Reste.Text = "";
        Jauge.Value = 0;
        Montrer(PageTravail);

        _enCours = true;
        _cts = new();
        _erreurTravail = null;
        SetThreadExecutionState(0x80000001); // pas de mise en veille pendant le travail
        string fichier = null;
        bool reussi = false;
        try
        {
            var ct = _cts.Token;
            Avancer("telechargement", 0);
            var img = await Catalogue.Chercher(windows, langue, ct);
            fichier = Path.Combine(Cache, img.Nom);

            // place nécessaire : le fichier Windows (s'il n'est pas déjà là), puis sa version dépliée avant l'envoi sur la clé.
            // Un Windows à découper passe deux fois sur le disque (mesuré : 13 Go pour un fichier de 4,7 Go).
            Directory.CreateDirectory(Cache);
            long deja = new[] { fichier, fichier + ".part" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            // les pilotes des cartes courantes : leurs .cab, puis leur contenu déplié (mesuré : 290 Mo pour 86)
            long lot = pilotes ? Pilotes.Courants.Sum(p => p.Taille) : 0;
            long besoin = img.Taille - deja + img.Taille * 3 / (img.Taille > Preparation.MaxFat32 ? 1 : 2) + lot * 5, libre = new DriveInfo(Path.GetPathRoot(Cache)).AvailableFreeSpace;
            if (libre < besoin)
                throw new InvalidOperationException(Textes.T("Il manque {0} Go de place sur le disque {1}", Math.Ceiling((besoin - libre) / 1e9), Path.GetPathRoot(Cache)));

            // une seule jauge pour Windows puis les pilotes : elle ne repart pas de zéro entre les deux
            _fait = 0; _total = img.Taille + lot; _depart = -1; _verification = false;
            _surTick = SuivreTelechargement;
            _suivi.Start();
            await Telechargement.Recuperer(img, fichier,
                (f, _) => { _verification = false; Interlocked.Exchange(ref _fait, f); },
                (f, _) => { _verification = true; Interlocked.Exchange(ref _fait, f); }, ct);
            var paquets = new List<string>();
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

            Directory.CreateDirectory(DossierTravail);
            foreach (var f in new[] { Preparation.FichierEtat, Preparation.FichierAnnuler }) File.Delete(Path.Combine(DossierTravail, f));
            string reglages = null;
            if (Reglages.Fabriquer(compte, questions, tpm) is { } doc) doc.Save(reglages = Path.Combine(DossierTravail, "reglages.xml"));
            // la liste des pilotes se remplit en arrière-plan à l'ouverture : on l'attend, sinon un clic rapide les oublierait
            var travail = new Preparation.Travail(cle.Numero, cle.Chemin, cle.Taille, fichier, edition, reglages, tpm,
                pilotes ? (await _recherchePilotes).Select(p => p.Inf).ToArray() : [], paquets.ToArray(), DossierTravail, Textes.Anglais);
            var fichierTravail = Path.Combine(DossierTravail, "travail.json");
            File.WriteAllText(fichierTravail, JsonSerializer.Serialize(travail));

            Avancer("windows", 0, Textes.T("Windows demande ton autorisation."));
            LectureAuto(true);
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
        catch (OperationCanceledException) { Montrer(PageAccueil); }
        catch (Exception ex)
        {
            Journal.Ecrire("Échec : " + ex);
            if (_etapes.FirstOrDefault(e => e.Etat == "encours") is { } active) { active.Etat = "erreur"; active.Droite = ""; }
            MessageErreur.Text = ex is InvalidOperationException or InvalidDataException or TimeoutException
                ? ex.Message : Textes.T("Ça n'a pas marché. Vérifie la connexion Internet et réessaie.");
            MessageErreur.Visibility = Visibility.Visible;
            BoutonAnnuler.Content = Textes.T("Retour");
            Reste.Text = "";
        }
        finally
        {
            _suivi.Stop();
            LectureAuto(false);
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

    /// <summary>Pendant le travail, Windows ne doit rien ouvrir quand la clé réapparaît (Explorateur, « que faire de ce disque ? ») :
    /// la lecture automatique de l'utilisateur est coupée, puis remise comme elle était (Rufus fait de même). L'état d'origine
    /// est gardé à part : si l'appli est tuée en plein travail, il est remis à son prochain lancement.</summary>
    static void LectureAuto(bool couper)
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
        if (m[0] == "erreur") { if (m[2] != "annule") _erreurTravail = m[2]; }
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
        foreach (var p in new FrameworkElement[] { PageAccueil, PageTravail, PageFin })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
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
