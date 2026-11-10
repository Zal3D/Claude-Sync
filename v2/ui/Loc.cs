// Claude-Sync V2 : textes de l'interface en francais et en anglais (aucun texte en dur dans les fenetres).
// Langue : celle choisie dans les Options, sinon celle de Windows (francais si Windows est en francais, anglais sinon).
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeSync.V2;

public static class Loc
{
    public static string Lang { get; private set; } = "fr";
    static readonly Dictionary<string, (string fr, string en)> T = new();
    static void A(string k, string fr, string en) => T[k] = (fr, en);

    public static void Detect(string? configured)
    {
        if (configured == "fr" || configured == "en") Lang = configured;
        else Lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en";
    }

    public static void Set(string lang) { Lang = lang == "en" ? "en" : "fr"; }

    // Texte traduit ; "{0}" ... remplaces par les arguments. Cle inconnue : la cle elle-meme (visible, jamais d'exception).
    public static string S(string key, params object[] args)
    {
        if (!T.TryGetValue(key, out var v)) return key;
        string s = Lang == "en" ? v.en : v.fr;
        return args.Length > 0 ? string.Format(CultureInfo.CurrentCulture, s, args) : s;
    }

    public static bool Has(string key) => T.ContainsKey(key);
    public static IEnumerable<string> Keys => T.Keys;
    public static string Fr(string key) => T[key].fr;
    public static string En(string key) => T[key].en;

    static Loc()
    {
        // navigation
        A("app", "Claude-Sync", "Claude-Sync");
        A("nav.menu", "Menu", "Menu");
        A("nav.home", "Accueil", "Home");
        A("nav.conflicts", "Conflits", "Conflicts");
        A("nav.trash", "Corbeille", "Trash");
        A("nav.options", "Options", "Settings");
        A("nav.server", "Connexion au serveur", "Server connection");
        A("nav.about", "À propos", "About");
        A("nav.back", "Retour", "Back");
        A("nav.quit", "Quitter Claude-Sync", "Quit Claude-Sync");
        // etats
        A("state.working", "En cours", "Syncing");
        A("state.idle", "À jour", "Up to date");
        A("state.conflict", "Conflit", "Conflict");
        A("state.error", "Erreur", "Error");
        A("state.offline", "Hors ligne", "Offline");
        A("state.blocked", "Attention", "Attention");
        A("state.waiting", "En attente", "Waiting");
        // accueil
        A("home.first", "En attente de la première synchronisation", "Waiting for the first sync");
        A("home.files", "Fichiers", "Files");
        A("home.data", "Données", "Data");
        A("home.speed", "Vitesse", "Speed");
        A("home.avgspeed", "Vitesse moyenne", "Average speed");
        A("home.left", "{0} restants", "{0} left");
        A("home.outof", "sur {0}", "of {0}");
        A("home.endsin", "fin dans {0}", "done in {0}");
        A("home.eta", "reste environ {0}", "about {0} left");
        A("home.calc", "calcul du temps restant...", "estimating time left...");
        A("home.current", "En cours : {0}", "Now: {0}");
        A("home.last", "Dernière synchronisation : {0}", "Last sync: {0}");
        A("home.never", "Aucune synchronisation pour l'instant", "No sync yet");
        A("home.detail", "Détail", "Details");
        A("home.uptodate", "à jour", "up to date");
        A("set.conversations", "Conversations", "Conversations");
        A("set.historique", "Historique des sessions", "Session history");
        A("set.projets", "Projets ({0})", "Projects ({0})");
        A("set.sessions", "Liste des sessions", "Session list");
        A("home.conflicts", "{0} conflit(s) à résoudre  ›", "{0} conflict(s) to resolve  ›");
        A("home.noconflict", "Aucun conflit", "No conflicts");
        A("home.trash", "Corbeille : {0} suppression(s)  ›", "Trash: {0} deletion(s)  ›");
        A("home.notconfigured", "Pas encore connecté à un serveur.", "Not connected to a server yet.");
        A("home.syncnow", "Synchroniser maintenant", "Sync now");
        // unites
        A("unit.gb", "Go", "GB"); A("unit.mb", "Mo", "MB"); A("unit.kb", "Ko", "KB"); A("unit.b", "o", "B");
        A("unit.h", "h", "h"); A("unit.min", "min", "min"); A("unit.s", "s", "s"); A("eta.few", "quelques secondes", "a few seconds");
        // conflits
        A("conf.intro", "Les deux versions sont conservées. Choisissez celle à garder : l'autre reste dans les archives pendant 30 jours.", "Both versions are kept. Choose the one to keep: the other stays in the archives for 30 days.");
        A("conf.here", "Ce PC", "This PC");
        A("conf.server", "Serveur", "Server");
        A("conf.mine", "Garder la mienne", "Keep mine");
        A("conf.theirs", "Prendre celle du serveur", "Take the server's");
        A("conf.both", "Garder les deux", "Keep both");
        A("conf.none", "Aucun conflit à résoudre.", "No conflicts to resolve.");
        A("conf.working", "Résolution...", "Resolving...");
        // corbeille
        A("trash.intro", "Les suppressions sont conservées 30 jours. Un clic remet tout en place sur vos PC.", "Deletions are kept for 30 days. One click puts everything back on your PCs.");
        A("trash.loading", "Chargement...", "Loading...");
        A("trash.empty", "Rien à restaurer pour l'instant.", "Nothing to restore yet.");
        A("trash.error", "Impossible de lire la corbeille : {0}", "Couldn't read the trash: {0}");
        A("trash.all", "Tout restaurer", "Restore all");
        A("trash.show", "Voir le détail", "Show details");
        A("trash.hide", "Masquer le détail", "Hide details");
        A("trash.one", "Restaurer", "Restore");
        A("trash.working", "Restauration...", "Restoring...");
        A("trash.item", "{0} élément", "{0} item");
        A("trash.items", "{0} éléments", "{0} items");
        A("trash.file", "{0} fichier", "{0} file");
        A("trash.files", "{0} fichiers", "{0} files");
        A("trash.session", "Session « {0} »", "Session “{0}”");
        A("trash.folder", "Dossier {0}", "Folder {0}");
        A("trash.by", "(supprimé depuis {0})", "(deleted from {0})");
        // options
        A("opt.server", "Serveur", "Server");
        A("opt.edit", "Modifier la connexion…", "Edit connection…");
        A("opt.connect", "Se connecter à un serveur…", "Connect to a server…");
        A("opt.notconf", "Pas encore configuré", "Not configured yet");
        A("opt.test", "Tester la liaison", "Test the link");
        A("opt.testing", "Mesure...", "Measuring...");
        A("opt.linkfast", "{0} ms (rapide)", "{0} ms (fast)");
        A("opt.linkslow", "{0} ms (lente : mode économe)", "{0} ms (slow: economy mode)");
        A("opt.linkdown", "Serveur injoignable", "Server unreachable");
        A("opt.link", "Liaison", "Link");
        A("opt.slow", "Seuil de liaison lente", "Slow-link threshold");
        A("opt.slowhelp", "Au-delà, Claude s'ouvre sans attendre la fin de la synchronisation (au plus {0} s) ; le reste est repris à sa fermeture.", "Above this, Claude opens without waiting for the sync to finish (at most {0} s); the rest resumes when it closes.");
        A("opt.app", "Application", "Application");
        A("opt.lang", "Langue", "Language");
        A("opt.startup", "Démarrer avec Windows", "Start with Windows");
        A("opt.startup.managed", "Disponible une fois Claude-Sync installé (menu Démarrer).", "Available once Claude-Sync is installed (Start menu).");
        A("opt.startup.on", "Activé", "On");
        A("opt.startup.off", "Désactivé", "Off");
        A("opt.update", "Rechercher une mise à jour", "Check for updates");
        A("opt.update.soon", "Les mises à jour par GitHub arrivent bientôt.", "Updates from GitHub are coming soon.");
        A("opt.folders", "Dossiers", "Folders");
        A("opt.projects", "Dossier des projets", "Projects folder");
        // connexion serveur
        A("srv.intro", "Le mot de passe sert une seule fois à inscrire ce PC ; il n'est jamais enregistré. Ensuite, une clé propre à ce PC suffit.", "The password is used once to register this PC; it is never saved. After that, a key unique to this PC is enough.");
        A("srv.host", "Adresse du serveur", "Server address");
        A("srv.port", "Port SSH", "SSH port");
        A("srv.user", "Utilisateur", "User");
        A("srv.password", "Mot de passe", "Password");
        A("srv.folder", "Dossier sur le serveur", "Folder on the server");
        A("srv.projects", "Dossier des projets de ce PC", "Projects folder on this PC");
        A("srv.pc", "Nom de ce PC", "Name of this PC");
        A("srv.register", "Inscrire ce PC", "Register this PC");
        A("srv.working", "Inscription en cours...", "Registering...");
        A("srv.step.pin", "Empreinte du serveur", "Server fingerprint");
        A("srv.step.check", "Contrôle du serveur", "Checking the server");
        A("srv.step.deposit", "Dépôt du programme serveur", "Installing the server program");
        A("srv.step.folder", "Dossier de données", "Data folder");
        A("srv.step.key", "Clé de ce PC", "This PC's key");
        A("srv.step.verify", "Vérification", "Verifying");
        A("srv.fp.title", "Empreinte du serveur", "Server fingerprint");
        A("srv.fp.ask", "Voici l'empreinte du serveur :\n\n{0}\n\nFaire confiance à ce serveur ? Comparez-la avec celle que vous connaissez.", "Here is the server fingerprint:\n\n{0}\n\nTrust this server? Compare it with the one you know.");
        A("srv.done", "Ce PC est inscrit. La première synchronisation démarre.", "This PC is registered. The first sync is starting.");
        A("srv.missing", "Renseignez l'adresse, l'utilisateur et le mot de passe.", "Enter the address, user and password.");
        // zone de notification et surveillance de Claude
        A("tray.open", "Ouvrir Claude-Sync", "Open Claude-Sync");
        A("tray.sync", "Synchroniser maintenant", "Sync now");
        A("tray.pause", "Suspendre la synchronisation", "Pause syncing");
        A("tray.resume", "Reprendre la synchronisation", "Resume syncing");
        A("tray.unreachable", "Serveur injoignable : Claude démarre sans synchronisation (reportée).", "Server unreachable: Claude is starting without syncing (postponed).");
        A("tray.refused", "Accès refusé par le serveur : Claude démarre sans synchronisation (reportée).", "Server refused access: Claude is starting without syncing (postponed).");
        A("tray.slow", "Connexion lente : Claude s'ouvre, le reste sera récupéré à sa fermeture.", "Slow connection: Claude is opening, the rest will be fetched when it closes.");
        A("tray.conflicts", "Des conflits sont à résoudre.", "Some conflicts need your attention.");
        A("tray.other.title", "Claude-Sync", "Claude-Sync");
        A("tray.other.ask", "Claude semble ouvert sur « {0} ». Synchroniser maintenant pourrait mélanger deux versions.\n\nOui : ouvrir quand même, sans synchroniser.\nNon : fermer Claude et attendre que l'autre PC se ferme.", "Claude seems to be open on “{0}”. Syncing now could mix two versions.\n\nYes: open anyway, without syncing.\nNo: close Claude and wait until the other PC closes it.");
        A("tray.phase.sync", "Synchronisation avant l'ouverture de Claude...", "Syncing before Claude opens...");
        A("tray.phase.slow", "Connexion lente ({0} ms) : {1} s maximum avant l'ouverture de Claude...", "Slow connection ({0} ms): {1} s at most before Claude opens...");
        A("tray.already", "Claude-Sync est déjà lancé.", "Claude-Sync is already running.");
        // installation
        A("setup.title", "Installer Claude-Sync", "Install Claude-Sync");
        A("setup.title.update", "Mettre à jour Claude-Sync", "Update Claude-Sync");
        A("setup.intro", "Claude-Sync garde vos conversations Claude Desktop et vos projets identiques sur tous vos PC, grâce à votre propre serveur (SSH). Aucun droit administrateur n'est nécessaire.", "Claude-Sync keeps your Claude Desktop conversations and your projects identical on all your PCs, using your own server (SSH). No administrator rights are needed.");
        A("setup.folder", "Dossier d'installation : {0}", "Install folder: {0}");
        A("setup.startup", "Démarrer Claude-Sync avec Windows (recommandé)", "Start Claude-Sync with Windows (recommended)");
        A("setup.install", "Installer", "Install");
        A("setup.update", "Mettre à jour", "Update");
        A("setup.reinstall", "Réinstaller", "Reinstall");
        A("setup.uninstall", "Désinstaller", "Uninstall");
        A("setup.cancel", "Annuler", "Cancel");
        A("setup.open", "Ouvrir Claude-Sync", "Open Claude-Sync");
        A("setup.close", "Fermer", "Close");
        A("setup.installed", "Version {0} déjà installée.", "Version {0} is already installed.");
        A("setup.p.stop", "Arrêt de l'ancienne version...", "Stopping the previous version...");
        A("setup.p.copy", "Copie du programme...", "Copying the program...");
        A("setup.p.tools", "Installation des outils (rsync, ssh)...", "Installing tools (rsync, ssh)...");
        A("setup.p.links", "Raccourcis...", "Shortcuts...");
        A("setup.p.reg", "Enregistrement dans Windows...", "Registering with Windows...");
        A("setup.p.launch", "Démarrage de Claude-Sync...", "Starting Claude-Sync...");
        A("setup.p.done", "Terminé", "Done");
        A("setup.done", "Claude-Sync est installé. Il vous guidera pour la connexion à votre serveur.", "Claude-Sync is installed. It will guide you through connecting to your server.");
        A("setup.error", "Échec de l'installation : {0}", "Installation failed: {0}");
        A("setup.nopayload", "Ce programme ne contient pas les outils (version de développement).", "This program does not contain the tools (development build).");
        A("setup.corrupt", "Le programme est abîmé (empreinte des outils incorrecte). Téléchargez-le de nouveau.", "The program is damaged (tools checksum mismatch). Download it again.");
        A("setup.uninstall.ask", "Désinstaller Claude-Sync ?\n\nVos conversations et vos projets synchronisés ne sont pas touchés. La configuration et la clé de ce PC sont conservées (vous pourrez réinstaller sans tout refaire).", "Uninstall Claude-Sync?\n\nYour synced conversations and projects are not touched. This PC's configuration and key are kept (you can reinstall without starting over).");
        A("setup.uninstalled", "Claude-Sync est désinstallé.", "Claude-Sync has been uninstalled.");
        // mises a jour
        A("update.off", "Mises à jour automatiques : non configurées (réglage « mises_a_jour » de config.json).", "Automatic updates: not configured (“mises_a_jour” setting in config.json).");
        A("update.check", "Rechercher une mise à jour", "Check for updates");
        A("update.checking", "Recherche en cours...", "Checking...");
        A("update.uptodate", "Vous avez la dernière version.", "You have the latest version.");
        A("update.available", "La version {0} est disponible.", "Version {0} is available.");
        A("update.install", "Installer la version {0}", "Install version {0}");
        A("update.downloading", "Téléchargement et vérification de l'empreinte...", "Downloading and verifying the checksum...");
        A("update.failed", "Mise à jour impossible : {0}", "Update failed: {0}");
        A("update.noreply", "réponse inattendue de la source", "unexpected reply from the source");
        A("update.balloon", "Claude-Sync {0} est disponible : ouvrez « À propos » pour l'installer.", "Claude-Sync {0} is available: open “About” to install it.");
        A("tray.update", "Mettre à jour vers {0}...", "Update to {0}...");
        // a propos
        A("about.text", "Synchronise vos conversations Claude Desktop et vos projets entre vos PC, par SSH et rsync.", "Syncs your Claude Desktop conversations and your projects between your PCs, over SSH and rsync.");
        A("about.license", "Licence MIT. rsync, OpenSSH et leurs bibliothèques gardent leurs propres licences (notices tierces fournies avec le programme).", "MIT license. rsync, OpenSSH and their libraries keep their own licenses (third-party notices ship with the app).");
        A("about.version", "Version {0}", "Version {0}");
        A("about.server", "Serveur : {0}", "Server: {0}");
    }
}
