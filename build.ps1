# Fabrique l'appli portable : un seul fichier, dist\Autoexec.exe, sans installation (.NET est dedans).
# C'est ce fichier, sous ce nom exact, qu'on joint a la publication GitHub : la mise a jour automatique le cherche (MiseAJour.cs).
$ErrorActionPreference = 'Stop'
$ici = $PSScriptRoot
dotnet publish "$ici\src\Autoexec.csproj" -c Release -r win-x64 --self-contained true -o "$ici\publish" --nologo -v q `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { 'la fabrication a echoue'; exit 1 }
New-Item -ItemType Directory -Force "$ici\dist" | Out-Null
Copy-Item "$ici\publish\Autoexec.exe" "$ici\dist\Autoexec.exe" -Force
$exe = Get-Item "$ici\dist\Autoexec.exe"
'{0} : version {1}, {2:N1} Mo' -f $exe.FullName, $exe.VersionInfo.ProductVersion, ($exe.Length / 1MB)
