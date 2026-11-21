using System.Text;
using System.Xml.Linq;

namespace Autoexec.Core;

/// <summary>Lit la liste des images d'un fichier .wim/.esd (la fiche XML rangée dans le fichier, jamais compressée).</summary>
static class Wim
{
    /// <summary>Edition vide = l'arborescence de la clé ; "WindowsPE" = les deux images de démarrage ; sinon une édition de Windows.</summary>
    public record Image(int Index, string Nom, string Edition);

    public static List<Image> Images(Func<long, int, byte[]> lire)
    {
        var h = lire(0, 208);
        if (Encoding.ASCII.GetString(h, 0, 5) != "MSWIM") throw new InvalidDataException(Textes.T("Ce n'est pas un fichier Windows (.esd)."));
        // en-tête : à l'octet 72, l'emplacement de la fiche XML (taille sur 7 octets, puis position)
        long taille = BitConverter.ToInt64(h, 72) & 0x00FFFFFFFFFFFFFF, position = BitConverter.ToInt64(h, 80);
        if (taille <= 0 || taille > 50_000_000) throw new InvalidDataException(Textes.T("Fichier Windows illisible."));
        var doc = XDocument.Parse(Encoding.Unicode.GetString(lire(position, (int)taille)).TrimStart('﻿'));
        return doc.Root.Elements("IMAGE").Select(i => new Image((int)i.Attribute("INDEX"), (string)i.Element("NAME") ?? "",
            (string)i.Element("WINDOWS")?.Element("EDITIONID") ?? "")).OrderBy(i => i.Index).ToList();
    }

    public static List<Image> Images(string fichier)
    {
        using var f = File.OpenRead(fichier);
        return Images((position, taille) => { f.Position = position; var b = new byte[taille]; f.ReadExactly(b); return b; });
    }
}
