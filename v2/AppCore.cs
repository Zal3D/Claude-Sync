// Claude-Sync V2 : configuration de l'application, connexion au serveur qui se retablit seule, mesure de liaison.
// La configuration ne contient AUCUN secret : le serveur, l'utilisateur, le dossier, le nom du PC et les CHEMINS de la cle et du known_hosts.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClaudeSync.V2;

public sealed class AppConfig
{
    public string Host = "", User = "", Folder = "", Pc = Environment.MachineName;
    public int Port = 22;
    public string KeyFile = "", KnownHosts = "", ToolsDir = "";
    public string ProjetsRoot = @"C:\Projets";
    public string? IndexDir;                      // dossier des sessions de Claude Desktop (detecte si absent)
    public int SlowMs = 120;                      // au-dela : liaison lente (mode econome a l'ouverture de Claude)
    public int LaunchWaitSeconds = 120;
    public bool BlockLaunch;                      // a l'ouverture de Claude : le fermer, synchroniser, le relancer (DESACTIVE par defaut : choix de l'utilisateur, Options)
    public bool AutoUpdate;                       // installer les mises a jour toutes seules, quand l'application est au repos (DESACTIVE par defaut)
    public string UpdateSource = "";             // depot GitHub (ou autre) des mises a jour
    public string Language = "";                  // "" = langue de Windows

    // Dossier d'etat de la V2 : distinct de celui de la V1 tant que la migration n'est pas faite (aucun risque pour les donnees de la V1).
    public static string DefaultStateDir => Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "ClaudeSync2");
    public static string FileOf(string stateDir) => Path.Combine(stateDir, "config.json");

    public static AppConfig? Load(string stateDir)
    {
        try
        {
            string p = FileOf(stateDir);
            if (!File.Exists(p)) return null;
            var d = Json.ParseObject(File.ReadAllText(p, Encoding.UTF8));
            var c = new AppConfig
            {
                Host = Json.Str(d, "hote") ?? "", User = Json.Str(d, "utilisateur") ?? "", Folder = Json.Str(d, "dossier") ?? "",
                Pc = Json.Str(d, "pc") ?? Environment.MachineName, KeyFile = Json.Str(d, "cle") ?? "", KnownHosts = Json.Str(d, "known_hosts") ?? "",
                ToolsDir = Json.Str(d, "outils") ?? "", ProjetsRoot = (Json.Str(d, "projets") ?? @"C:\Projets").Replace('/', '\\'), IndexDir = Json.Str(d, "sessions")?.Replace('/', '\\'),
                UpdateSource = Json.Str(d, "mises_a_jour") ?? "", Language = Json.Str(d, "langue") ?? ""
            };
            long port = Json.Lng(d, "port"); if (port > 0) c.Port = (int)port;
            long slow = Json.Lng(d, "seuil_lent_ms"); if (slow > 0) c.SlowMs = (int)slow;
            long wait = Json.Lng(d, "attente_ouverture_s"); if (wait > 0) c.LaunchWaitSeconds = (int)wait;
            if (d.ContainsKey("bloquer_ouverture")) c.BlockLaunch = Json.Bool(d, "bloquer_ouverture");
            if (d.ContainsKey("mises_a_jour_auto")) c.AutoUpdate = Json.Bool(d, "mises_a_jour_auto");
            return c.Host.Length > 0 && c.User.Length > 0 ? c : null;
        }
        catch (Exception) { return null; }
    }

    public void Save(string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        var d = new List<KeyValuePair<string, object?>>
        {
            new("hote", Host), new("port", Port), new("utilisateur", User), new("dossier", Folder), new("pc", Pc),
            new("cle", KeyFile.Replace('\\', '/')), new("known_hosts", KnownHosts.Replace('\\', '/')), new("outils", ToolsDir.Replace('\\', '/')),
            new("projets", ProjetsRoot.Replace('\\', '/')), new("sessions", IndexDir?.Replace('\\', '/')),
            new("seuil_lent_ms", SlowMs), new("attente_ouverture_s", LaunchWaitSeconds), new("bloquer_ouverture", BlockLaunch), new("mises_a_jour_auto", AutoUpdate), new("mises_a_jour", UpdateSource), new("langue", Language)
        };
        var sb = new StringBuilder("{\n");
        for (int i = 0; i < d.Count; i++) sb.Append("  ").Append(Json.Serialize(d[i].Key)).Append(": ").Append(Json.Serialize(d[i].Value)).Append(i < d.Count - 1 ? ",\n" : "\n");
        sb.Append("}\n");
        string tmp = FileOf(stateDir) + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(FileOf(stateDir))) File.Delete(FileOf(stateDir));
        File.Move(tmp, FileOf(stateDir));
    }

    public SshTarget ToTarget() => new()
    {
        SshExe = Path.Combine(ToolsDir, "ssh.exe"), KeyFile = KeyFile, KnownHosts = KnownHosts, Host = Host, User = User, Port = Port
    };

    public RsyncTransport NewTransport() =>
        new(new RsyncOptions { ToolsDir = ToolsDir }, new RsyncRemote { Ssh = ToTarget() });

    public EngineOptions ToEngineOptions(string stateDir) => new()
    {
        Pc = Pc, ProjetsRoot = ProjetsRoot, IndexDir = IndexDir, StateDir = stateDir
    };
}

// Fournit toujours une session serveur vivante : si la precedente est morte (liaison perdue, delai depasse), une nouvelle est ouverte.
// Ne reconnecte JAMAIS au milieu d'une synchro : le verrou est lie a la session, une synchro interrompue est reportee.
public sealed class HubProvider : IDisposable
{
    readonly Func<HubServeur> open;
    readonly object gate = new();
    HubServeur? cur;

    public HubProvider(Func<HubServeur> open) { this.open = open; }
    public static HubProvider ForConfig(AppConfig c) => new(() => HubServeur.OpenSsh(c.ToTarget()));

    public IHub Get()
    {
        lock (gate)
        {
            if (cur == null || cur.Session.HasExited) { cur?.Dispose(); cur = open(); }
            return cur;
        }
    }

    public void Reset() { lock (gate) { cur?.Dispose(); cur = null; } }
    public void Dispose() => Reset();
}

public static class LinkQuality
{
    // Mediane de plusieurs allers-retours avec le serveur (ms) ; 9999 si injoignable.
    public static int MeasureMs(IHub hub, int samples = 4)
    {
        try { return hub.PingMs(samples); } catch (HubException) { return 9999; }
    }

    public static bool IsSlow(int ms, AppConfig c) => ms > c.SlowMs;
}
