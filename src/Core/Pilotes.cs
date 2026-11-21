using System.Management;

namespace Autoexec.Core;

record Pilote(string Inf, string Nom);

static class Pilotes
{
    /// <summary>Pilotes Wi-Fi/Ethernet ajoutés à ce PC (oemNN.inf) pour du vrai matériel ; les cartes virtuelles (VPN…) sont laissées.</summary>
    public static List<Pilote> Reseau()
    {
        var liste = new List<Pilote>();
        using var s = new ManagementObjectSearcher("SELECT DeviceName, InfName, DeviceID FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'");
        foreach (ManagementObject d in s.Get())
            using (d)
            {
                string inf = (d["InfName"] as string ?? "").ToLowerInvariant(), id = d["DeviceID"] as string ?? "";
                bool materiel = new[] { @"PCI\", @"USB\", @"SD\" }.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (inf.StartsWith("oem") && materiel && !liste.Any(p => p.Inf == inf))
                    liste.Add(new Pilote(inf, d["DeviceName"] as string ?? inf));
            }
        return liste;
    }

    /// <summary>Pilotes des cartes Wi-Fi et Ethernet les plus répandues, pour un PC qui n'a pas les mêmes cartes que celui-ci.
    /// Ce sont ceux de Windows Update, pris au catalogue public de Microsoft ; le nom de chaque fichier porte son empreinte SHA-1.
    /// Liste choisie et entretenue avec tools\pilotes.ps1 (cartes couvertes : voir README.md).</summary>
    // shortcut: liste figée dans l'appli, à sortir dans un fichier mis à jour en ligne quand l'appli saura se mettre à jour
    public static readonly ImageWindows[] Courants =
    [
        Paquet("intel-wifi", 24189879, "d/msdownload/update/driver/drvs/2026/01/482bde6e-59be-44a7-9977-f767402f555e_29b8fc460f645ece8071cbd332c5141ed993c4b4.cab"),
        Paquet("realtek-wifi6-8852be", 2477980, "d/msdownload/update/driver/drvs/2026/07/f69a5370-88eb-4023-886c-3e64260f5876_f512db5869cfdecdc184baa482a687e1c10c5ae8.cab"),
        Paquet("realtek-wifi6-8852ce", 2648876, "d/msdownload/update/driver/drvs/2026/08/2b0f02a5-083d-46d2-8587-df293974bae9_000d52b59c07edf604b86ee5a3d58e56ac20b742.cab"),
        Paquet("realtek-wifi6-8852ae", 1863793, "d/msdownload/update/driver/drvs/2026/03/534a7d16-5e86-4f6e-ad08-f0f3de0f4604_7b63656fbe6dfce5dbd20361ba8af03ece851571.cab"),
        Paquet("realtek-wifi6-8851be", 3289508, "d/msdownload/update/driver/drvs/2026/07/3967e55d-f16a-4e9b-bc13-e0d84445a3ad_b26508e31121b6d4e3e8064da9b0b08c276dbb57.cab"),
        Paquet("realtek-wifi7-8922ae", 3243270, "d/msdownload/update/driver/drvs/2026/10/f8e5331f-6170-4f6d-b6a1-4ee673cc50c2_06028d5c2beff434e1454c56bf2f0719ec7ef882.cab"),
        Paquet("realtek-wifi5", 2899794, "d/msdownload/update/driver/drvs/2026/03/eca7302e-cc51-428e-a42e-dda3c18aca8e_2f9bf7a8e56e10993524eb5d4771ac53e097eaf8.cab"),
        Paquet("realtek-wifi6-usb", 4174171, "c/msdownload/update/driver/drvs/2026/02/646d09f9-6ef6-47d6-8908-0384d11f4acc_ffc875b2e052800d92910b5d564db42891452d7e.cab"),
        Paquet("mediatek-wifi6", 7244024, "c/msdownload/update/driver/drvs/2026/10/65703f07-5d21-411d-bf07-3eb830a46957_31a20943d060d3b000ffaa2897ab7878f4b82ca9.cab"),
        Paquet("mediatek-wifi7", 22293098, "d/msdownload/update/driver/drvs/2026/10/087c8719-cfa5-4a11-989e-561b36f50ee8_5fe86e4340149a1303ee6362687cefc3a43cd20d.cab"),
        Paquet("mediatek-mt7663", 1959160, "d/msdownload/update/driver/drvs/2023/06/68904f9b-8c93-4815-8f97-dc3c22b64da6_72650bcd8ef61881d90ec81ee716c2f94e4fe8e6.cab"),
        Paquet("qualcomm-wcn6855", 3441636, "d/msdownload/update/driver/drvs/2026/08/2de10b53-5ae0-4e22-8f45-ec079882208d_f8d709dd17158eefa961c8bc5c9a5a46ba33c819.cab"),
        Paquet("qualcomm-wcn6855-w10", 3395338, "d/msdownload/update/driver/drvs/2026/08/e62e8822-b035-4d01-bd84-b07d0898d10e_0bc0154f09232e5a09e3828cc29d7a97b0c163f7.cab"),
        Paquet("qualcomm-wcn7850", 4689864, "c/msdownload/update/driver/drvs/2026/10/e55e0e90-ca15-4896-85a1-6f9b2f4c56e0_aa2199a67ea3b23de3e3b3415481800760e885b7.cab"),
        Paquet("intel-ethernet-i219", 286449, "c/msdownload/update/driver/drvs/2026/08/004763f6-ba6e-4fe4-8390-40e36d37e6a2_a4b7540e32f70c5ad75f041de46c3ed97b556174.cab"),
        Paquet("intel-ethernet-i226", 389173, "d/msdownload/update/driver/drvs/2026/06/6673f0b1-5eef-4ed8-bf3d-b831fe75c7e6_498295c3726ca53ad14a5e24aae7a89a2973f979.cab"),
        Paquet("intel-ethernet-i226-w10", 286927, "d/msdownload/update/driver/drvs/2025/03/feb4cd83-9ec0-45a9-b43c-710ae8d13101_ad2f08d91ffd5f98d85b806ee0030ccad3fb11c5.cab"),
        Paquet("realtek-ethernet", 601479, "d/msdownload/update/driver/drvs/2026/04/12410157-0483-4eca-94ce-a6ed4e6fea65_c1a514029b5b741cadd058d6be1cca0fe1c739f2.cab"),
        Paquet("marvell-ethernet-aqc", 145187, "c/msdownload/update/driver/drvs/2026/04/3cd471a2-0ce8-49dc-bcd4-5f56875b0b31_0b848d71d8be6f07f2351ff315939f805d80a7aa.cab"),
    ];

    static ImageWindows Paquet(string nom, long taille, string chemin) =>
        new("https://catalog.s.download.windowsupdate.com/" + chemin, taille, chemin[^44..^4], nom + ".cab");
}
