# Lit (sans rien ecrire) le premier secteur d'un disque et celui de sa premiere partition : code de demarrage present ou non.
param([int]$Disque = 2)
$f = [IO.File]::Open("\\.\PhysicalDrive$Disque", 'Open', 'Read', 'ReadWrite')
try {
  $b = New-Object byte[] 512
  [void]$f.Read($b, 0, 512)
  $code = ($b[0..439] | Where-Object { $_ -ne 0 }).Count
  $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b, 0, 440)).Replace('-', '').Substring(0, 16)
  "secteur 0 : $code octets non nuls dans le code de demarrage (empreinte $sha), signature du disque $([BitConverter]::ToString($b[440..443])), fin $([BitConverter]::ToString($b[510..511]))"
  for ($i = 0; $i -lt 4; $i++) {
    $e = $b[(446 + 16 * $i)..(461 + 16 * $i)]
    if (($e | Where-Object { $_ -ne 0 }).Count) { "  partition $($i + 1) : drapeau $('{0:X2}' -f $e[0]), type $('{0:X2}' -f $e[4]), debut secteur $([BitConverter]::ToUInt32($e, 8)), $([BitConverter]::ToUInt32($e, 12)) secteurs" }
  }
  $debut = [BitConverter]::ToUInt32($b, 454)
  if ($debut -gt 0 -and $b[450] -ne 0xEE) {
    [void]$f.Seek([long]$debut * 512, 'Begin'); $p = New-Object byte[] 512; [void]$f.Read($p, 0, 512)
    $shap = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($p, 90, 420)).Replace('-', '').Substring(0, 16)
    "secteur de la partition : saut $([BitConverter]::ToString($p[0..2])), nom [$([Text.Encoding]::ASCII.GetString($p, 3, 8))], systeme [$([Text.Encoding]::ASCII.GetString($p, 82, 8))], code de demarrage empreinte $shap, fin $([BitConverter]::ToString($p[510..511]))"
  }
}
finally { $f.Close() }
