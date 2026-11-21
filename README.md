# Autoexec

Prépare toute seule une clé USB d'installation de Windows 10 ou 11 : on branche la clé, on choisit Windows, on clique sur « Préparer la clé ».

Un seul fichier, `Autoexec.exe`, sans installation : on le lance, c'est tout. En français ou en anglais (bouton `EN` / `FR` en haut de la fenêtre). L'appli se met à jour toute seule au lancement.

## Ce que fait l'appli

1. **Télécharge Windows chez Microsoft** : le fichier `.esd` du catalogue public de l'outil de création de média (`products.cab`), le même que l'outil de Microsoft, dans la langue choisie (38 langues). Reprise après coupure, vérification de la taille et de l'empreinte SHA-1.
2. **Prépare Windows** sur le disque du PC avec DISM (livré avec Windows), après confirmation puis demande d'autorisation de Windows : arborescence de la clé, `boot.wim`, puis `install.esd`, ou `install.swm` et `install2.swm` quand le fichier dépasse 4 Go (le cas de Windows 11). Les **réglages et les pilotes** y sont ajoutés. Tant que tout cela n'a pas réussi, la clé n'est pas touchée.
3. **Efface la clé** : table MBR, une partition FAT32 nommée `AUTOEXEC`, limitée à 32 Go sur les grosses clés.
4. **Copie le tout sur la clé**. Pendant le travail, la lecture automatique de Windows est coupée pour que rien ne s'ouvre, puis remise comme elle était.

Pendant le téléchargement et la préparation, la fenêtre ne se ferme pas : pour arrêter, il y a « Annuler ».

## Réglages

| Réglage | Ce que ça écrit sur la clé |
|---|---|
| Édition (toutes, Famille, Professionnel, Éducation) | « Toutes » : la clé porte les 7 éditions et l'installation demande laquelle (sur un PC vendu avec Windows, elle prend d'office la sienne). Une édition choisie : `sources\ei.cfg`, et l'installation prend celle-là sans demander |
| Langue de Windows | le fichier Windows téléchargé est celui de cette langue |
| Compte local, sans compte Microsoft | `BypassNRO` (passe specialize) et `HideOnlineAccountScreens` |
| Sans questions de confidentialité | `ProtectYourPC = 3`, `HideEULAPage` |
| Accepter les PC sans TPM (Windows 11) | clés `LabConfig` (TPM, Secure Boot, mémoire), passe windowsPE |
| Pilotes Wi-Fi et Ethernet | dans `$WinPEDriver$` : les pilotes réseau de ce PC, et ceux des cartes les plus courantes (voir plus bas) |
| Garder le fichier Windows téléchargé | sinon il est supprimé une fois la clé faite |

Le fichier de réglages est posé dans `sources\$OEM$\$$\Panther\unattend.xml` (et à la racine en `autounattend.xml` quand il y a une passe windowsPE).

## Pilotes des cartes courantes

Une clé sert souvent à installer Windows sur un autre PC que celui qui la fabrique. En plus des pilotes de ce PC, la clé emporte donc ceux des cartes Wi-Fi et Ethernet les plus répandues que Windows ne connaît pas tout seul : 19 paquets, 86 Mo à télécharger, 290 Mo sur la clé. Ce sont les pilotes de Windows Update, pris au catalogue public de Microsoft (`catalog.update.microsoft.com`) par lien direct, avec vérification de l'empreinte. L'installation de Windows les range tous dans le Windows installé, qui se sert de ceux qui correspondent au PC.

| Fabricant | Cartes couvertes |
|---|---|
| Intel Wi-Fi | 9260, 9560, AX200, AX201, AX210, AX211, AX411, BE200, BE201 (et les Killer équivalentes) |
| Realtek Wi-Fi | RTL8852AE, 8852BE, 8852CE, 8851BE, 8922AE ; 8821CE, 8822BE, 8822CE, 8723DE ; clés USB Wi-Fi 6 (8852BU, 8832BU…) |
| MediaTek Wi-Fi | MT7921, MT7922, MT7902, MT7920 (AMD RZ608, RZ616), MT7925, MT7927, MT7663 |
| Qualcomm Wi-Fi | WCN6855 (FastConnect 6900), WCN7850 (FastConnect 7800) |
| Ethernet | Intel I219, I225, I226 ; Realtek 8168, 8125, 8126, 8127 ; Marvell AQC107, AQC113 |

Limites : aucun lot ne couvre tout ; les pilotes ne valent que pour les PC à processeur Intel ou AMD (x64) ; certains ne s'installent que sous Windows 11 (Wi-Fi 7, RTL8851BE, WCN7850). La liste est dans `Pilotes.Courants` (`src\Core\Pilotes.cs`) et s'entretient avec `tools\pilotes.ps1` : `-Cherche 'PCI\VEN_8086&DEV_2725'` liste les pilotes du catalogue pour un matériel, `-Paquet <identifiant> -Nom <nom> -Dossier <dossier>` télécharge un paquet, lit ses `.inf` et écrit la ligne à coller. Un paquet que Microsoft a retiré est sauté (noté au journal) ; l'autotest vérifie que tous existent encore.

## Organisation

- `src\MainWindow.xaml(.cs)` : la fenêtre, sans droits d'administrateur.
- `src\Core\Preparation.cs` : le travail en administrateur (`Autoexec.exe --preparer travail.json`), seul à toucher la clé. Il écrit son avancement dans `%LOCALAPPDATA%\Autoexec\travail\etat.txt`.
- `src\Core\Catalogue.cs`, `Telechargement.cs`, `Wim.cs`, `Reglages.cs`, `Pilotes.cs`, `Cles.cs`, `MiseAJour.cs` : une brique par fichier.
- `src\Textes.cs` : les deux langues de l'appli. Le texte français sert de clé (`Textes.T("…")` dans le code, `{l:T '…'}` dans la fenêtre) ; l'anglais est dans le dictionnaire `Textes.En`.
- `build.ps1` : fabrique l'exe unique, `dist\Autoexec.exe`.
- `tools\make-icon.ps1` : régénère `src\Assets\autoexec.ico`.
- `tools\pilotes.ps1` : entretien de la liste des pilotes des cartes courantes.
- `tools\essai\` : outils de l'essai réel sur une clé (chaque script porte son mode d'emploi en tête). `clic.ps1` actionne la fenêtre sans souris, `attendre.ps1` attend la fin du travail, `guetter.ps1` note toute fenêtre qui surgit, `capture.ps1` capture la fenêtre, `image-cle.ps1` copie la clé dans un disque virtuel, `vm.ps1` démarre une machine Hyper-V dessus et la pilote au clavier.
- Journal : `%LOCALAPPDATA%\Autoexec\journal.log`.

## Compiler et tester

```powershell
dotnet build src\Autoexec.csproj
src\bin\Debug\net10.0-windows10.0.19041.0\Autoexec.exe --autotest   # rapport : %TEMP%\autoexec-autotest.txt
.\build.ps1                                                          # l'exe unique : dist\Autoexec.exe
```

L'autotest vérifie tout ce qui se vérifie sans droits d'administrateur ni clé à effacer : catalogue, structure du fichier Windows (lue à distance), reprise de téléchargement, fichier de réglages, script d'effacement, pilotes de ce PC, pilotes des cartes courantes (toujours présents chez Microsoft, à la bonne taille), clés branchées. L'écriture d'une clé se teste avec une vraie clé.

## Publier une version

1. Augmenter `<Version>` dans `src\Autoexec.csproj` (jamais deux publications au même numéro).
2. `.\build.ps1`, puis `dist\Autoexec.exe --autotest`.
3. Publier sur GitHub une « release » nommée `vX.Y.Z` avec `dist\Autoexec.exe` en pièce jointe, sous ce nom exact : `gh release create vX.Y.Z dist\Autoexec.exe --title "Autoexec X.Y.Z" --notes "…"`.

Au lancement, l'appli demande à GitHub la dernière publication du dépôt (`MiseAJour.Depot`). Si son numéro est plus grand, elle télécharge `Autoexec.exe`, vérifie sa taille et l'empreinte SHA-256 que GitHub publie pour lui, le met à la place de l'exe en cours et se relance. Sans réseau, ou si l'exe est dans un dossier où l'on ne peut pas écrire, elle continue simplement avec la version en place.

## Sécurité

- Seules les clés sont proposées : disques USB que Windows déclare amovibles, de 7 à 300 Go, qui ne portent pas Windows. Un disque dur ou un SSD externe n'apparaît pas.
- Le travail en administrateur revérifie le numéro, le matériel et la taille de la clé juste avant d'effacer.
- Le lien de téléchargement doit pointer chez Microsoft, et le fichier doit avoir l'empreinte annoncée par le catalogue.
