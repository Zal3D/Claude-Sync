// Claude-Sync V2 : modele de l'interface (etat affiche + actions). Toutes les operations longues tournent en tache de fond ;
// les fenetres s'abonnent a Changed (declenche sur le fil de l'interface) et ne bloquent jamais.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeSync.V2;

public static class V2Info
{
    public const string Version = "2.0.0-beta.2";
}

public sealed class AppModel
{
    public readonly string StateDir;
    public AppConfig? Config;
    public UiState State = UiState.Waiting;
    public string Phase = Loc.S("home.first"), Detail = "";
    public ProgressInfo P = new();
    public DateTime? Last;
    public List<ConflictRec> Conflicts = new();
    public List<TrashEntry>? Trash;            // null = pas encore charge
    public string TrashError = "";
    public int LinkMs = -1;                            // derniere mesure de liaison (-1 = aucune, 9999 = injoignable)
    public bool Busy;
    public bool Demo;                                  // donnees fictives : jamais de reseau
    public event Action? Changed;
    readonly SynchronizationContext? ui = SynchronizationContext.Current;
    HubProvider? provider;

    public AppModel(string stateDir) { StateDir = stateDir; Directory.CreateDirectory(stateDir); Reload(); }

    public bool Configured => Config != null;

    public void Reload()
    {
        Config = AppConfig.Load(StateDir);
        if (Config != null && (Config.ToolsDir.Length == 0 || !File.Exists(Path.Combine(Config.ToolsDir, "rsync.exe"))))
            Config.ToolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "outils", "win-x64");
        provider?.Dispose(); provider = null;
        try { Conflicts = new StateStore(StateDir).LoadConflicts(); } catch (Exception) { Conflicts = new List<ConflictRec>(); }
        Loc.Detect(Config?.Language);
        Raise();
    }

    public void Raise()
    {
        if (ui != null) ui.Post(_ => Changed?.Invoke(), null); else Changed?.Invoke();
    }

    HubProvider Provider() => provider ??= HubProvider.ForConfig(Config!);

    Engine2 NewEngine(Action<EngineOptions>? cfg = null)
    {
        var o = Config!.ToEngineOptions(StateDir);
        o.Profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        o.IndexDir ??= ClaudePaths.FindIndexDir(o.Profile);
        cfg?.Invoke(o);
        return new Engine2(o, Provider().Get(), Config.NewTransport());
    }

    // ------------------------------------------------------------ synchronisation
    public Engine2? Engine;                            // moteur de la synchro en cours (arret des recuperations en mode econome)
    public int LastCode;
    public string? PhaseOverride;                      // texte force pendant la prise en charge de l'ouverture de Claude
    public bool Paused;                                // synchronisation suspendue par l'utilisateur
    public bool BlockLaunch => Config?.BlockLaunch ?? true;

    // Synchronisation complete, BLOQUANTE (appelee en tache de fond). claude : "auto" | "no" | "idle" | "yes". Retourne le code du moteur.
    public int RunSyncBlocking(string claude = "auto", int waitLock = 90, bool apply = true)
    {
        if (Config == null) return 3;
        Busy = true; State = UiState.Working; Phase = Loc.S("state.working"); Detail = ""; Raise();
        int code = 3; string msg = "";
        try
        {
            var e = NewEngine(o => { o.Apply = apply; o.ClaudeRunning = claude; o.WaitLockSeconds = waitLock; });
            Engine = e;
            e.OnProgress += pi => { P = pi; Phase = pi.Phase; Raise(); };
            code = e.Run(); msg = e.Summary; Conflicts = e.Conflicts.ToList();
        }
        catch (Exception ex) { msg = ex.Message; code = 2; }
        Engine = null;
        Last = DateTime.Now; Detail = msg; LastCode = code; Busy = false;
        State = code switch { 0 => UiState.Idle, 1 => UiState.Conflict, 2 => UiState.Error, 3 => UiState.Offline, _ => UiState.Blocked };
        Phase = Gfx.StateLabel(State);
        Raise();
        LoadTrash();
        return code;
    }

    public void SyncNow(bool apply = true, string claude = "auto")
    {
        if (Demo || Busy || Config == null) return;
        Task.Run(() => RunSyncBlocking(claude, 90, apply));
    }

    // Mode econome : plus aucune ecriture sur le PC, les envois continuent ; le reste est repris a la fermeture de Claude.
    public void StopPulls() { var e = Engine; if (e != null) e.StopPulls = true; }

    // Combien de fichiers le serveur a-t-il a livrer ? (simulation, rien n'est ecrit)
    public int CountIncoming()
    {
        if (Config == null) return 0;
        return NewEngine(o => { o.Apply = false; o.ClaudeRunning = "no"; }).CountIncoming();
    }

    public string? OtherPcActive() => Config == null ? null : NewEngine().OtherPcActive();

    // Presence de ce PC (Claude ouvert ici ?) : lue par l'autre PC avant qu'il synchronise.
    public void WritePresenceNow(bool running) { if (Config != null) NewEngine().WritePresence(running); }

    // Le serveur repond-il ? Mesure aussi la liaison. refused = cle ou autorisation refusee (inutile de reessayer tout de suite).
    public (bool ok, int ms, string message, bool refused) Probe()
    {
        if (Config == null) return (false, 9999, "", false);
        try { var hub = Provider().Get(); hub.Hello(); return (true, LinkQuality.MeasureMs(hub), "", false); }
        catch (HubException e) { return (false, 9999, e.Message, e.Kind == HubErrKind.Refused); }
    }

    // ------------------------------------------------------------ corbeille et conflits
    public void LoadTrash()
    {
        if (Demo || Config == null) return;
        Task.Run(() =>
        {
            try { Trash = NewEngine().ListEntries(); TrashError = ""; }
            catch (Exception ex) { TrashError = ex.Message; }
            Raise();
        });
    }

    public void Restore(IEnumerable<TrashEntry> items, Action<string> done)
    {
        if (Demo || Config == null) { done(""); return; }
        var list = items.ToList();
        Task.Run(() =>
        {
            string msg = "";
            try { var e = NewEngine(); foreach (var t in list) msg = e.RestoreItems(t); }
            catch (Exception ex) { msg = Loc.S("state.error") + " : " + ex.Message; }
            done(msg); LoadTrash();
        });
    }

    public void Resolve(int index1, string choice, Action<string> done)
    {
        if (Demo || Config == null) { done(""); return; }
        Task.Run(() =>
        {
            string msg;
            try { var e = NewEngine(); msg = e.Resolve(index1, choice); Conflicts = e.LoadConflicts(); }
            catch (Exception ex) { msg = Loc.S("state.error") + " : " + ex.Message; }
            Raise(); done(msg);
        });
    }

    // ------------------------------------------------------------ mises a jour du programme (rien ne s'installe sans clic)
    public UpdateInfo? Update; public string UpdateText = ""; public bool UpdateBusy;
    public event Action? UpdateStarted;                      // l'installeur telecharge est lance : l'application doit se fermer
    public string UpdateSource => string.IsNullOrWhiteSpace(Config?.UpdateSource) ? Updater2.DefaultSource : Config!.UpdateSource.Trim();

    public void CheckUpdate(bool force)
    {
        if (Demo || UpdateBusy || Updater2.SourceUrl(UpdateSource) == null) return;
        UpdateBusy = true; UpdateText = force ? Loc.S("update.checking") : UpdateText; Raise();
        Task.Run(() =>
        {
            var u = Updater2.Check(UpdateSource, V2Info.Version, StateDir, force, DateTime.UtcNow, out string err);
            if (u != null) { Update = u; UpdateText = Loc.S("update.available", u.Version); }
            else if (err == "recent") { }
            else if (err.Length > 0) UpdateText = force ? Loc.S("update.failed", err == "reponse" ? Loc.S("update.noreply") : err) : UpdateText;
            else { Update = null; UpdateText = Loc.S("update.uptodate"); }
            UpdateBusy = false; Raise();
        });
    }

    public void InstallUpdate()
    {
        var u = Update; if (u == null || UpdateBusy) return;
        UpdateBusy = true; UpdateText = Loc.S("update.downloading"); Raise();
        Task.Run(() =>
        {
            try
            {
                string path = Updater2.Download(u, Path.Combine(Path.GetTempPath(), "ClaudeSync2-update"));
                Updater2.Apply(path);
                UpdateBusy = false; UpdateStarted?.Invoke();
            }
            catch (Exception e) { UpdateText = Loc.S("update.failed", e.Message); UpdateBusy = false; Raise(); }
        });
    }

    // ------------------------------------------------------------ liaison et reglages
    public void TestLink(Action<int> done)
    {
        if (Demo || Config == null) { done(-1); return; }
        Task.Run(() =>
        {
            int ms = LinkQuality.MeasureMs(Provider().Get());
            LinkMs = ms; Raise(); done(ms);
        });
    }

    public void SetSlowThreshold(int ms)
    {
        if (Config == null) return;
        Config.SlowMs = Math.Max(40, Math.Min(2000, ms));
        Config.Save(StateDir); Raise();
    }

    public void SetLanguage(string lang)
    {
        Loc.Set(lang);
        if (Config != null) { Config.Language = lang; Config.Save(StateDir); }
        Raise();
    }

    // ------------------------------------------------------------ donnees fictives (captures d'ecran, essais)
    public static AppModel DemoModel(string stateDir)
    {
        var m = new AppModel(stateDir) { Demo = true };
        m.Config = new AppConfig { Host = "nas.exemple.fr", Port = 22, User = "claudesync", Folder = "/srv/claude-sync", Pc = "PC-BUREAU", SlowMs = 120, ProjetsRoot = @"C:\Projets" };
        m.State = UiState.Idle; m.Phase = Loc.S("state.idle"); m.Last = DateTime.Now.AddMinutes(-3); m.LinkMs = 4;
        m.P = new ProgressInfo { TotalFiles = 16, DoneFiles = 16, TotalBytes = 225_000_000, DoneBytes = 225_000_000, BytesPerSec = 8_800_000 };
        m.P.PerSet["projets"] = new long[] { 16, 16 };
        long t = DateTime.UtcNow.Ticks;
        m.Conflicts = new List<ConflictRec>
        {
            new() { Set = "projets", Rel = "reseau/notes.md", LSize = 12_300, LTicks = t - 40 * 600_000_000L, HSize = 14_100, HTicks = t - 35 * 600_000_000L, Detected = t },
            new() { Set = "conversations", Rel = "C--Projets-reseau/abc123.jsonl", LSize = 3_200_000, LTicks = t - 90 * 600_000_000L, HSize = 3_400_000, HTicks = t - 50 * 600_000_000L, Detected = t }
        };
        var a = new TrashEntry { Num = 1, Kind = "projet", Title = "d", Stamp = "20261110-113200", Pc = "PC-A", Date = DateTime.Now.AddHours(-1), Files = 1, Bytes = 80_000, Sample = "d/é b.txt" };
        a.Items.Add(new[] { "20261110-113200/projets/d/é b.txt", "projets", "d/é b.txt" });
        var b = new TrashEntry { Num = 2, Kind = "session", Title = "Simu drone", Stamp = "20261109-211000", Pc = "PC-B", Date = DateTime.Now.AddDays(-1), Files = 14, Bytes = 215_000_000, Sample = "x" };
        var c = new TrashEntry { Num = 3, Kind = "projet", Title = "test", Stamp = "20261109-211000", Pc = "PC-B", Date = DateTime.Now.AddDays(-1), Files = 3, Bytes = 400_000, Sample = "y" };
        m.Trash = new List<TrashEntry> { a, b, c };
        return m;
    }
}
