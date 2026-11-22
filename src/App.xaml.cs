using System.Windows;
using Autoexec.Core;

namespace Autoexec;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { Journal.Ecrire("Erreur : " + a.Exception); a.Handled = true; };
        // le même exe sert de travailleur en administrateur (sans fenêtre) et d'autotest
        if (e.Args.Length == 2 && e.Args[0] == "--preparer")
        {
            var fichier = e.Args[1];
            Shutdown(await Task.Run(() => Preparation.Executer(fichier)));
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--autotest")
        {
            Shutdown(await Task.Run(Autotest.Lancer));
            return;
        }
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        // Une seule fenêtre à la fois : deux fenêtres se partageraient le dossier de travail, la clé et le réglage de lecture
        // automatique. Un second lancement met la première fenêtre devant et s'arrête. (L'attente laisse à une version qui
        // vient de se mettre à jour le temps de se fermer.)
        _seule = new Mutex(false, @"Local\Autoexec-fenetre");
        var autre = System.Diagnostics.Process.GetProcessesByName("Autoexec").FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
        bool libre;
        // sans autre fenêtre à l'écran, celui qui tient encore la place est en train de se fermer : on l'attend
        try { libre = _seule.WaitOne(0) || (autre == null && _seule.WaitOne(5000)); }
        catch (AbandonedMutexException) { libre = true; } // la fenêtre précédente a été tuée
        if (!libre)
        {
            if (autre != null) { ShowWindow(autre.MainWindowHandle, 9); SetForegroundWindow(autre.MainWindowHandle); } // 9 = la sortir de la barre des tâches
            Shutdown();
            return;
        }
        // Tant que l'appli est ouverte, Windows ne doit rien ouvrir de lui-même quand une clé apparaît : ni l'Explorateur,
        // ni « Formater le disque ? » devant une clé Linux qu'il ne sait pas lire (vu le 22/11/2026 : sans lecture
        // automatique, cette question ne vient pas). Rufus fait de même. Elle est remise à la fermeture.
        _fenetre = true;
        Autoexec.MainWindow.LectureAuto(true);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    bool _fenetre; // le travail en administrateur et l'autotest, eux, ne touchent pas à la lecture automatique
    Mutex _seule;  // tenu jusqu'à la fin du processus

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr fenetre);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr fenetre, int commande);

    protected override void OnExit(ExitEventArgs e)
    {
        if (_fenetre) Autoexec.MainWindow.LectureAuto(false);
        base.OnExit(e);
    }
}
