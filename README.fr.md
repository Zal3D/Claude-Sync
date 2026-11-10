# Claude-Sync

*English version: [README.md](README.md)*

Garde vos **conversations Claude Desktop** (sessions Cowork / Code) et votre **dossier de projets** identiques sur plusieurs PC Windows,
grâce à **votre propre serveur** joint en SSH. Pas de service dans le nuage, pas de compte, rien ne sort de votre réseau.

> **État : bêta (2.0.0-dev).** Développé et testé par son auteur (y compris sur liaisons lentes simulées), pas encore largement éprouvé au quotidien. Gardez vos propres sauvegardes.

## Fonctionnement

- Chaque PC fait tourner une petite application dans la zone de notification. À la fermeture de Claude Desktop elle envoie ce qui a changé au serveur ; à son ouverture elle récupère ce qu'a fait l'autre PC.
- Les copies utilisent **rsync par SSH** : seule la *différence* d'un fichier circule, compressée. Un journal de conversation qui grossit de 1 Mo n'envoie qu'environ un quart (mesuré : 274 Ko), pas le fichier entier — utile en 4G lente.
- Le serveur garde une corbeille et des archives 30 jours : rien n'est jamais écrasé ni supprimé sans copie.
- Si les deux PC ont modifié le même fichier, rien n'est perdu : c'est un *conflit* et vous choisissez la version à garder.
- Windows pour les PC. Le serveur peut être n'importe quelle machine avec SSH, `rsync` et Python 3 (NAS Linux, Raspberry Pi, serveur loué…).

## Installation

1. **Serveur** (une fois) : un compte SSH avec `rsync` et `python3`. Créez un compte dédié si possible ; un compte utilisateur ordinaire convient aussi.
2. **Chaque PC** : lancer `Claude-Sync-Setup.exe`. Aucun droit administrateur n'est nécessaire (installation dans votre profil).
3. Au premier démarrage, saisissez l'adresse du serveur, votre nom d'utilisateur, le dossier à utiliser sur le serveur et votre **mot de passe — utilisé une seule fois**
   pour installer une clé SSH propre à ce PC, jamais enregistré. Ensuite, c'est la clé qui sert (limitée à Claude-Sync : elle ne peut rien exécuter d'autre sur le serveur).
4. C'est fini. Claude-Sync démarre avec Windows et surveille Claude Desktop.

Windows peut afficher *« Éditeur inconnu »* : l'installeur n'est pas signé avec un certificat de signature payant. Vérifiez l'empreinte SHA-256 indiquée sur la page de la version.

## Sécurité

- Gardez le port SSH derrière un VPN ou un pare-feu ; ne l'exposez pas sur Internet.
- Chaque PC a sa propre clé, limitée par une commande imposée aux seules opérations de Claude-Sync (rsync dans un dossier sans suppression, et le programme serveur de Claude-Sync).
- Le mot de passe ne sert que pendant l'installation, via un petit programme SSH temporaire, et n'est jamais écrit sur le disque.
- Mises à jour : l'application peut interroger les versions publiées sur GitHub (réglage `mises_a_jour` = `propriétaire/dépôt` dans `config.json`), vérifie l'empreinte SHA-256 publiée et **n'installe que sur votre clic**.
  L'empreinte prouve que le fichier est intact, pas qui l'a publié : la confiance repose sur le propriétaire du dépôt.

## Compiler depuis les sources

Windows et PowerShell ; le compilateur (Roslyn) est fourni dans `tools/roslyn`. Python 3 sert aux tests.

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-v2.ps1            # compile et lance les tests
# résultat : dist\v2app\ClaudeSync2.exe  (un seul fichier, outils inclus)
```

## Licence

MIT (voir `LICENSE`). L'installeur embarque rsync, OpenSSH et leurs bibliothèques de MSYS2, non modifiés, chacun sous sa licence :
voir [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). « Claude » est une marque d'Anthropic ; ce projet est indépendant, non affilié à Anthropic et non cautionné par elle.
