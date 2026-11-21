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
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
