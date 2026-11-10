// Claude-Sync V2 : le "hub" (le serveur) vu par le moteur. C# 5 / .NET Framework 4.x.
// Le moteur ne connait que cette interface ; HubServeur la realise avec le programme serveur (SSH ou local pour les tests).
using System;
using System.Collections.Generic;

namespace ClaudeSync.V2
{
    public enum HubErrKind
    {
        Network,   // connexion impossible, coupee ou delai depasse : a reessayer plus tard (jamais de repli)
        Protocol,  // reponse incomprehensible ou version du protocole incompatible
        Refused,   // le serveur a refuse la requete (chemin invalide, jeu inconnu...)
        Server     // le serveur a signale une erreur
    }

    public sealed class HubException : Exception
    {
        public readonly HubErrKind Kind;
        public HubException(HubErrKind kind, string message) : base(message) { Kind = kind; }
        public HubException(HubErrKind kind, string message, Exception inner) : base(message, inner) { Kind = kind; }
    }

    public sealed class HelloInfo
    {
        public string ServerVersion;
        public int Proto;
        public string HubId;
        public long ServerTicks;
    }

    public sealed class ListResult
    {
        public bool Unchanged;      // vrai si l'empreinte fournie est toujours la bonne (Files est alors null)
        public string Token;        // empreinte du listage, a redonner a la prochaine demande
        public int Count;           // nombre de fichiers du jeu
        public int Ignored;         // noms ignores par le serveur (octets invalides, tabulation...)
        public int Errors;          // erreurs de lecture cote serveur (dossier illisible...) : le moteur ne doit deduire AUCUNE suppression
        public Dictionary<string, FileEntry> Files;
        public List<string> CaseCollisions = new List<string>(); // chemins ne differant que par la casse (Windows ne sait pas les distinguer)
    }

    public sealed class LockResult
    {
        public bool Ok;
        public string Holder;       // PC qui tient le verrou (si refuse)
        public long AgeSeconds;
        public bool TookOrphan;     // un verrou abandonne a ete repris
    }

    public sealed class PresenceInfo
    {
        public string Pc;
        public long Ticks;
        public bool Running;        // Claude est ouvert sur ce PC
    }

    public sealed class TrashFile
    {
        public string Stamp, Set, Rel, Cli, Title;   // Cli/Title : renseignes seulement pour les fiches de sessions
        public long Size;
    }

    public sealed class TrashListing
    {
        public Dictionary<string, string> PcByStamp = new Dictionary<string, string>();   // lot -> PC qui a supprime
        public List<TrashFile> Files = new List<TrashFile>();
    }

    public interface IHub : IDisposable
    {
        TrashListing TrashList();                                              // contenu de la corbeille du serveur
        bool RestoreFromTrash(string stamp, string set, string rel);           // faux si un fichier existe deja a cette place (rien n'est ecrase)
        HelloInfo Hello();
        int PingMs(int samples);                                  // mediane de plusieurs allers-retours, en ms
        ListResult List(string set, string ifChangedToken);
        Dictionary<string, FileEntry> Stat(string set, IList<string> rels);   // null pour un fichier absent
        LockResult AcquireLock(string pc);
        void ReleaseLock();
        void WritePresence(string pc, bool running);
        List<PresenceInfo> ReadPresence();
        bool Trash(string set, string rel, string stamp, string pc);          // faux si le fichier n'existe pas
        bool Archive(string set, string rel, string stamp);
        string Hash(string set, string rel);                                   // SHA-256 (hex minuscule) calcule par le serveur ; null si le fichier n'existe pas
        bool Touch(string set, string rel, long ticks);                        // remet la date d'un fichier du serveur ; faux s'il n'existe pas
        List<string> Purge(int days, bool dryRun);
    }
}
