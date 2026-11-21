namespace Autoexec.Core;

/// <summary>%LOCALAPPDATA%\Autoexec\journal.log, comme toutes les applis.</summary>
static class Journal
{
    static readonly object Verrou = new();

    /// <summary>Le travail en administrateur peut tourner sous un autre compte : il reçoit ce dossier de la fenêtre.</summary>
    public static string Dossier = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autoexec");

    public static void Ecrire(string msg)
    {
        lock (Verrou) try
        {
            Directory.CreateDirectory(Dossier);
            var path = Path.Combine(Dossier, "journal.log");
            // au-delà de 1 Mo, l'ancien journal est gardé à côté (un seul) et on repart de zéro
            if (new FileInfo(path) is { Exists: true, Length: > 1_000_000 }) File.Move(path, Path.Combine(Dossier, "journal.old.log"), true);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }
}
