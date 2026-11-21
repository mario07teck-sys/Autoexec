# Entretien de la liste des pilotes reseau courants (src\Core\Pilotes.cs, Pilotes.Courants).
# Les pilotes viennent du catalogue public de Microsoft (catalog.update.microsoft.com), celui de Windows Update.
#   -Cherche 'PCI\VEN_8086&DEV_2725'            liste les pilotes du catalogue pour ce materiel (premiere page)
#   -Paquet <identifiant> -Nom <nom> -Dossier <dossier>
#        telecharge le paquet, verifie son empreinte, le deplie, lit ses .inf (Windows vises, materiels reconnus)
#        et ecrit la ligne a coller dans Pilotes.cs
param([string]$Cherche, [string]$Paquet, [string]$Nom, [string]$Dossier, [string]$Filtre)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$site = 'https://www.catalog.update.microsoft.com'
if ($Cherche) {
  $page = (Invoke-WebRequest "$site/Search.aspx?q=$([uri]::EscapeDataString($Cherche))" -UseBasicParsing -TimeoutSec 90).Content
  [regex]::Match($page, '(?s)<span id="ctl00_catalogBody_searchDuration">(.*?)</span>').Groups[1].Value
  foreach ($l in [regex]::Matches($page, '(?s)<tr id="([0-9a-f-]{36})_R\d+".*?</tr>')) {
    $c = @([regex]::Matches($l.Value, '(?s)<td[^>]*>(.*?)</td>') | ForEach-Object { ($_.Groups[1].Value -replace '<[^>]+>', ' ' -replace '\s+', ' ').Trim() })
    $o = [pscustomobject]@{ Id = $l.Groups[1].Value; Date = [datetime]::ParseExact($c[4], 'M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture).ToString('yyyy-MM-dd')
      Version = $c[5]; Mo = [math]::Round([long]($c[6] -split ' ')[-1] / 1MB, 1); Titre = $c[1]; Produits = $c[2] -replace ',\s+', ', ' -replace 'Servicing Drivers', 'SD' }
    if (-not $Filtre -or "$($o.Titre) $($o.Produits)" -match $Filtre) { $o }
  }
  exit 0
}
if (-not ($Paquet -and $Nom -and $Dossier)) { 'il faut -Cherche, ou -Paquet avec -Nom et -Dossier'; exit 1 }
$corps = 'updateIDs=' + [uri]::EscapeDataString('[{"size":0,"languages":"","uidInfo":"' + $Paquet + '","updateID":"' + $Paquet + '"}]')
$rep = (Invoke-WebRequest "$site/DownloadDialog.aspx" -Method Post -Body $corps -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 90).Content
$liens = @([regex]::Matches($rep, "files\[\d+\]\.url = '([^']+)'") | ForEach-Object { $_.Groups[1].Value })
if ($liens.Count -ne 1) { "le paquet $Paquet a $($liens.Count) fichiers : un seul attendu"; exit 1 }
$lien = $liens[0]
$sha1 = [regex]::Match($lien, '_([0-9a-f]{40})\.cab$').Groups[1].Value
if (-not $sha1) { "lien sans empreinte : $lien"; exit 1 }
New-Item -ItemType Directory -Force $Dossier | Out-Null
$cab = Join-Path $Dossier "$Nom.cab"
if (-not (Test-Path $cab)) { Invoke-WebRequest $lien -OutFile $cab -UseBasicParsing -TimeoutSec 600 }
if ((Get-FileHash $cab -Algorithm SHA1).Hash.ToLower() -ne $sha1) { "empreinte differente pour $cab"; exit 1 }
$deplie = Join-Path $Dossier $Nom
if (Test-Path $deplie) { Remove-Item $deplie -Recurse -Force }
New-Item -ItemType Directory $deplie | Out-Null
& "$env:SystemRoot\System32\expand.exe" -F:* $cab $deplie | Out-Null
if ($LASTEXITCODE -ne 0) { "expand a echoue ($LASTEXITCODE)"; exit 1 }
$fichiers = Get-ChildItem $deplie -Recurse -File
foreach ($inf in $fichiers | Where-Object Extension -eq '.inf') {
  $texte = Get-Content $inf.FullName -Raw
  $vises = @([regex]::Matches($texte, '(?im)^\s*%?[^=\r\n;]+%?\s*=\s*[^,\r\n;]+((?:,\s*NT[^,\r\n;]+)+)') | ForEach-Object { $_.Groups[1].Value -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)
  $materiels = @([regex]::Matches($texte, '(?i)(PCI\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}|USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}|SD\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4})') | ForEach-Object { $_.Value.ToUpper() } | Sort-Object -Unique)
  $classe = [regex]::Match($texte, '(?im)^\s*Class\s*=\s*(\w+)').Groups[1].Value
  $ver = [regex]::Match($texte, '(?im)^\s*DriverVer\s*=\s*([^\r\n;]+)').Groups[1].Value.Trim()
  # "sans sous-modele" : le pilote prend la carte quel que soit le fabricant du PC ; sinon seulement les variantes qu'il enumere
  $generiques = @($materiels | Where-Object { $texte -match ([regex]::Escape($_) + '(?![&\w])') })
  "{0} : classe {1}, version {2}, {3} materiels dont {4} sans sous-modele" -f $inf.Name, $classe, $ver, $materiels.Count, $generiques.Count
  "   Windows vises : " + ($vises -join ' ')
  "   " + (($materiels | ForEach-Object { if ($generiques -contains $_) { $_ } else { "($_)" } } | Select-Object -First 400) -join ' ')
}
$taille = (Get-Item $cab).Length
"cab {0:N1} Mo, deplie {1:N1} Mo, {2} fichiers" -f ($taille / 1MB), (($fichiers | Measure-Object Length -Sum).Sum / 1MB), $fichiers.Count
"LIGNE  new(`"$lien`", $taille, `"$sha1`", `"$Nom`"),"
