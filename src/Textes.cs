using System.Globalization;
using System.Windows.Markup;
using Microsoft.Win32;

namespace Autoexec;

/// <summary>Les deux langues de l'appli. Le texte français sert de clé : le code et la fenêtre s'écrivent en français,
/// et T() rend l'anglais quand il est choisi. Un texte sans traduction reste en français (l'autotest les compte).</summary>
public static class Textes
{
    /// <summary>Langue choisie (gardée dans le registre), sinon celle de Windows : français pour un Windows en français, anglais pour tous les autres.</summary>
    public static bool Anglais = Lire() ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "fr";

    static bool? Lire()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"Software\Autoexec")?.GetValue("Langue") is string l ? l == "en" : null; }
        catch { return null; }
    }

    public static void Choisir(bool anglais)
    {
        Anglais = anglais;
        try { Registry.CurrentUser.CreateSubKey(@"Software\Autoexec").SetValue("Langue", anglais ? "en" : "fr"); } catch { }
    }

    public static string T(string fr) => Anglais && En.TryGetValue(fr, out var en) ? en : fr;
    public static string T(string fr, params object[] valeurs) => string.Format(T(fr), valeurs);

    public static readonly Dictionary<string, string> En = new()
    {
        // fenêtre
        ["Réduire"] = "Minimize",
        ["Fermer"] = "Close",
        ["Passer en anglais"] = "Switch to French",
        ["Préparer la clé"] = "Prepare the drive",
        ["Clé USB"] = "USB drive",
        ["Branche une clé USB"] = "Plug in a USB drive",
        ["Elle apparaîtra ici."] = "It will show up here.",
        ["{0} Go"] = "{0} GB",
        [" · protégée en écriture"] = " · write-protected",
        [" · tout sera effacé"] = " · everything will be erased",
        ["Windows 10 ne reçoit plus de mises à jour de sécurité."] = "Windows 10 no longer gets security updates.",
        ["Édition de Windows"] = "Windows edition",
        ["Langue de Windows"] = "Windows language",
        ["Toutes les éditions"] = "All editions",
        ["Famille"] = "Home",
        ["Professionnel"] = "Pro",
        ["Éducation"] = "Education",
        ["Réglages"] = "Settings",
        ["Compte local, sans compte Microsoft"] = "Local account, no Microsoft account",
        ["Pilotes Wi-Fi et Ethernet"] = "Wi-Fi and Ethernet drivers",
        ["Ceux des cartes les plus courantes."] = "For the most common cards.",
        ["Ceux des cartes les plus courantes, et ceux de ce PC :"] = "For the most common cards, plus those of this PC:",
        ["Sans questions de confidentialité"] = "Skip the privacy questions",
        ["Accepter les PC sans TPM"] = "Allow PCs without TPM",
        ["Garder le fichier téléchargé"] = "Keep the downloaded file",
        // choix du système, page Linux, clé remise à neuf
        ["Revenir au choix"] = "Back to the choice",
        ["Quelle clé veux-tu préparer ?"] = "Which drive do you want to prepare?",
        ["11 ou 10"] = "11 or 10",
        ["Remettre une clé à neuf"] = "Make a drive like new",
        ["Remettre la clé à neuf"] = "Make the drive like new",
        ["Elle redeviendra une clé ordinaire, vide."] = "It will become an ordinary, empty drive again.",
        ["Pour une clé que Windows montre vide ou minuscule."] = "For a drive that Windows shows as empty or tiny.",
        ["Le plus répandu : on trouve de l'aide partout."] = "The most widespread: help is easy to find.",
        ["Le plus proche de Windows : idéal pour débuter."] = "The closest to Windows: ideal for beginners.",
        ["Sobre et très stable."] = "Plain and very stable.",
        ["Les nouveautés en premier."] = "The latest features first.",
        ["Version légère, pour un PC ancien"] = "Lightweight version, for an older PC",
        ["Le même système avec un bureau plus simple (Xfce), qui demande moins de mémoire."] = "The same system with a simpler desktop (Xfce) that needs less memory.",
        ["{0} sur {1}"] = "{0} on {1}",
        ["Vérification de la clé"] = "Checking the drive",
        ["Retire-la, puis démarre un PC dessus pour essayer ou installer {0}."] = "Unplug it, then start a PC from it to try or install {0}.",
        ["Sous Windows, cette clé ne se lit pas. S'il propose de la formater, réponds Annuler."] = "Windows cannot read this drive. If it offers to format it, answer Cancel.",
        ["Si l'écran reste noir au démarrage, choisis « compatibility mode »."] = "If the screen stays black at startup, choose “compatibility mode”.",
        ["Évite de la rebrancher sous Windows : il la modifie, et Fedora la dirait abîmée. Si cela arrive, choisis « Start Fedora » au démarrage."]
            = "Avoid plugging it back in under Windows: it alters the drive, and Fedora would call it damaged. If that happens, choose “Start Fedora” at startup.",
        ["Elle est vide, comme une clé neuve."] = "It is empty, like a new drive.",
        ["Effacer « {0} » ({1}) ?"] = "Erase “{0}” ({1})?",
        ["Tout son contenu sera perdu."] = "Everything on it will be lost.",
        ["Annuler"] = "Cancel",
        ["Effacer et préparer"] = "Erase and prepare",
        ["Effacer"] = "Erase",
        // travail
        ["Windows {0} sur {1}"] = "Windows {0} on {1}",
        ["Tu peux continuer à utiliser le PC."] = "You can keep using your PC.",
        ["Téléchargement"] = "Download",
        ["Préparation de Windows"] = "Preparing Windows",
        ["Pilotes et réglages"] = "Drivers and settings",
        ["Effacement de la clé"] = "Erasing the drive",
        ["Copie sur la clé"] = "Copying to the drive",
        ["Vérification du fichier…"] = "Checking the file…",
        ["{0} Go sur {1} Go"] = "{0} of {1} GB",
        [" · encore une minute environ"] = " · about a minute left",
        [" · encore {0} minutes environ"] = " · about {0} minutes left",
        ["Windows demande ton autorisation."] = "Windows is asking for your permission.",
        ["C'est l'étape la plus longue."] = "This is the longest step.",
        ["Arrêt en cours…"] = "Stopping…",
        ["Pour arrêter, clique sur Annuler."] = "To stop, click Cancel.",
        ["Retour"] = "Back",
        ["La clé est prête"] = "The drive is ready",
        ["Redémarre le PC dessus pour installer Windows."] = "Restart the PC from it to install Windows.",
        ["Préparer une autre clé"] = "Prepare another drive",
        ["Mise à jour en cours…"] = "Updating…",
        // erreurs
        ["Il manque {0} Go de place sur le disque {1}"] = "Drive {1} is {0} GB short of free space",
        ["Windows n'a pas donné l'autorisation. Sans elle, la clé ne peut pas être effacée."] = "Windows did not give permission. Without it, the drive cannot be erased.",
        ["La préparation de la clé a échoué."] = "Preparing the drive failed.",
        ["Ça n'a pas marché. Vérifie la connexion Internet et réessaie."] = "That did not work. Check your Internet connection and try again.",
        ["Le catalogue de Microsoft est illisible."] = "Microsoft's catalog could not be read.",
        ["Microsoft ne répond pas. Vérifie la connexion Internet."] = "Microsoft is not responding. Check your Internet connection.",
        ["Windows est introuvable dans le catalogue de Microsoft."] = "Windows was not found in Microsoft's catalog.",
        ["Le catalogue pointe ailleurs que chez Microsoft."] = "The catalog points somewhere other than Microsoft.",
        ["Le fichier téléchargé est abîmé. Réessaie."] = "The downloaded file is damaged. Try again.",
        ["Ce n'est pas un fichier Windows (.esd)."] = "This is not a Windows file (.esd).",
        ["Fichier Windows illisible."] = "The Windows file could not be read.",
        ["Une erreur est survenue pendant la préparation."] = "Something went wrong during preparation.",
        ["Les pilotes réseau de ce PC n'ont pas pu être copiés. Réessaie, ou décoche les pilotes."] = "This PC's network drivers could not be copied. Try again, or turn the drivers off.",
        ["Les pilotes n'ont pas pu être préparés. Réessaie, ou décoche les pilotes."] = "The drivers could not be prepared. Try again, or turn the drivers off.",
        ["La clé est trop petite : il faut au moins {0} Go."] = "The drive is too small: at least {0} GB is needed.",
        ["La clé n'a pas pu être effacée. Débranche-la, rebranche-la et réessaie."] = "The drive could not be erased. Unplug it, plug it back in and try again.",
        ["La clé ne répond plus. Rebranche-la et recommence."] = "The drive stopped responding. Plug it back in and start again.",
        ["La copie sur la clé est incomplète. Recommence, ou essaie une autre clé."] = "The copy to the drive is incomplete. Start again, or try another drive.",
        ["La clé a été retirée ou remplacée."] = "The drive was removed or replaced.",
        ["La clé est protégée en écriture."] = "The drive is write-protected.",
        ["Le fichier Windows n'a pas la forme attendue."] = "The Windows file does not have the expected layout.",
        ["Cette édition n'est pas dans le fichier Windows."] = "This edition is not in the Windows file.",
        ["Windows n'a pas pu être préparé pour la clé."] = "Windows could not be prepared for the drive.",
        ["Il n'y a plus de lettre de lecteur libre."] = "No drive letter is left.",
        ["La fin de l'écriture sur la clé n'a pas pu être confirmée. Recommence."] = "The end of the write to the drive could not be confirmed. Start again.",
        ["Le site de {0} ne répond pas. Vérifie la connexion Internet."] = "The {0} website is not responding. Check your Internet connection.",
        ["{0} est introuvable sur son site."] = "{0} could not be found on its website.",
        ["La clé ne garde pas ce qu'on y écrit. Essaie une autre clé."] = "The drive does not keep what is written to it. Try another drive.",
        ["Arrêté. La clé a déjà été effacée : il faudra la refaire."] = "Stopped. The drive was already erased: it will have to be made again.",
        ["Une autre préparation est déjà en cours sur ce PC. Attends qu'elle soit finie."] = "Another preparation is already running on this PC. Wait until it is finished.",
    };
}

/// <summary>Dans la fenêtre : Text="{l:T 'Préparer la clé'}". Le texte est rendu dans la langue choisie à l'ouverture de la fenêtre.</summary>
[MarkupExtensionReturnType(typeof(string))]
public class T(string fr) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider services) => Textes.T(fr);
}
