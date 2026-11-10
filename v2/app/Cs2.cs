// Claude-Sync V2 : application console (inscription, synchro, conflits, corbeille...). Sert aussi de "programme de mot de passe" pour SSH.
// L'interface graphique (icone, fenetres, surveillance de Claude) est branchee dessus a l'etape suivante (J4b).
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ClaudeSync.V2;

public static class Cs2
{
    static string Base => AppDomain.CurrentDomain.BaseDirectory;

    sealed class Args
    {
        public readonly string Cmd; public readonly List<string> Pos = new(); readonly Dictionary<string, string> opt = new(StringComparer.OrdinalIgnoreCase);
        public Args(string[] a)
        {
            Cmd = a.Length > 0 ? a[0].ToLowerInvariant() : "help";
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i].StartsWith("--")) { string k = a[i].Substring(2); opt[k] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "1"; }
                else Pos.Add(a[i]);
            }
        }
        public string? Opt(string k) => opt.TryGetValue(k, out var v) ? v : null;
        public bool Has(string k) => opt.ContainsKey(k);
    }

    // Fenetre de demonstration avec des donnees fictives (aucun reseau) : ui-demo [--page ...] [--drawer] [--lang fr|en] [--light] [--shot fichier.png]
    static int UiDemo(Args a)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
        ThemeX.Force(!a.Has("light"));
        Loc.Detect(a.Opt("lang"));
        string st = Path.Combine(Path.GetTempPath(), "cs2-ui-demo");
        var model = AppModel.DemoModel(st);
        if (a.Has("empty")) { model.Config = null; model.State = UiState.Waiting; model.Phase = Loc.S("home.first"); model.P = new ProgressInfo(); model.Conflicts.Clear(); model.Trash = new List<TrashEntry>(); }
        Loc.Detect(a.Opt("lang"));
        var win = new MainWindow(model);
        PageId page = (a.Opt("page") ?? "home") switch { "conflits" => PageId.Conflicts, "corbeille" => PageId.Trash, "options" => PageId.Options, "serveur" => PageId.Server, "apropos" => PageId.About, _ => PageId.Home };
        string? shot = a.Opt("shot");
        if (shot == null) { win.Navigate(page); if (a.Has("drawer")) win.Shown += (_, _) => win.OpenDrawer(); System.Windows.Forms.Application.Run(win); return 0; }
        win.Show();
        for (int i = 0; i < 6; i++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(60); }
        win.Navigate(page);
        for (int i = 0; i < 6; i++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(60); }
        if (a.Has("drawer")) { win.OpenDrawer(true); System.Windows.Forms.Application.DoEvents(); }
        using (var bmp = win.Screenshot()) bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
        win.HideOnClose = false; win.Close();
        Console.WriteLine("Capture : " + shot);
        return 0;
    }

    // Application graphique residente : icone de la zone de notification + surveillance de Claude. run [--hidden] [--demo] [--exit-after SECONDES]
    static int RunTray(Args a, string state)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
        Theme.Load();
        bool demo = a.Has("demo");
        string mutexName = "Local\\ClaudeSync2-" + (demo ? "demo" : BitConverter.ToString(System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(state.ToLowerInvariant())), 0, 6).Replace("-", ""));
        using var one = new System.Threading.Mutex(true, mutexName, out bool first);
        AppModel model = demo ? AppModel.DemoModel(Path.Combine(Path.GetTempPath(), "cs2-ui-demo")) : new AppModel(state);
        Loc.Detect(model.Config?.Language);
        if (!first) { Console.WriteLine(Loc.S("tray.already")); return 20; }
        AppModel.DefaultProjects = a.Opt("projets"); AppModel.DefaultFolder = a.Opt("folder"); AppModel.DefaultPc = a.Opt("pc");
        int.TryParse(a.Opt("exit-after"), out int after);
        var app = new TrayApp2(model, a.Has("hidden"), after, a.Has("no-watch"));
        System.Windows.Forms.Application.Run(app);
        return 0;
    }


    // ---------- installation (voir Setup2.cs) ; --sandbox DOSSIER : tout dans un dossier d'essai (tests), jamais dans le vrai profil
    static InstallTarget TargetOf(Args a) => a.Opt("sandbox") is { Length: > 1 } sb ? InstallTarget.Sandbox(sb) : InstallTarget.Default();

    static int RunSetupWindow(InstallTarget t, string? shot = null, string? lang = null)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        try { Native.SetProcessDPIAware(); } catch (Exception) { }
        Theme.Load(); Loc.Detect(lang);
        var w = new SetupWindow(t);
        if (shot != null)        // capture d'ecran pour verification visuelle
        {
            w.Show();
            for (int i = 0; i < 6; i++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(60); }
            using (var bmp = new System.Drawing.Bitmap(w.Width, w.Height)) { w.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png); }
            w.Close(); return 0;
        }
        System.Windows.Forms.Application.Run(w);
        return 0;
    }

    static int Install(Args a)
    {
        var t = TargetOf(a);
        if (!a.Has("silent")) return RunSetupWindow(t, a.Opt("shot"), a.Opt("lang"));
        Loc.Detect(a.Opt("lang"));
        var r = Installer2.Install(t, new InstallOptions { Startup = !a.Has("no-startup"), Launch = a.Has("launch") }, (m, p) => Console.WriteLine("  - " + m));
        Console.WriteLine(r.Message + (r.Ok ? "  (" + r.Written + " ecrit(s), " + r.Unchanged + " inchange(s), " + r.Removed + " retire(s))" : ""));
        return r.Ok ? 0 : 2;
    }

    // update [--source proprietaire/depot] [--install] : cherche une version plus recente (et l'installe seulement avec --install)
    static int UpdateCmd(Args a)
    {
        string src = a.Opt("source") ?? Updater2.DefaultSource;
        string st = a.Opt("state") ?? AppConfig.DefaultStateDir;
        if (a.Opt("source") == null && AppConfig.Load(st) is { UpdateSource: { Length: > 0 } cu }) src = cu;
        var u = Updater2.Check(src, V2Info.Version, st, true, DateTime.UtcNow, out string err);
        if (u == null) { Console.WriteLine(err.Length > 0 ? "Verification impossible : " + err : "Version " + V2Info.Version + " : a jour."); return err.Length > 0 ? 3 : 0; }
        Console.WriteLine("Version " + u.Version + " disponible (installee : " + V2Info.Version + "). Empreinte SHA-256 : " + u.Sha256);
        if (!a.Has("install")) return 0;
        string path = Updater2.Download(u, Path.Combine(Path.GetTempPath(), "ClaudeSync2-update"));
        Console.WriteLine("Telecharge et verifie : " + path);
        Updater2.Apply(path);
        return 0;
    }

    static int Uninstall(Args a)
    {
        var t = TargetOf(a);
        Loc.Detect(a.Opt("lang"));
        if (!a.Has("silent"))
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            if (System.Windows.Forms.MessageBox.Show(Loc.S("setup.uninstall.ask"), Loc.S("app"), System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question) != System.Windows.Forms.DialogResult.Yes) return 1;
        }
        var r = Installer2.Uninstall(t, a.Has("purge-state"), a.Opt("state"));
        Console.WriteLine(r.Message);
        return r.Ok ? 0 : 2;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Sans argument : programme telecharge (hors dossier d'installation) = installation ; sinon application residente (raccourci, demarrage de Windows)
        if (args.Length == 0) args = new[] { Installer2.HasEmbeddedPayload && !Installer2.IsInstalledCopy(InstallTarget.Default()) ? "install" : "run" };
        // SSH lance ce programme pour obtenir le mot de passe : il le lit dans l'environnement (jamais sur le disque) et le renvoie.
        // ssh lance ce programme avec le TEXTE DE LA QUESTION comme seul argument (pas "--askpass") : c'est la variable CS_ASKPASS_MODE, donnee
        // uniquement au processus ssh de l'inscription, qui declenche ce mode (defaut trouve avec un vrai mot de passe : l'inscription etait refusee).
        if ((args.Length > 0 && args[0] == "--askpass") || Environment.GetEnvironmentVariable("CS_ASKPASS_MODE") == "1")
        {
            // octets UTF-8 directs : la page de codes de la console deformerait les accents du mot de passe
            byte[] b = new UTF8Encoding(false).GetBytes((Environment.GetEnvironmentVariable("CS_ASKPASS_VALUE") ?? "") + "\n");
            using (var o = Console.OpenStandardOutput()) { o.Write(b, 0, b.Length); o.Flush(); }
            return 0;
        }
        if (args[0] is not ("run" or "ui-demo" or "uninstall-cleanup")) Native.AttachIfNoStdout();      // programme graphique (winexe) : se rattache a la console qui l'a lance
        // sortie en UTF-8 quelle que soit la page de codes (sans console, Console.OutputEncoding ne fait rien : on ecrit directement les octets)
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }); Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true }); } catch (Exception) { }
        //     // accents corrects quel que soit la page de codes de la console
        var a = new Args(args);
        if (a.Cmd == "update") return UpdateCmd(a);
        if (a.Cmd == "install") return Install(a);
        if (a.Cmd == "uninstall") return Uninstall(a);
        if (a.Cmd == "uninstall-cleanup") return Installer2.Cleanup(args.Length > 1 ? args[1] : "");
        if (a.Cmd == "icon") { Theme.Load(); IconArt.WriteIco(a.Pos.Count > 0 ? a.Pos[0] : "ClaudeSync2.ico"); return 0; }
        string state = a.Opt("state") ?? AppConfig.DefaultStateDir;
        Directory.CreateDirectory(state);
        Log.Init(Path.Combine(state, "logs"));
        try
        {
            switch (a.Cmd)
            {
                case "enroll": return Enroll(a, state);
                case "ui-demo": return UiDemo(a);
                case "run": return RunTray(a, state);
                case "help": case "--help": case "-h": return Help();
            }
            var cfg = AppConfig.Load(state);
            if (cfg == null) { Console.WriteLine("Claude-Sync n'est pas configure (" + AppConfig.FileOf(state) + "). Lancez : ClaudeSync2 enroll --host ... --user ..."); return 10; }
            if (cfg.ToolsDir.Length == 0 || !File.Exists(Path.Combine(cfg.ToolsDir, "rsync.exe"))) cfg.ToolsDir = DefaultTools();
            switch (a.Cmd)
            {
                case "ping": return Ping(cfg);
                case "sync": return Sync(a, cfg, state);
                case "conflicts": return Conflicts(cfg, state);
                case "resolve": return Resolve(a, cfg, state);
                case "trash": return Trash(cfg, state);
                case "restore": return Restore(a, cfg, state);
                case "purge": return Purge(a, cfg, state);
                default: return Help();
            }
        }
        catch (HubException e) { Console.WriteLine("Serveur : " + e.Message); return 3; }
    }

    static string DefaultTools() => Path.Combine(Base, "outils", "win-x64");

    static int Help()
    {
        Console.WriteLine("Claude-Sync V2 (console)\n" +
            "  enroll --host H --user U [--port 22] [--folder claude-sync] [--pc NOM] [--projets C:\\Projets] [--fingerprint SHA256:... | --yes] [--admin-key FICHIER [--admin-login root@hote]]   (mot de passe : variable CS_PASSWORD, sinon saisie)\n" +
            "  sync [--apply] [--claude auto|yes|no|idle] [--wait-lock SECONDES] [--no-pull]    (sans --apply : simulation)\n" +
            "  update [--source proprietaire/depot] [--install]\n" +
            "  install [--silent] [--no-startup] [--launch] | uninstall [--silent] [--purge-state]   (--sandbox DOSSIER : essai hors du vrai profil)\n" +
            "  ping | conflicts | resolve N local|hub|both [--claude no] | trash | restore N [--claude no] | purge [--days 30]\n" +
            "  option commune : --state DOSSIER (defaut : %LOCALAPPDATA%\\ClaudeSync2)");
        return 0;
    }

    static (Engine2 e, HubProvider p) NewEngine(AppConfig c, string state, Action<EngineOptions>? cfg = null)
    {
        var prov = HubProvider.ForConfig(c);
        var o = c.ToEngineOptions(state);
        o.Profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        o.IndexDir ??= ClaudePaths.FindIndexDir(o.Profile);
        cfg?.Invoke(o);
        return (new Engine2(o, prov.Get(), c.NewTransport()), prov);
    }

    static int Ping(AppConfig c)
    {
        using var p = HubProvider.ForConfig(c);
        int ms = LinkQuality.MeasureMs(p.Get());
        Console.WriteLine(ms >= 9999 ? "Serveur injoignable." : "Liaison : " + ms + " ms" + (LinkQuality.IsSlow(ms, c) ? " (LENTE : mode econome a l'ouverture de Claude)" : " (rapide)"));
        return ms >= 9999 ? 3 : 0;
    }

    static int Sync(Args a, AppConfig c, string state)
    {
        var (e, p) = NewEngine(c, state, o =>
        {
            o.Apply = a.Has("apply"); o.NoPull = a.Has("no-pull"); o.IgnorePresence = a.Has("ignore-presence");
            o.ClaudeRunning = a.Opt("claude") ?? "auto";
            if (int.TryParse(a.Opt("wait-lock"), out int w)) o.WaitLockSeconds = w;
        });
        using (p)
        {
            int code = e.Run();
            Console.WriteLine(e.Summary);
            foreach (var n in e.Notices) Console.WriteLine("Note : " + n);
            return code;
        }
    }

    static int Conflicts(AppConfig c, string state)
    {
        var (e, p) = NewEngine(c, state);
        using (p)
        {
            var l = e.LoadConflicts();
            if (l.Count == 0) Console.WriteLine("Aucun conflit.");
            for (int i = 0; i < l.Count; i++) Console.WriteLine((i + 1) + ". [" + l[i].Set + "] " + l[i].Rel + "  (ici " + Engine2.Mb(l[i].LSize) + ", serveur " + Engine2.Mb(l[i].HSize) + ")");
            return 0;
        }
    }

    static int Resolve(Args a, AppConfig c, string state)
    {
        if (a.Pos.Count < 2 || !int.TryParse(a.Pos[0], out int n)) { Console.WriteLine("Usage : resolve N local|hub|both"); return 2; }
        var (e, p) = NewEngine(c, state, o => o.ClaudeRunning = a.Opt("claude") ?? "auto");
        using (p) { string m = e.Resolve(n, a.Pos[1]); Console.WriteLine(m); return m.StartsWith("Conflit resolu") ? 0 : 1; }
    }

    static int Trash(AppConfig c, string state)
    {
        var (e, p) = NewEngine(c, state);
        using (p)
        {
            var l = e.ListEntries();
            if (l.Count == 0) Console.WriteLine("Corbeille vide.");
            foreach (var t in l) Console.WriteLine(t.Num + ". " + t.Date.ToString("yyyy-MM-dd HH:mm") + "  " + t.Kind + "  \"" + t.Title + "\"  " + t.Files + " fichier(s), " + Engine2.Mb(t.Bytes) + (t.Pc.Length > 0 ? "  (par " + t.Pc + ")" : ""));
            return 0;
        }
    }

    static int Restore(Args a, AppConfig c, string state)
    {
        if (a.Pos.Count < 1 || !int.TryParse(a.Pos[0], out int n)) { Console.WriteLine("Usage : restore N"); return 2; }
        var (e, p) = NewEngine(c, state, o => o.ClaudeRunning = a.Opt("claude") ?? "auto");
        using (p) { Console.WriteLine(e.RestoreEntry(n)); return 0; }
    }

    static int Purge(Args a, AppConfig c, string state)
    {
        int days = int.TryParse(a.Opt("days"), out int d) ? d : 30;
        var (e, p) = NewEngine(c, state);
        using (p) { Console.WriteLine(e.PurgeAll(days) + " lot(s) de plus de " + days + " jours supprime(s) definitivement."); return 0; }
    }

    // ---------- inscription
    static string ReadPassword()
    {
        string? env = Environment.GetEnvironmentVariable("CS_PASSWORD");
        if (!string.IsNullOrEmpty(env)) return env;
        Console.Write("Mot de passe (utilise une seule fois, jamais enregistre) : ");
        var sb = new StringBuilder();
        ConsoleKeyInfo k;
        while ((k = Console.ReadKey(true)).Key != ConsoleKey.Enter) { if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; } else sb.Append(k.KeyChar); }
        Console.WriteLine();
        return sb.ToString();
    }

    static int Enroll(Args a, string state)
    {
        string? host = a.Opt("host"), user = a.Opt("user");
        if (host == null || user == null) { Console.WriteLine("Usage : enroll --host H --user U [--port 22] [--folder claude-sync] [--pc NOM] [--projets DOSSIER]"); return 2; }
        string tools = a.Opt("tools") ?? DefaultTools();
        string serverDir = a.Opt("server-files") ?? Path.Combine(Base, "serveur");
        var files = new Dictionary<string, byte[]>();
        foreach (string n in new[] { "claudesync-serveur", "claudesync-entree" })
        {
            string p = Path.Combine(serverDir, n);
            if (!File.Exists(p)) { Console.WriteLine("Fichier du serveur introuvable : " + p); return 2; }
            files[n] = File.ReadAllBytes(p);
        }
        string? expected = a.Opt("fingerprint");
        var req = new EnrollRequest
        {
            Host = host, User = user, Port = int.TryParse(a.Opt("port"), out int pt) ? pt : 22, Folder = a.Opt("folder") ?? "claude-sync",
            Pc = a.Opt("pc") ?? Environment.MachineName, ProjetsRoot = a.Opt("projets") ?? @"C:\Projets", ToolsDir = tools, StateDir = state,
            AskpassExe = Assembly.GetExecutingAssembly().Location, ServerFiles = files,
            Progress = s => Console.WriteLine("  - " + s),
            ConfirmFingerprint = fp =>
            {
                Console.WriteLine("Empreinte du serveur : " + fp);
                if (expected != null) return fp.Contains(expected);
                Console.Write("Faire confiance a ce serveur ? (oui/non) : ");
                return (Console.ReadLine() ?? "").Trim().ToLowerInvariant() is "oui" or "o" or "yes" or "y";
            }
        };
        string? adminKey = a.Opt("admin-key");
        if (a.Has("yes")) req.ConfirmFingerprint = fp => { Console.WriteLine("Empreinte acceptee (--yes) : " + fp); return true; };
        EnrollResult r;
        if (adminKey != null)
        {
            // amorcage par la cle d'un administrateur du serveur (essais, installations par un administrateur) : aucun mot de passe
            string known = Path.Combine(state, "ssh", "known_hosts");
            var shell = new Enroller.SshShell(req, known, adminKey, a.Opt("admin-login") ?? ("root@" + host), user);
            r = Enroller.Run(req, shell);
        }
        else
        {
            // le mot de passe n'est demande qu'apres confirmation de l'empreinte : on le lit a la demande de l'amorcage
            req.Password = ReadPassword();
            r = Enroller.Run(req);
        }
        Console.WriteLine(r.Message);
        if (r.Ok) Console.WriteLine("Configuration : " + AppConfig.FileOf(state) + "\nVerifiez avec : ClaudeSync2 ping  puis  ClaudeSync2 sync");
        return r.Ok ? 0 : 1;
    }
}
