<#
  Telecharge le compilateur C# moderne portable (Roslyn, Microsoft, licence MIT) dans tools\roslyn, une seule fois.
  Source et empreinte SHA-256 du paquet : tools\roslyn\roslyn.json (l'empreinte est verifiee avant toute extraction).
  Appele automatiquement par tools\build-v2.ps1 quand tools\roslyn\csc.exe manque. Aucune installation, aucun droit administrateur.
#>
$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot 'roslyn'
$info = Get-Content (Join-Path $dest 'roslyn.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (Test-Path (Join-Path $dest 'csc.exe')) { "Roslyn deja present : $dest"; return }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('roslyn-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
  $pkg = Join-Path $tmp 'roslyn.nupkg'
  "Telechargement de $($info.paquet) $($info.version) depuis nuget.org ..."
  Invoke-WebRequest -Uri $info.source -OutFile $pkg -UseBasicParsing
  $h = (Get-FileHash $pkg -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($h -ne $info.sha256.ToLowerInvariant()) { throw "Empreinte SHA-256 incorrecte ($h) : paquet refuse." }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [IO.Compression.ZipFile]::OpenRead($pkg)
  try {
    $prefix = $info.dossier_origine.TrimEnd('/') + '/'
    foreach ($e in $zip.Entries) {
      if ($e.FullName.StartsWith($prefix) -and $e.Name.Length -gt 0) {
        $rel = $e.FullName.Substring($prefix.Length)
        if ($rel.Contains('..')) { throw "Chemin refuse : $($e.FullName)" }
        $target = Join-Path $dest ($rel.Replace('/', [string][char]92))
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $target, $true)
      }
    }
  } finally { $zip.Dispose() }
  "Roslyn installe dans $dest"
} finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
