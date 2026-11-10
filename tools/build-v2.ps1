<#
  Compile et lance les tests de la V2 (jalon J1 : client du serveur). Aucun outil a installer sauf Python 3 pour les tests.
  Produit dist\v2tests\V2Tests.exe (outil de test, jamais distribue).
  Usage : powershell -ExecutionPolicy Bypass -File tools\build-v2.ps1 [-Python chemin\python.exe] [-SansLancer]
#>
param([string]$Python = '', [string]$Outils = '', [string]$Enrol = '', [string]$RootKey = '', [switch]$SansLancer, [switch]$Lente)
# -RootKey : cle d'administrateur d'essai du serveur (inscription reelle de deux PC d'essai + synchro complete par SSH ; avec -Enrol)
# -Outils : dossier contenant rsync.exe et ssh.exe (par defaut v2\outils\win-x64 s'il existe)
# -Enrol  : dossier d'une inscription d'essai (config.json, cle, known_hosts) : active les tests par SSH vers le serveur d'essai
$ErrorActionPreference = 'Stop'
$racine = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Compilateur moderne portable (Roslyn, tools\roslyn) : C# recent vers .NET Framework 4.8 ; repli sur celui de Windows (C# 5) s'il manque.
$csc = Join-Path $PSScriptRoot 'roslyn\csc.exe'
if (-not (Test-Path $csc)) { & (Join-Path $PSScriptRoot 'get-roslyn.ps1') }      # telecharge (une fois) le compilateur, empreinte verifiee
if (-not (Test-Path $csc)) { throw "Compilateur introuvable : $csc (lancez tools\get-roslyn.ps1)" }
$out = Join-Path $racine 'dist\v2tests'
New-Item -ItemType Directory -Force $out | Out-Null
# Plus aucun code V1 : les briques communes (FileEntry, Theme, IconArt...) sont dans v2\Shared ; /main choisit le point d'entree des tests.
$v1 = @()
$v2 = @(Get-ChildItem (Join-Path $racine 'v2') -Filter *.cs -File) + @(Get-ChildItem (Join-Path $racine 'v2\Shared') -Filter *.cs -File) + @(Get-ChildItem (Join-Path $racine 'v2\ui') -Filter *.cs -File) | ForEach-Object { $_.FullName }
$v2t = Get-ChildItem (Join-Path $racine 'v2\tests') -Filter *.cs -File | ForEach-Object { $_.FullName }
$args2 = @('/nologo', '/optimize+', '/debug-', '/codepage:65001', '/warn:3', '/langversion:latest', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.IO.Compression.dll',
           '/main:ClaudeSync.V2Tests.TestProgram', '/target:exe', "/out:$out\V2Tests.exe") + $v1 + $v2 + $v2t
$sortie = & $csc @args2 2>&1 | Where-Object { $_ -notmatch 'CS0649' }
if ($LASTEXITCODE -ne 0) { $sortie; throw 'La compilation a echoue.' }
$sortie | Where-Object { $_ -match 'warning' }
"Compile : $out\V2Tests.exe"
# Application ClaudeSync2.exe : un petit exe graphique SANS fichier embarque (un exe qui en cache d'autres ressemble a un "dropper" : Windows Defender l'a
# classe Trojan:Win32/Bearfoos.A!ml, comme avec la V1). Les outils (rsync, ssh) et le programme serveur sont LIVRES A COTE, dans le meme dossier (zip).
$app = Join-Path $racine 'dist\v2app'
New-Item -ItemType Directory -Force $app | Out-Null
$v2a = Get-ChildItem (Join-Path $racine 'v2\app') -Filter *.cs -File | ForEach-Object { $_.FullName }
$commA = @('/nologo', '/optimize+', '/debug-', '/codepage:65001', '/warn:3', '/langversion:latest', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.IO.Compression.dll', '/main:ClaudeSync.V2.Cs2')
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$pay = Join-Path $racine 'dist\v2payload'
if (Test-Path $pay) { Remove-Item -Recurse -Force $pay }
New-Item -ItemType Directory -Force $pay | Out-Null
# 1re passe sans icone pour la fabriquer, 2e passe definitive (icone + manifeste + informations de version, sans console)
$ico = Join-Path $pay 'ClaudeSync2.ico'
$tmpExe = Join-Path $pay 'icone.exe'
$s1 = & $csc @($commA + @('/target:exe', "/out:$tmpExe") + $v1 + $v2 + $v2a) 2>&1 | Where-Object { $_ -notmatch 'CS0649' }
if ($LASTEXITCODE -ne 0) { $s1; throw 'La compilation de ClaudeSync2.exe (passe 1) a echoue.' }
& $tmpExe icon $ico | Out-Null
$argsA = @($commA + @("/win32icon:$ico", "/win32manifest:$(Join-Path $racine 'app.manifest')", '/target:winexe', "/out:$app\ClaudeSync2.exe") + $v1 + $v2 + $v2a)
$sortieA = & $csc @argsA 2>&1 | Where-Object { $_ -notmatch 'CS0649' }
if ($LASTEXITCODE -ne 0) { $sortieA; throw 'La compilation de ClaudeSync2.exe a echoue.' }
$sortieA | Where-Object { $_ -match 'warning' }
New-Item -ItemType Directory -Force "$app\serveur", "$app\outils\win-x64" | Out-Null
Copy-Item (Join-Path $racine 'v2\serveur\*') "$app\serveur" -Force
Copy-Item (Join-Path $racine 'v2\outils\win-x64\*') "$app\outils\win-x64" -Force
"Compile : $app\ClaudeSync2.exe  ($([math]::Round((Get-Item "$app\ClaudeSync2.exe").Length / 1KB)) Ko, sans fichier embarque ; outils et serveur a cote)"
# Livraison : dist\release\Claude-Sync-win-x64.zip = ClaudeSync2.exe + outils\ + serveur\ + LISEZ-MOI.txt (entrees avec des "/", empreinte SHA-256 a cote)
$rel = Join-Path $racine 'dist\release'
if (Test-Path $rel) { Remove-Item -Recurse -Force $rel }
New-Item -ItemType Directory -Force $rel | Out-Null
$zipPath = Join-Path $rel 'Claude-Sync-win-x64.zip'
$lisezmoi = "Claude-Sync $((Select-String -Path (Join-Path $racine 'v2\ui\AppModel.cs') -Pattern 'Version = \"([^\"]+)\"').Matches[0].Groups[1].Value)`r`n`r`nFR : double-cliquez sur ClaudeSync2.exe pour installer (sans droits administrateur). Gardez ce dossier tel quel : outils\ et serveur\ doivent rester a cote de ClaudeSync2.exe.`r`nEN: double-click ClaudeSync2.exe to install (no administrator rights). Keep this folder as is: outils\ and serveur\ must stay next to ClaudeSync2.exe.`r`n`r`nhttps://github.com/Zal3D/Claude-Sync`r`nLicense: MIT; third-party notices: outils\licences\THIRD-PARTY-NOTICES.md`r`n"
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
  [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, "$app\ClaudeSync2.exe", 'ClaudeSync2.exe', [System.IO.Compression.CompressionLevel]::Optimal)
  $e = $zip.CreateEntry('LISEZ-MOI.txt'); $w = New-Object IO.StreamWriter($e.Open(), (New-Object Text.UTF8Encoding($false))); $w.Write($lisezmoi); $w.Dispose()
  foreach ($d in 'serveur', 'outils') {
    $base = Join-Path $racine "v2\$d"
    foreach ($f in Get-ChildItem $base -Recurse -File) {
      $nom = $d + '/' + $f.FullName.Substring($base.Length + 1).Replace([string][char]92, '/')
      [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $nom, [System.IO.Compression.CompressionLevel]::Optimal)
    }
  }
} finally { $zip.Dispose() }
$h = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$h  Claude-Sync-win-x64.zip" | Set-Content (Join-Path $rel 'Claude-Sync-win-x64.zip.sha256') -Encoding ASCII
"Livraison : $zipPath  ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) Mo)  SHA-256 $h"
if ($SansLancer) { return }
$a = @('--serveur', (Join-Path $racine 'v2\serveur\claudesync-serveur'))
if ($Python) { $a += @('--python', $Python) }
if (-not $Outils) { $def = Join-Path $racine 'v2\outils\win-x64'; if (Test-Path (Join-Path $def 'rsync.exe')) { $Outils = $def } }
if ($Outils) { $a += @('--outils', $Outils) }
if ($Enrol) { $a += @('--enrol', $Enrol) }
if ($RootKey) { $a += @('--root-key', $RootKey) }
if ($Lente) { $a += '--lente' }   # essais de liaison lente seulement (J5)
& "$out\V2Tests.exe" @a
exit $LASTEXITCODE
