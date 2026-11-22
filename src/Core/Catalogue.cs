using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;

namespace Autoexec.Core;

/// <summary>Un fichier à télécharger, tel que son éditeur le décrit. Empreinte : SHA-1 chez Microsoft (40 caractères),
/// SHA-256 pour Linux (64).</summary>
record ImageWindows(string Url, long Taille, string Empreinte, string Nom);

/// <summary>Catalogue public de l'outil de création de média de Microsoft (products.cab).</summary>
static class Catalogue
{
    public static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static string Lien(int windows) => "https://go.microsoft.com/fwlink/?LinkId=" + (windows == 10 ? "841361" : "2156292");

    /// <summary>Les langues de Windows que le catalogue propose, pour Windows 10 comme pour Windows 11 (l'autotest le revérifie).</summary>
    public static readonly string[] Langues = ("ar-sa bg-bg cs-cz da-dk de-de el-gr en-gb en-us es-es es-mx et-ee fi-fi fr-ca fr-fr he-il hr-hr hu-hu it-it ja-jp ko-kr "
        + "lt-lt lv-lv nb-no nl-nl pl-pl pt-br pt-pt ro-ro ru-ru sk-sk sl-si sr-latn-rs sv-se th-th tr-tr uk-ua zh-cn zh-tw").Split(' ');

    /// <summary>La langue de ce PC si le catalogue l'a, sinon la même langue d'un autre pays, sinon l'anglais.</summary>
    public static string LangueDuPc()
    {
        var pc = CultureInfo.CurrentUICulture;
        return Langues.FirstOrDefault(l => l.Equals(pc.Name, StringComparison.OrdinalIgnoreCase))
            ?? Langues.FirstOrDefault(l => l.StartsWith(pc.TwoLetterISOLanguageName + "-")) ?? "en-us";
    }

    public static async Task<ImageWindows> Chercher(int windows, string langue, CancellationToken ct) => Choisir(await Document(windows, ct), langue);

    public static async Task<XDocument> Document(int windows, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "autoexec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            string cab = Path.Combine(tmp, "products.cab"), xml = Path.Combine(tmp, "products.xml");
            await File.WriteAllBytesAsync(cab, await Http.GetByteArrayAsync(Lien(windows), delai.Token), delai.Token);
            // le .cab ne contient qu'un fichier : expand.exe (livré avec Windows) le sort sous le nom demandé
            using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"), $"\"{cab}\" \"{xml}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
            {
                await p.StandardOutput.ReadToEndAsync(delai.Token);
                await p.WaitForExitAsync(delai.Token);
            }
            if (!File.Exists(xml)) throw new InvalidOperationException(Textes.T("Le catalogue de Microsoft est illisible."));
            return XDocument.Load(xml);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Textes.T("Microsoft ne répond pas. Vérifie la connexion Internet."));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Windows grand public 64 bits, dans la langue demandée.</summary>
    public static ImageWindows Choisir(XDocument doc, string langue)
    {
        var f = doc.Descendants("File").FirstOrDefault(f =>
            (string)f.Element("Architecture") == "x64" && (string)f.Element("Edition") == "Professional"
            && ((string)f.Element("FileName") ?? "").Contains("CLIENTCONSUMER")
            && string.Equals((string)f.Element("LanguageCode"), langue, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(Textes.T("Windows est introuvable dans le catalogue de Microsoft."));
        var url = new Uri((string)f.Element("FilePath"));
        // le lien vient du catalogue : on n'accepte que les serveurs de Microsoft
        if (!url.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Textes.T("Le catalogue pointe ailleurs que chez Microsoft."));
        return new ImageWindows(url.ToString(), (long)f.Element("Size"), ((string)f.Element("Sha1")).ToLowerInvariant(), (string)f.Element("FileName"));
    }
}
