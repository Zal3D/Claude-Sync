<#
  Essai complet de Claude-Sync V2 en BAC A SABLE : rien de ce qui existe sur ce PC n'est touche.
  - le programme est installe dans un dossier d'essai (installeur --sandbox : pas de vrais raccourcis, pas de vraie entree Windows) ;
  - deux PC fictifs (A et B) avec leur faux profil Windows, leur dossier de projets et leur dossier d'etat, dans le dossier d'essai ;
  - un dossier de synchro d'essai sur un serveur d'essai (jamais le hub de production) ; la surveillance de Claude Desktop n'est PAS lancee.
  Scenario : inscription des deux PC, propagation, modification, conflit, suppression/corbeille/restauration, delta, console, application residente.
  Usage : powershell -ExecutionPolicy Bypass -File tools\essai-bac-a-sable.ps1 -HostName serveur -User compte -HubDir /chemin/hub-essai -AdminKey cle_admin [-Port 22] [-Exe ClaudeSync2.exe] [-Garder]
  -AdminKey : cle privee d'un administrateur du serveur D'ESSAI (amorcage sans mot de passe). Ne jamais l'enregistrer dans le depot.
#>
param(
  [Parameter(Mandatory)][string]$HostName,
  [Parameter(Mandatory)][string]$User,
  [Parameter(Mandatory)][string]$HubDir,
  [Parameter(Mandatory)][string]$AdminKey,
  [int]$Port = 22,
  [string]$Exe = '',
  [switch]$Garder
)
$ErrorActionPreference = 'Stop'
# Garde-fou : le dossier d'essai du serveur doit etre un chemin absolu Linux assez profond (depuis Git Bash, /chemin est deforme en C:/Program Files/Git/... : utiliser PowerShell ou MSYS_NO_PATHCONV=1).
if ($HubDir -notmatch '^/[^/]+/[^/]+/[^/]+') { throw "HubDir doit etre un chemin absolu Linux d'au moins 3 niveaux (recu : $HubDir)" }
$racine = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) { $Exe = Join-Path $racine 'dist\v2app\ClaudeSync2.exe' }
$Exe = (Resolve-Path $Exe).Path
$S = Join-Path ([IO.Path]::GetTempPath()) ('cs-bac-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $S | Out-Null
$script:ok = 0; $script:ko = 0; $script:echecs = @()
function Verif([string]$nom, [bool]$cond, [string]$detail = '') {
  if ($cond) { $script:ok++; "  ok     $nom" } else { $script:ko++; $script:echecs += $nom; "  ECHEC  $nom  $detail" }
}
function Titre([string]$t) { ''; $t }

# Lance un programme (graphique ou console : on attend sa fin et on recupere sa sortie) avec, si demande, le faux profil d'un PC d'essai.
function Exec([string]$fichier, [string[]]$args2, [string]$profil = '') {
  $q = ($args2 | ForEach-Object { $a = [string]$_; if ($a -match '[\s"]') { '"' + $a.Replace('"', [string][char]92 + '"') + '"' } else { $a } }) -join ' '
  $psi = New-Object Diagnostics.ProcessStartInfo($fichier, $q)
  $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
  if ($profil) { $psi.EnvironmentVariables['USERPROFILE'] = $profil }
  $p = [Diagnostics.Process]::Start($psi)
  $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
  $p.WaitForExit()
  return [pscustomobject]@{ Code = $p.ExitCode; Out = ($so.Result + $se.Result) }
}
function Cs([string]$pc, [string[]]$args2) { Exec $inst $args2 (Join-Path $S "$pc\profil") }
function Etat([string]$pc) { Join-Path $S "$pc\etat" }
function Proj([string]$pc) { Join-Path $S "$pc\Projets" }
function Prof([string]$pc) { Join-Path $S "$pc\profil" }
function Ecrire([string]$chemin, [string]$texte, [int]$minutesPassees = 10) {
  New-Item -ItemType Directory -Force (Split-Path $chemin) | Out-Null
  [IO.File]::WriteAllText($chemin, $texte, (New-Object Text.UTF8Encoding($false)))
  (Get-Item -LiteralPath $chemin).LastWriteTime = (Get-Date).AddMinutes(-$minutesPassees)
}
function Empreintes([string]$dossier) {
  $h = @{}
  if (Test-Path $dossier) { Get-ChildItem -LiteralPath $dossier -Recurse -File | ForEach-Object { $h[$_.FullName.Substring($dossier.Length)] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }
  return $h
}
function Pareil([string]$a, [string]$b) {
  $x = Empreintes $a; $y = Empreintes $b
  if ($x.Count -ne $y.Count) { return $false }
  foreach ($k in $x.Keys) { if (-not $y.ContainsKey($k) -or $y[$k] -ne $x[$k]) { return $false } }
  return $true
}

"Bac a sable : $S"
Titre '1. Installation dans le bac a sable (installeur --sandbox)'
$ri = Exec $Exe @('install', '--silent', '--lang', 'fr', '--sandbox', (Join-Path $S 'installation'))
$inst = Join-Path $S 'installation\programme\ClaudeSync2.exe'
Verif 'installation reussie' ((Test-Path $inst) -and $ri.Code -eq 0) $ri.Out
Verif 'rsync, ssh et serveur installes a cote du programme' ((Test-Path (Join-Path (Split-Path $inst) 'outils\win-x64\rsync.exe')) -and (Test-Path (Join-Path (Split-Path $inst) 'serveur\claudesync-serveur')))

try {
  Titre '2. Inscription de deux PC fictifs (cle d''administrateur d''essai, empreinte du serveur acceptee)'
  foreach ($pc in 'A', 'B') {
    New-Item -ItemType Directory -Force (Proj $pc), (Prof $pc) | Out-Null
    $r = Cs $pc @('enroll', '--host', $HostName, '--user', $User, '--port', $Port, '--folder', $HubDir, '--pc', "PC-BAC-$pc", '--projets', (Proj $pc), '--state', (Etat $pc), '--admin-key', $AdminKey, '--yes')
    Verif "PC $pc : inscription reussie" ($r.Code -eq 0 -and (Test-Path (Join-Path (Etat $pc) 'config.json'))) $r.Out
    if ($pc -eq 'A') { $cle = (Get-Content (Join-Path (Etat 'A') 'config.json') -Raw | ConvertFrom-Json).cle }
  }
  # hub d'essai vide (donnees d'un essai precedent) : apres l'inscription, quand la cle du serveur est memorisee ; seulement si c'est bien un hub Claude-Sync
  $sshx = Join-Path (Split-Path $inst) 'outils\win-x64\ssh.exe'; $khx = (Join-Path (Etat 'A') 'ssh\known_hosts').Replace([string][char]92, '/')
  Exec $sshx @('-i', $AdminKey.Replace([string][char]92, '/'), '-p', $Port, '-o', "UserKnownHostsFile=$khx", '-o', 'BatchMode=yes', "root@$HostName", "cd '$HubDir' && test -f _etat/hub.id && rm -rf projets _corbeille _anciennes claude desktop .rsync-partial && mkdir -p .rsync-tmp && rm -rf .rsync-tmp/*; true") | Out-Null
  $r = Cs 'A' @('ping', '--state', (Etat 'A'))
  Verif 'ping : liaison mesuree' ($r.Code -eq 0 -and $r.Out -match 'Liaison') $r.Out

  Titre '3. Donnees d''essai sur le PC A, simulation puis envoi'
  1..40 | ForEach-Object { Ecrire (Join-Path (Proj 'A') ("appli\dossier$($_ % 5)\fichier $_ é.txt")) ("contenu $_ " * 30) }
  Ecrire (Join-Path (Proj 'A') 'appli\main.py') "print('bonjour')`n"
  $gros = (1..60000 | ForEach-Object { '{"type":"assistant","text":"mot' + ($_ % 97) + ' mot' + ($_ % 31) + ' ligne ' + $_ + '"}' }) -join "`n"
  Ecrire (Join-Path (Prof 'A') '.claude\projects\proj1\conv1.jsonl') $gros
  Ecrire (Join-Path (Prof 'A') '.claude\file-history\abc\v1.txt') "ancienne version`n"
  $r = Cs 'A' @('sync', '--state', (Etat 'A'))
  Verif 'simulation : annoncee, rien n''est ecrit' ($r.Code -eq 0 -and $r.Out -match 'SIMULATION') $r.Out
  $r = Cs 'A' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'A'))
  Verif 'A envoie tout (code 0)' ($r.Code -eq 0 -and $r.Out -match 'Termin') $r.Out

  Titre '4. Le PC B recupere : contenus identiques'
  $r = Cs 'B' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'B'))
  Verif 'B recupere (code 0)' ($r.Code -eq 0) $r.Out
  Verif 'projets identiques (SHA-256, accents et espaces compris)' (Pareil (Proj 'A') (Proj 'B'))
  Verif 'conversations identiques (.claude\projects)' (Pareil (Join-Path (Prof 'A') '.claude\projects') (Join-Path (Prof 'B') '.claude\projects'))
  Verif 'historique des fichiers identique (.claude\file-history)' (Pareil (Join-Path (Prof 'A') '.claude\file-history') (Join-Path (Prof 'B') '.claude\file-history'))

  Titre '5. Modification sur B (le journal grossit, un fichier change), retour sur A'
  Add-Content -LiteralPath (Join-Path (Prof 'B') '.claude\projects\proj1\conv1.jsonl') -Value ((1..3000 | ForEach-Object { '{"type":"user","text":"suite ' + $_ + '"}' }) -join "`n") -Encoding UTF8
  (Get-Item (Join-Path (Prof 'B') '.claude\projects\proj1\conv1.jsonl')).LastWriteTime = (Get-Date).AddMinutes(-8)
  Ecrire (Join-Path (Proj 'B') 'appli\main.py') "print('bonjour, version B')`n" 8
  $r = Cs 'B' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'B')); Verif 'B envoie ses modifications' ($r.Code -eq 0) $r.Out
  $r = Cs 'A' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'A')); Verif 'A les recupere' ($r.Code -eq 0) $r.Out
  Verif 'A = B apres modification' ((Pareil (Proj 'A') (Proj 'B')) -and (Pareil (Join-Path (Prof 'A') '.claude\projects') (Join-Path (Prof 'B') '.claude\projects')))

  Titre '6. Conflit : le meme fichier modifie sur les deux PC'
  Ecrire (Join-Path (Proj 'A') 'appli\main.py') "print('version A du conflit, plus longue')`n" 6
  Ecrire (Join-Path (Proj 'B') 'appli\main.py') "print('version B')`n" 5
  $r = Cs 'A' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'A')); Verif 'A envoie sa version' ($r.Code -eq 0) $r.Out
  $r = Cs 'B' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'B'))
  Verif 'B : conflit detecte (code 1), rien d''ecrase' ($r.Code -eq 1) $r.Out
  Verif 'B garde sa version intacte' ((Get-Content (Join-Path (Proj 'B') 'appli\main.py') -Raw) -match 'version B')
  $r = Cs 'B' @('conflicts', '--state', (Etat 'B')); Verif 'le conflit est liste' ($r.Out -match 'main.py') $r.Out
  $r = Cs 'B' @('resolve', '1', 'hub', '--claude', 'no', '--state', (Etat 'B')); Verif 'resolution : garder la version du serveur' ($r.Code -eq 0 -and $r.Out -match 'Conflit resolu') $r.Out
  Verif 'B a maintenant la version A' ((Get-Content (Join-Path (Proj 'B') 'appli\main.py') -Raw) -match 'version A du conflit')
  $r = Cs 'B' @('conflicts', '--state', (Etat 'B')); Verif 'plus aucun conflit' ($r.Out -match 'Aucun conflit') $r.Out

  Titre '7. Suppression : corbeille du serveur, propagation, restauration'
  Remove-Item -LiteralPath (Join-Path (Proj 'A') 'appli\dossier1\fichier 6 é.txt')
  $r = Cs 'A' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'A')); Verif 'A signale la suppression' ($r.Code -eq 0) $r.Out
  $r = Cs 'B' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'B')); Verif 'B la recoit' ($r.Code -eq 0) $r.Out
  Verif 'le fichier a disparu chez B aussi' (-not (Test-Path -LiteralPath (Join-Path (Proj 'B') 'appli\dossier1\fichier 6 é.txt')))
  $r = Cs 'B' @('trash', '--state', (Etat 'B')); Verif 'corbeille : la suppression est listee (par PC-BAC-A)' ($r.Out -match 'PC-BAC-A') $r.Out
  $r = Cs 'B' @('restore', '1', '--claude', 'no', '--state', (Etat 'B')); Verif 'restauration demandee' ($r.Code -eq 0) $r.Out
  $r = Cs 'B' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'B')); $r2 = Cs 'A' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'A'))
  Verif 'le fichier est revenu sur les deux PC' ((Test-Path -LiteralPath (Join-Path (Proj 'A') 'appli\dossier1\fichier 6 é.txt')) -and (Test-Path -LiteralPath (Join-Path (Proj 'B') 'appli\dossier1\fichier 6 é.txt')))
  Verif 'A = B en fin de scenario' ((Pareil (Proj 'A') (Proj 'B')) -and (Pareil (Join-Path (Prof 'A') '.claude') (Join-Path (Prof 'B') '.claude')))

  Titre '8. Stabilite et application residente'
  $r = Cs 'A' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'A')); $r2 = Cs 'B' @('sync', '--apply', '--claude', 'no', '--state', (Etat 'B'))
  Verif 'synchro sans changement : rien a transferer des deux cotes' ($r.Code -eq 0 -and $r2.Code -eq 0 -and $r.Out -match '(?i)aucun|0 fichier|a jour|Termin') ($r.Out + $r2.Out)
  $r = Cs 'B' @('run', '--no-watch', '--exit-after', '4', '--state', (Etat 'B'))
  Verif 'application residente (sans surveillance de Claude) : demarre et s''arrete seule' ($r.Code -eq 0 -and $r.Out -notmatch 'xception') $r.Out
  $r = Cs 'B' @('update', '--state', (Etat 'B')); Verif 'recherche de mise a jour : reponse claire' ($r.Code -eq 0 -or $r.Code -eq 3) $r.Out
}
finally {
  Titre '9. Nettoyage'
  if (-not $Garder) {
    try {
      $ssh = Join-Path (Split-Path $inst) 'outils\win-x64\ssh.exe'
      if ((Test-Path $ssh) -and (Test-Path (Join-Path (Etat 'A') 'ssh\known_hosts'))) {
        $kh = (Join-Path (Etat 'A') 'ssh\known_hosts').Replace([string][char]92, '/')
        Exec $ssh @('-i', $AdminKey.Replace([string][char]92, '/'), '-p', $Port, '-o', "UserKnownHostsFile=$kh", '-o', 'BatchMode=yes', "root@$HostName", "test -f '$HubDir/_etat/hub.id' && rm -rf -- '$HubDir'") | Out-Null
        "  dossier d'essai du serveur supprime : $HubDir"
      }
      Exec $inst @('uninstall', '--silent', '--sandbox', (Join-Path $S 'installation')) | Out-Null
      Start-Sleep -Seconds 5
      Remove-Item -Recurse -Force $S -ErrorAction SilentlyContinue
      "  bac a sable supprime"
    } catch { "  nettoyage incomplet : $($_.Exception.Message) ($S)" }
  } else { "  conserve : $S" }
}
''
"Resultat : $script:ok verifications reussies, $script:ko echec(s)"
$script:echecs | ForEach-Object { "  - $_" }
exit $(if ($script:ko -eq 0) { 0 } else { 1 })
