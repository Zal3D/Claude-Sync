# Le programme serveur de Claude-Sync / Claude-Sync server program

[Français](#français) · [English](#english)

<a id="français"></a>
## Français

Claude-Sync met un **petit programme sur votre serveur** : un script Python (`claudesync-serveur`) et un aiguilleur shell (`claudesync-entree`). Le code est public : [`v2/serveur/`](../v2/serveur/). Cette page explique ce qu'il fait, comment il s'installe, ce qu'il peut (et ne peut pas) faire, et comment le retirer.

### Ce qu'il fait, et ce qu'il ne fait pas

- **Ce n'est pas un service** : aucun port supplémentaire, rien ne tourne en permanence. Le programme ne démarre que lorsqu'un PC se connecte en SSH, et s'arrête avec la connexion.
- Il répond à des **requêtes JSON, une par ligne** : lister les fichiers d'un jeu de dossiers, donner une empreinte ou une date, prendre/rendre le **verrou** (un seul PC synchronise à la fois), noter la **présence** d'un PC, mettre à la **corbeille** ou en **archive**, restaurer, purger (sur demande, après 30 jours). Les données elles-mêmes voyagent par **rsync**.
- Il **ne lance jamais de commande** issue des requêtes, refuse les chemins absolus et les `..`, n'utilise que la bibliothèque standard de Python.
- **Rien n'est supprimé définitivement** avant 30 jours (corbeille `_corbeille/`, versions remplacées `_anciennes/`).

### Prérequis du serveur

- Linux (testé sur Debian / OpenMediaVault) avec **OpenSSH** (`authorized_keys`, option `restrict` : OpenSSH 7.2 ou plus récent).
- **Python 3.6 ou plus**, **rsync** et son utilitaire **`rrsync`** (fourni avec rsync ; Debian/Ubuntu : paquet `rsync`).
- Un compte SSH qui peut **écrire** dans le dossier de synchro (créez un compte dédié si vous le pouvez).
- Synology / QNAP : non vérifiés ; Python 3 et rsync y sont installables, `rrsync` peut manquer.

### Installation : automatique

**Vous n'avez rien à installer à la main** : c'est l'inscription d'un PC (fenêtre « Connexion au serveur » ou `ClaudeSync2.exe enroll`) qui le fait, en 6 étapes :

1. Empreinte du serveur : affichée, à confirmer (jamais de confiance aveugle).
2. Contrôle du serveur : présence de `rsync`, `python3` (≥ 3.6), `rrsync` ; message clair sinon.
3. Dépôt du programme : `~/.claudesync/claudesync-serveur` et `claudesync-entree` (droits 700).
4. Dossier de données : création du dossier de synchro et de `_etat/hub.id` (identité du dossier, créée une seule fois).
5. Clé propre à ce PC : générée sur le PC, **clé publique** ajoutée à `~/.ssh/authorized_keys` avec une commande imposée (voir ci-dessous). Le mot de passe, utilisé une seule fois, n'est jamais enregistré.
6. Vérification : connexion par la clé, puis `hello` au programme.

Une nouvelle inscription (ou `enroll`) **redéploie** la version du programme serveur fournie avec le PC.

### Ce que la clé peut faire

Chaque PC a sa propre clé, ajoutée à `authorized_keys` sous la forme :

```
restrict,command="CS_ROOT=<dossier de synchro> <home>/.claudesync/claudesync-entree" ssh-ed25519 AAAA... claude-sync:<NOM-DU-PC>
```

`restrict` coupe tout (terminal, redirections de ports, agent…). La commande imposée lance **uniquement** `claudesync-entree`, qui n'accepte que :

- `rsync --server …` → exécuté par `rrsync -no-del <dossier de synchro>` : **limité à ce dossier, sans suppression** ;
- `claudesync-serveur` → le programme Python.

Toute autre commande est refusée.

### Disposition du dossier de synchro

```
<dossier de synchro>/
  projets/                      votre dossier de projets
  claude/projects/              conversations de Claude (Code)
  claude/reglages/  claude/file-history/
  desktop/sessions/             liste des sessions de Claude Desktop
  _corbeille/   _anciennes/     suppressions et versions remplacées (30 jours)
  _etat/                        hub.id, présence des PC
  .rsync-tmp/                   fichiers temporaires de rsync
```

### Requêtes comprises (protocole 1)

`hello`, `ping`, `list`, `stat`, `hash`, `touch`, `lock.acquire`, `lock.release`, `presence.write`, `presence.read`, `trash`, `archive`, `trash.list`, `trash.restore`, `purge`, `bye`. Une requête = une ligne JSON ; la réponse est une ligne JSON, suivie si besoin d'octets bruts (listes compressées). Le verrou est lié à la connexion : libéré tout de suite si le PC s'éteint, ou après 180 s de silence.

### Installation manuelle (administrateur)

Si vous préférez ne pas laisser l'application écrire sur le serveur, ou pour un compte restreint :

```sh
# en tant que l'utilisateur qui recevra la synchro
mkdir -p ~/.claudesync && chmod 700 ~/.claudesync
cp claudesync-serveur claudesync-entree ~/.claudesync/ && chmod 700 ~/.claudesync/*
mkdir -p /chemin/du/hub/_etat
# une ligne par PC (la clé publique vient du PC) :
echo 'restrict,command="CS_ROOT=/chemin/du/hub /home/UTILISATEUR/.claudesync/claudesync-entree" ssh-ed25519 AAAA... claude-sync:NOM-DU-PC' >> ~/.ssh/authorized_keys
```

L'utilisateur doit pouvoir écrire dans `/chemin/du/hub` (droits Unix ou ACL).

### Mise à jour du programme serveur

Pour l'instant, le programme serveur est mis à jour **par une nouvelle inscription** du PC (qui redéploie les fichiers), ou en recopiant les deux fichiers de `v2/serveur/` dans `~/.claudesync/`. La version installée : `grep '^VERSION' ~/.claudesync/claudesync-serveur`. Le client et le serveur vérifient leur numéro de protocole (`proto`) à la connexion.

### Désinstallation côté serveur

1. Supprimez de `~/.ssh/authorized_keys` les lignes qui se terminent par `claude-sync:<NOM-DU-PC>`.
2. Supprimez `~/.claudesync/`.
3. Vos **données** (le dossier de synchro) ne sont jamais supprimées par Claude-Sync : à vous de décider de les garder ou non.

---

<a id="english"></a>
## English

Claude-Sync puts a **small program on your server**: a Python script (`claudesync-serveur`) and a shell dispatcher (`claudesync-entree`). The code is public: [`v2/serveur/`](../v2/serveur/). This page explains what it does, how it gets installed, what it can (and cannot) do, and how to remove it.

### What it does, and what it does not

- **It is not a service**: no extra port, nothing runs permanently. It starts only when a PC connects over SSH and ends with the connection.
- It answers **JSON requests, one per line**: list the files of a set, return a checksum or a date, take/release the **lock** (one PC syncs at a time), record a PC's **presence**, move to **trash** or **archive**, restore, purge (on request, after 30 days). The data itself travels over **rsync**.
- It **never runs a command** taken from a request, rejects absolute paths and `..`, and uses only Python's standard library.
- **Nothing is permanently deleted** before 30 days (`_corbeille/` trash, `_anciennes/` replaced versions).

### Server requirements

- Linux (tested on Debian / OpenMediaVault) with **OpenSSH** (`authorized_keys`, the `restrict` option: OpenSSH 7.2 or newer).
- **Python 3.6+**, **rsync** and its **`rrsync`** helper (shipped with rsync; Debian/Ubuntu: package `rsync`).
- An SSH account that can **write** to the sync folder (create a dedicated account if you can).
- Synology / QNAP: not verified; Python 3 and rsync can be installed there, `rrsync` may be missing.

### Installation: automatic

**You install nothing by hand**: enrolling a PC (the “Connection to the server” window or `ClaudeSync2.exe enroll`) does it, in 6 steps:

1. Server fingerprint: shown, you confirm it (no blind trust).
2. Server check: `rsync`, `python3` (≥ 3.6), `rrsync` present; clear message otherwise.
3. Program upload: `~/.claudesync/claudesync-serveur` and `claudesync-entree` (mode 700).
4. Data folder: the sync folder and `_etat/hub.id` (the folder's identity, created once).
5. A key specific to this PC: generated on the PC, its **public key** is added to `~/.ssh/authorized_keys` with a forced command (see below). The password, used once, is never stored.
6. Verification: connection with the key, then `hello` to the program.

Enrolling again (or `enroll`) **redeploys** the server program version shipped with the PC.

### What the key can do

Each PC has its own key, added to `authorized_keys` as:

```
restrict,command="CS_ROOT=<sync folder> <home>/.claudesync/claudesync-entree" ssh-ed25519 AAAA... claude-sync:<PC-NAME>
```

`restrict` disables everything (terminal, port forwarding, agent…). The forced command runs **only** `claudesync-entree`, which accepts just:

- `rsync --server …` → run through `rrsync -no-del <sync folder>`: **confined to that folder, no deletion**;
- `claudesync-serveur` → the Python program.

Any other command is refused.

### Sync folder layout

```
<sync folder>/
  projets/                      your projects folder
  claude/projects/              Claude (Code) conversations
  claude/reglages/  claude/file-history/
  desktop/sessions/             Claude Desktop session list
  _corbeille/   _anciennes/     deletions and replaced versions (30 days)
  _etat/                        hub.id, PC presence
  .rsync-tmp/                   rsync temporary files
```

### Requests understood (protocol 1)

`hello`, `ping`, `list`, `stat`, `hash`, `touch`, `lock.acquire`, `lock.release`, `presence.write`, `presence.read`, `trash`, `archive`, `trash.list`, `trash.restore`, `purge`, `bye`. One request = one JSON line; the reply is one JSON line, followed if needed by raw bytes (compressed listings). The lock is tied to the connection: released at once if the PC powers off, or after 180 s of silence.

### Manual installation (administrator)

If you prefer not to let the app write to the server, or for a restricted account:

```sh
# as the user that will receive the sync
mkdir -p ~/.claudesync && chmod 700 ~/.claudesync
cp claudesync-serveur claudesync-entree ~/.claudesync/ && chmod 700 ~/.claudesync/*
mkdir -p /path/to/hub/_etat
# one line per PC (the public key comes from the PC):
echo 'restrict,command="CS_ROOT=/path/to/hub /home/USER/.claudesync/claudesync-entree" ssh-ed25519 AAAA... claude-sync:PC-NAME' >> ~/.ssh/authorized_keys
```

The user must be able to write to `/path/to/hub` (Unix permissions or ACL).

### Updating the server program

For now, the server program is updated **by enrolling the PC again** (which redeploys the files), or by copying the two files from `v2/serveur/` into `~/.claudesync/`. Installed version: `grep '^VERSION' ~/.claudesync/claudesync-serveur`. Client and server check their protocol number (`proto`) on connection.

### Removing it from the server

1. Delete from `~/.ssh/authorized_keys` the lines ending with `claude-sync:<PC-NAME>`.
2. Delete `~/.claudesync/`.
3. Your **data** (the sync folder) is never deleted by Claude-Sync: keeping it or not is up to you.
