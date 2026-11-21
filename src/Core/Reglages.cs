using System.Xml.Linq;

namespace Autoexec.Core;

/// <summary>Fichier de réponses lu par l'installation de Windows (unattend.xml), écrit à la main : trois réglages seulement.</summary>
static class Reglages
{
    static readonly XNamespace U = "urn:schemas-microsoft-com:unattend", W = "http://schemas.microsoft.com/WMIConfig/2002/State";

    /// <summary>null si rien n'est demandé. La passe windowsPE n'existe que pour sansTpm.</summary>
    public static XDocument Fabriquer(bool compteLocal, bool sansQuestions, bool sansTpm)
    {
        if (!compteLocal && !sansQuestions && !sansTpm) return null;
        var racine = new XElement(U + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", W));
        if (sansTpm)
            racine.Add(Passe("windowsPE", "Microsoft-Windows-Setup",
                // dès qu'il y a une passe windowsPE, l'installation exige cette rubrique (clé vide = elle demande ou lit celle du PC)
                new XElement(U + "UserData", new XElement(U + "AcceptEula", "true"), new XElement(U + "ProductKey", new XElement(U + "Key"))),
                Commandes(new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck" }
                    .Select(v => $@"reg add HKLM\SYSTEM\Setup\LabConfig /v {v} /t REG_DWORD /d 1 /f"))));
        if (compteLocal)
            racine.Add(Passe("specialize", "Microsoft-Windows-Deployment",
                Commandes(new[] { @"reg add ""HKLM\Software\Microsoft\Windows\CurrentVersion\OOBE"" /v BypassNRO /t REG_DWORD /d 1 /f" })));
        if (compteLocal || sansQuestions)
        {
            var oobe = new XElement(U + "OOBE");
            if (sansQuestions) oobe.Add(new XElement(U + "HideEULAPage", "true"), new XElement(U + "ProtectYourPC", "3"));
            if (compteLocal) oobe.Add(new XElement(U + "HideOnlineAccountScreens", "true"));
            racine.Add(Passe("oobeSystem", "Microsoft-Windows-Shell-Setup", oobe));
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), racine);
    }

    static XElement Passe(string passe, string composant, params object[] contenu) =>
        new(U + "settings", new XAttribute("pass", passe),
            new XElement(U + "component", new XAttribute("name", composant), new XAttribute("processorArchitecture", "amd64"),
                new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"),
                new XAttribute("versionScope", "nonSxS"), contenu));

    static XElement Commandes(IEnumerable<string> commandes) =>
        new(U + "RunSynchronous", commandes.Select((c, i) => new XElement(U + "RunSynchronousCommand", new XAttribute(W + "action", "add"),
            new XElement(U + "Order", i + 1), new XElement(U + "Path", c))));
}
