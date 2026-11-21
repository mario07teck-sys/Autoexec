# Actionne un bouton, une case a cocher ou un bouton radio d'une fenetre par UI Automation (sans toucher a la souris),
# designe par son identifiant (x:Name).
# Sans -Bouton : liste les textes visibles de la fenetre.
# -Bouton Edition -Choix Professionnel : choisit une ligne d'une liste deroulante, par son texte.
param([string]$Processus = 'Autoexec', [string]$Bouton, [string]$Choix)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process $Processus -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "pas de fenetre pour $Processus"; exit 1 }
$fen = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
if (-not $Bouton) {
  $tous = $fen.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $tous) {
    $c = $e.Current
    if (-not $c.IsOffscreen -and $c.Name) { '{0,-12} {1,-18} {2}' -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.AutomationId, $c.Name }
  }
  exit 0
}
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $Bouton
$b = $fen.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $b) { "bouton $Bouton introuvable"; exit 1 }
if (-not $b.Current.IsEnabled) { "bouton $Bouton inactif"; exit 1 }
# fenetre au premier plan : la demande d'autorisation de Windows s'ouvre alors devant, au lieu de clignoter dans la barre des taches
try { $fen.SetFocus() } catch { }
$motif = $null
if ($Choix) {
  # les lignes d'une liste deroulante n'existent pour les outils qu'une fois la liste ouverte
  if (-not $b.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$motif)) { "$Bouton n'est pas une liste deroulante"; exit 1 }
  $liste = $motif
  $liste.Expand(); Start-Sleep -Milliseconds 500
  $nom = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Choix
  $ligne = $b.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nom)
  if (-not $ligne -or -not $ligne.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$motif)) { $liste.Collapse(); "ligne $Choix introuvable dans $Bouton"; exit 1 }
  $motif.Select(); $liste.Collapse()
  "$Bouton : $Choix"
  exit 0
}
# un bouton s'actionne, une case a cocher bascule, un bouton radio se choisit, une liste deroulante s'ouvre
if ($b.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$motif)) { $motif.Invoke() }
elseif ($b.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$motif)) { $motif.Toggle() }
elseif ($b.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$motif)) { $motif.Select() }
elseif ($b.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$motif)) { $motif.Expand() }
else { "impossible d'actionner $Bouton"; exit 1 }
"appui sur $Bouton"
