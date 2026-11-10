# Claude-Sync

[Français](#français) · [English](#english)

<a id="français"></a>
## Français

Garde vos **conversations Claude Desktop** (sessions Cowork / Code) et votre **dossier de projets** identiques sur plusieurs PC Windows,
grâce à **votre propre serveur** joint en SSH. Pas de service dans le nuage, pas de compte, rien ne sort de votre réseau.

> **État : bêta (2.0.0-beta.3).** Développé et testé par son auteur (y compris sur liaisons lentes simulées), pas encore largement éprouvé au quotidien. Gardez vos propres sauvegardes.

### Fonctionnement

- Chaque PC fait tourner une petite application dans la zone de notification. À la fermeture de Claude Desktop elle envoie ce qui a changé au serveur ; à son ouverture elle récupère ce qu'a fait l'autre PC.
- Les copies utilisent **rsync par SSH** : seule la *différence* d'un fichier circule, compressée. Un journal de conversation qui grossit de 1 Mo n'envoie qu'environ un quart (mesuré : 274 Ko), pas le fichier entier — utile en 4G lente.
- Le serveur garde une corbeille et des archives 30 jours : rien n'est jamais écrasé ni supprimé sans copie.
- Si les deux PC ont modifié le même fichier, rien n'est perdu : c'est un *conflit* et vous choisissez la version à garder.
- Windows pour les PC. Le serveur peut être n'importe quelle machine avec SSH, `rsync` et Python 3 (NAS Linux, Raspberry Pi, serveur loué…).

### Installation

1. **Serveur** (une fois) : un compte SSH avec `rsync` et `python3`. Créez un compte dédié si possible ; un compte utilisateur ordinaire convient aussi.
2. **Chaque PC** : télécharger `Claude-Sync-Setup.exe` sur la [page des versions](https://github.com/Zal3D/Claude-Sync/releases) et le lancer. Aucun droit administrateur n'est nécessaire (installation dans votre profil).
3. Au premier démarrage, saisissez l'adresse du serveur, votre nom d'utilisateur, le dossier à utiliser sur le serveur et votre **mot de passe — utilisé une seule fois**
   pour installer une clé SSH propre à ce PC, jamais enregistré. Ensuite, c'est la clé qui sert (limitée à Claude-Sync : elle ne peut rien exécuter d'autre sur le serveur).
4. C'est fini. Claude-Sync démarre avec Windows et surveille Claude Desktop.

Windows peut afficher *« Éditeur inconnu »* : l'installeur n'est pas signé avec un certificat de signature payant. Vérifiez l'empreinte SHA-256 indiquée sur la page de la version.

### Sécurité

- Gardez le port SSH derrière un VPN ou un pare-feu ; ne l'exposez pas sur Internet.
- Chaque PC a sa propre clé, limitée par une commande imposée aux seules opérations de Claude-Sync (rsync dans un dossier sans suppression, et le programme serveur de Claude-Sync).
- Le mot de passe ne sert que pendant l'installation, via un petit programme SSH temporaire, et n'est jamais écrit sur le disque.
- Mises à jour : l'application peut interroger les versions publiées sur GitHub (réglage `mises_a_jour` = `propriétaire/dépôt` dans `config.json`), vérifie l'empreinte SHA-256 publiée et **n'installe que sur votre clic**.
  L'empreinte prouve que le fichier est intact, pas qui l'a publié : la confiance repose sur le propriétaire du dépôt.

### Compiler depuis les sources

Windows et PowerShell ; le compilateur (Roslyn) est fourni dans `tools/roslyn`. Python 3 sert aux tests.

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-v2.ps1            # compile et lance les tests
# résultat : dist\v2app\ClaudeSync2.exe  (un seul fichier, outils inclus)
```

### Licence

MIT (voir `LICENSE`). L'installeur embarque rsync, OpenSSH et leurs bibliothèques de MSYS2, non modifiés, chacun sous sa licence :
voir [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). « Claude » est une marque d'Anthropic ; ce projet est indépendant, non affilié à Anthropic et non cautionné par elle.

---

<a id="english"></a>
## English

Keep your **Claude Desktop conversations** (Cowork / Code sessions) and your **project folder** identical on several Windows PCs,
through **your own server** reached over SSH. No cloud service, no account, nothing leaves your network.

> **Status: beta (2.0.0-beta.2).** Developed and tested by its author (including simulated slow links), not yet widely tested in daily use. Keep your own backups.

### How it works

- Each PC runs a small tray application. When Claude Desktop closes, it sends what changed to the server; when Claude opens, it fetches what the other PC did.
- Copies use **rsync over SSH**: only the *difference* of a file travels, compressed. A conversation log that grows by 1 MB sends roughly a quarter of that (measured: 274 KB), not the whole file — useful on a slow 4G link.
- The server keeps a trash and an archive for 30 days: nothing is ever overwritten or deleted without a copy.
- If both PCs changed the same file, nothing is lost: it shows up as a *conflict* and you choose which version to keep.
- Windows only (the PCs). The server can be any machine with SSH, `rsync` and Python 3 (a Linux NAS, a Raspberry Pi, a VPS…).

### Install

1. **Server** (once): an SSH account with `rsync` and `python3` installed. Create a dedicated account if you can; a normal user account works too.
2. **Each PC**: download `Claude-Sync-Setup.exe` from the [releases page](https://github.com/Zal3D/Claude-Sync/releases) and run it. No administrator rights are needed (it installs into your user profile).
3. At first start, enter the server address, your user name, the folder to use on the server and your **password — it is used once** to
   install a dedicated SSH key for this PC and is never stored. After that the key (restricted to Claude-Sync only: it can run nothing else on the server) is used.
4. Done. Claude-Sync starts with Windows and watches Claude Desktop.

Windows may show *"Unknown publisher"*: the installer is not signed with a paid code-signing certificate. Check the SHA-256 shown on the release page.

### Security notes

- Keep the SSH port behind a VPN or a firewall; do not expose it to the Internet.
- Each PC gets its own key, restricted by a forced command to Claude-Sync's own operations (rsync into one folder without deletion, and the Claude-Sync server program).
- The password is only used during setup, through a temporary SSH askpass helper, and is never written to disk.
- Updates: the app can check GitHub Releases (setting `mises_a_jour` = `owner/repo` in `config.json`), verifies the published SHA-256, and **only installs when you click**.
  The checksum proves the file is intact, not who published it: trust rests on the repository's owner.

### Build from source

Requires Windows and PowerShell; the compiler (Roslyn) is vendored in `tools/roslyn`. Python 3 is needed for the tests.

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-v2.ps1            # builds and runs the tests
# result: dist\v2app\ClaudeSync2.exe  (single file, includes the tools)
```

### License

MIT (see `LICENSE`). The installer embeds unmodified rsync, OpenSSH and their libraries from MSYS2, each under its own license:
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). "Claude" is a trademark of Anthropic; this project is independent and not affiliated with or endorsed by Anthropic.
