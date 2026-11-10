// Claude-Sync V2 : tests de la couche d'integration (J4a) : configuration, mot de passe SSH, inscription, reconnexion, console, et
// (avec --root-key) inscription REELLE de deux PC sur le serveur d'essai puis synchronisation complete entre eux par SSH.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class AppTests
{
    static string tools = "";

    static (int code, string output) RunApp(string args, Dictionary<string, string>? env = null)
    {
        string exe = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "v2app", "ClaudeSync2.exe"));
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(120000);
        return (p.ExitCode, o);
    }

    public static void Run(string? toolsDir, string? enrolDir, string? rootKey)
    {
        PureTests();
        if (toolsDir == null || !File.Exists(Path.Combine(toolsDir, "ssh-keygen.exe"))) { Console.WriteLine("(tests d'inscription ignores : --outils sans ssh-keygen.exe)"); return; }
        tools = toolsDir;
        ToolTests();
        if (enrolDir != null && rootKey != null && File.Exists(rootKey)) Integration(enrolDir, rootKey);
        else Console.WriteLine("(inscription reelle et synchro par SSH ignorees : --enrol et --root-key absents)");
    }

    static void PureTests()
    {
        T.Run("T100 configuration : sauvegarde et relecture, fichier absent ou corrompu", delegate
        {
            string d = Path.Combine(Env.Tmp, "cfg1");
            T.Check("absente -> null", AppConfig.Load(d) == null);
            var c = new AppConfig { Host = "nas.exemple.fr", Port = 2222, User = "léa", Folder = "/srv/cs", Pc = "PC-LÉA", KeyFile = @"C:\Users\léa\k", KnownHosts = @"C:\Users\léa\kh", ToolsDir = @"C:\Prog Files\cs\outils", ProjetsRoot = @"D:\Mes Projets", IndexDir = @"C:\idx", SlowMs = 200, UpdateSource = "https://github.com/x/y", Language = "fr" };
            c.Save(d);
            var r = AppConfig.Load(d);
            T.Check("relue identique (accents, espaces)", r != null && r.Host == c.Host && r.Port == 2222 && r.User == "léa" && r.Pc == "PC-LÉA" && r.ProjetsRoot == @"D:\Mes Projets" && r.IndexDir == @"C:\idx" && r.SlowMs == 200 && r.Language == "fr" && r.UpdateSource == c.UpdateSource);
            T.Check("aucun secret dans le fichier", !File.ReadAllText(AppConfig.FileOf(d)).ToLowerInvariant().Contains("password") && !File.ReadAllText(AppConfig.FileOf(d)).Contains("PRIVATE"));
            File.WriteAllText(AppConfig.FileOf(d), "{pas du json");
            T.Check("corrompue -> null (pas d'exception)", AppConfig.Load(d) == null);
            File.WriteAllText(AppConfig.FileOf(d), "{\"hote\":\"\",\"utilisateur\":\"u\"}");
            T.Check("hote vide -> null", AppConfig.Load(d) == null);
            T.Check("dossier d'etat V2 distinct de celui de la V1", AppConfig.DefaultStateDir.EndsWith("ClaudeSync2"));
        });

        T.Run("T100b dossiers locaux ecrits avec des / (config.json) : le dossier de projets est bien analyse (defaut trouve par l'essai en bac a sable : 0 fichier vu)", delegate
        {
            string root = Path.Combine(Env.Tmp, "projets-slash", "Mes Projets"); Directory.CreateDirectory(Path.Combine(root, "sous")); File.WriteAllText(Path.Combine(root, "sous", "a.txt"), "x");
            string withSlash = root.Replace('\\', '/');
            T.Check("Fs.Long redresse les / (le prefixe long desactive la normalisation de Windows)", Directory.Exists(Fs.Long(withSlash)) && Fs.Long(withSlash).StartsWith(@"\\?\") && !Fs.Long(withSlash).Contains("/"));
            var c = new AppConfig { Host = "h", User = "u", ProjetsRoot = root };
            string d = Path.Combine(Env.Tmp, "cfg-slash"); c.Save(d);
            T.Check("le fichier contient des /, la relecture rend des \\", File.ReadAllText(AppConfig.FileOf(d)).Contains("/Mes Projets") && AppConfig.Load(d)!.ProjetsRoot == root);
            var set = SetDefs.Build(Path.Combine(Env.Tmp, "profil-x"), AppConfig.Load(d)!.ProjetsRoot, null).First(s => s.Name == "projets");
            T.Check("le jeu 'projets' voit le fichier", SetDefs.ScanLocal(set, out _, out int err).ContainsKey("sous/a.txt") && err == 0);
            var set2 = SetDefs.Build(Path.Combine(Env.Tmp, "profil-x"), withSlash, null).First(s => s.Name == "projets");
            T.Check("meme avec un chemin ecrit avec des / (protection supplementaire)", SetDefs.ScanLocal(set2, out _, out _).ContainsKey("sous/a.txt"));
        });

        T.Run("T101 mot de passe SSH : transmis par l'environnement, jamais sur le disque", delegate
        {
            var (code, o) = RunApp("--askpass", new Dictionary<string, string> { ["CS_ASKPASS_VALUE"] = "s3cr&t é" });
            T.Check("le programme renvoie le mot de passe de l'environnement", code == 0 && o.TrimEnd('\r', '\n') == "s3cr&t é", o);
            var (c2, o2) = RunApp("--askpass", new Dictionary<string, string> { ["CS_ASKPASS_VALUE"] = "" });
            T.Check("environnement vide -> ligne vide, pas de plantage", c2 == 0 && o2.Trim() == "");
        });

        T.Run("T106 application residente : demarre avec son icone, s'arrete seule, refuse un second lancement", delegate
        {
            var (c1, o1) = RunApp("run --demo --exit-after 3");
            T.Check("mode demonstration : demarre puis s'arrete proprement (code 0, aucune exception)", c1 == 0 && !o1.Contains("Exception") && !o1.Contains("exception"), "code " + c1 + " " + o1);
            string st = Path.Combine(Env.Tmp, "tray-vide");
            var (c2, o2) = RunApp("run --exit-after 3 --state \"" + st + "\"");
            T.Check("sans configuration : demarre (fenetre de connexion) puis s'arrete, code 0", c2 == 0 && !o2.Contains("Exception"), "code " + c2 + " " + o2);
            var first = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "v2app", "ClaudeSync2.exe")), "run --exit-after 8 --state \"" + st + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            System.Threading.Thread.Sleep(2500);
            var (c3, o3) = RunApp("run --exit-after 3 --state \"" + st + "\"");
            T.Check("second lancement : refuse avec un message (code 20), un seul programme residant", c3 == 20 && o3.Contains("déjà") , "code " + c3 + " " + o3);
            first.WaitForExit(20000);
            T.Check("le premier s'arrete seul", first.HasExited && first.ExitCode == 0);
        });

        T.Run("T102 inscription : analyse du serveur, conseils, ligne de cle restreinte", delegate
        {
            var ok = Enroller.ParseControl("HOME=/home/u\nOS=Linux\nDISTRO=Debian GNU/Linux 12\nOK_rsync=/usr/bin/rsync\nOK_python3=/usr/bin/python3\nPYVER=3.11\nRSYNCVER=rsync  version 3.2.7\nRRSYNC=/usr/bin/rrsync\n");
            T.Check("serveur complet : aucun probleme", Enroller.Problems(ok).Count == 0 && ok["HOME"] == "/home/u");
            var bad = Enroller.ParseControl("HOME=/var/services/homes/u\nOS=Linux\nNAS=Synology\nOK_rsync=/bin/rsync\nMANQUE_python3=1\n");
            var p = Enroller.Problems(bad);
            T.Check("Python et rrsync manquants signales, avec conseil Synology", p.Count == 2 && p[0].Contains("Python 3") && p[0].Contains("Synology") && p[1].Contains("rrsync"), string.Join(" | ", p));
            T.Check("Python trop ancien", Enroller.Problems(Enroller.ParseControl("HOME=/h\nPYVER=3.5\nRRSYNC=/usr/bin/rrsync\n")).Any(x => x.Contains("trop ancien")));
            T.Check("rsync absent", Enroller.Problems(Enroller.ParseControl("HOME=/h\nMANQUE_rsync=1\nPYVER=3.9\nRRSYNC=/x\n")).Any(x => x.Contains("rsync est absent")));
            string line = Enroller.AuthorizedLine("/srv/mon dossier", "/home/u", "ssh-ed25519", "AAAAKEY", "PC-1");
            T.Check("ligne : restrict + commande forcee + dossier protege + commentaire", line == "restrict,command=\"CS_ROOT='/srv/mon dossier' /home/u/.claudesync/claudesync-entree\" ssh-ed25519 AAAAKEY claude-sync:PC-1", line);
            T.Check("dossier simple non quote", Enroller.AuthorizedLine("/srv/cs", "/home/u", "ssh-ed25519", "K", "P").Contains("CS_ROOT=/srv/cs "));
            Action bad1 = () => Enroller.AuthorizedLine("/x\"y", "/h", "t", "k", "p");
            bool threw = false; try { bad1(); } catch (ArgumentException) { threw = true; }
            T.Check("guillemet dans le dossier refuse (injection)", threw);
            bool threw2 = false; try { Enroller.AuthorizedLine("/x", "/h", "t", "k", "PC avec espace"); } catch (ArgumentException) { threw2 = true; }
            T.Check("nom de PC avec espace refuse", threw2);
            T.Check("Shq : apostrophe", Enroller.ShQ("a'b") == "'a'\\''b'");
        });

        T.Run("T103 reconnexion : une session morte est remplacee a l'appel suivant, jamais pendant une synchro", delegate
        {
            string root = Env.NewHub(); int opened = 0;
            using var prov = new HubProvider(() => { opened++; return Env.Open(root); });
            var h1 = prov.Get(); h1.Hello();
            T.Check("meme session reutilisee", ReferenceEquals(h1, prov.Get()) && opened == 1);
            ((HubServeur)h1).Session.Kill();
            System.Threading.Thread.Sleep(300);
            var h2 = prov.Get();
            T.Check("session morte -> nouvelle session fonctionnelle", !ReferenceEquals(h1, h2) && opened == 2 && h2.Hello().Proto == 1);
            T.Check("mesure de liaison", LinkQuality.MeasureMs(h2) >= 0 && LinkQuality.MeasureMs(h2) < 2000);
            ((HubServeur)h2).Session.Kill(); System.Threading.Thread.Sleep(300);
            T.Check("serveur mort : mesure = 9999 (injoignable), pas d'exception", LinkQuality.MeasureMs(h2) == 9999);
            T.Check("seuil de liaison lente", LinkQuality.IsSlow(150, new AppConfig { SlowMs = 120 }) && !LinkQuality.IsSlow(100, new AppConfig { SlowMs = 120 }));
        });
    }

    static void ToolTests()
    {
        T.Run("T104 cle de ce PC : ed25519 sans phrase de passe, reutilisee si elle existe", delegate
        {
            string d = Path.Combine(Env.Tmp, "keys1"); Directory.CreateDirectory(d); string k = Path.Combine(d, "id_ed25519");
            var r = Enroller.GenerateKey(tools, k, "PC-TEST");
            T.Check("cle creee, type ed25519", r.ok && r.pubType == "ssh-ed25519" && r.pubKey.StartsWith("AAAA") && File.Exists(k), r.message);
            T.Check("commentaire = claude-sync:PC", File.ReadAllText(k + ".pub").Trim().EndsWith("claude-sync:PC-TEST"));
            var psi = new ProcessStartInfo(Path.Combine(tools, "ssh-keygen.exe"), "-y -P \"\" -f \"" + k.Replace('\\', '/') + "\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.EnvironmentVariables["MSYS2_ARG_CONV_EXCL"] = "*";
            using var p = Process.Start(psi)!; string o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
            T.Check("la cle n'est PAS protegee par une phrase de passe (usage automatique)", p.ExitCode == 0 && o.StartsWith("ssh-ed25519"), o);
            var r2 = Enroller.GenerateKey(tools, k, "PC-TEST");
            T.Check("2e appel : meme cle, rien n'est ecrase", r2.ok && r2.pubKey == r.pubKey);
        });

        T.Run("T105 inscription : serveur injoignable -> message clair, rien d'ecrit", delegate
        {
            string st = Path.Combine(Env.Tmp, "enrol-ko");
            var req = new EnrollRequest { Host = "127.0.0.1", Port = 1, User = "u", ToolsDir = tools, StateDir = st, Password = "x", ConfirmFingerprint = _ => true };
            var r = Enroller.Run(req);
            T.Check("echec explicite", !r.Ok && r.Message.Contains("injoignable"), r.Message);
            T.Check("aucune configuration ecrite", !File.Exists(AppConfig.FileOf(st)));
        });
    }

    // ------------------------------------------------------------------ inscription REELLE + synchro par SSH
    sealed class RPc : IDisposable
    {
        public readonly string Name, Projets, Profile, State; public readonly AppConfig Cfg; public readonly HubServeur Hub; public Engine2? E; public string LogText = "";
        public RPc(string name, AppConfig cfg, string root)
        {
            Name = name; Cfg = cfg; Projets = Path.Combine(root, "Projets"); Profile = Path.Combine(root, "profil"); State = Path.Combine(root, "etat");
            Directory.CreateDirectory(Projets); Directory.CreateDirectory(Profile);
            Hub = HubServeur.OpenSsh(cfg.ToTarget());
        }
        public int Sync(Action<EngineOptions>? o = null)
        {
            var eo = new EngineOptions { Pc = Name, Profile = Profile, ProjetsRoot = Projets, IndexDir = null, StateDir = State, Apply = true, ClaudeRunning = "no", StableSeconds = 0, RecheckTicks = 0 };
            o?.Invoke(eo);
            E = new Engine2(eo, Hub, Cfg.NewTransport());
            var old = Console.Out; var sw = new StringWriter(); Console.SetOut(sw);
            try { return E.Run(); } finally { Console.SetOut(old); LogText = sw.ToString(); }
        }
        public string P(string rel) => Path.Combine(Projets, rel.Replace('/', '\\'));
        public void Put(string rel, string text) { Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!); File.WriteAllText(P(rel), text, new UTF8Encoding(false)); File.SetLastWriteTimeUtc(P(rel), DateTime.UtcNow.AddHours(-1)); }
        public string? Read(string rel) => File.Exists(P(rel)) ? File.ReadAllText(P(rel)) : null;
        public bool OnHub(string rel) => Hub.Stat("projets", new[] { rel })[rel] != null;
        public void Dispose() { try { Hub.Dispose(); } catch (Exception) { } }
    }

    public static string HubSibling(string folder, string name) { string f = folder.TrimEnd('/'); int i = f.LastIndexOf('/'); return (i > 0 ? f.Substring(0, i) : f) + "/" + name; }

    static void Integration(string enrolDir, string rootKey)
    {
        var baseCfg = AppConfig.Load(enrolDir);
        if (baseCfg == null) { Console.WriteLine("(--enrol sans config.json : integration ignoree)"); return; }
        string hubDir = HubSibling(baseCfg.Folder, "hub-e2e");      // a cote du hub de l'inscription d'essai (meme dossier parent sur le serveur)
        string serverDir = Path.GetDirectoryName(Env.Script)!;
        var files = new Dictionary<string, byte[]> { ["claudesync-serveur"] = File.ReadAllBytes(Path.Combine(serverDir, "claudesync-serveur")), ["claudesync-entree"] = File.ReadAllBytes(Path.Combine(serverDir, "claudesync-entree")) };
        var cfgs = new Dictionary<string, AppConfig>(); var roots = new Dictionary<string, string>();
        Console.WriteLine("Integration reelle : serveur " + baseCfg.User + "@" + baseCfg.Host + ", hub d'essai " + hubDir);

        T.Run("T110 inscription REELLE de deux PC sur le serveur d'essai (amorcage administrateur d'essai)", delegate
        {
            foreach (string pc in new[] { "PC-E2E-A", "PC-E2E-B" })
            {
                string root = Path.Combine(Env.Tmp, "e2e-" + pc); roots[pc] = root;
                var steps = new List<string>();
                var req = new EnrollRequest { Host = baseCfg.Host, Port = baseCfg.Port, User = baseCfg.User, Folder = hubDir, Pc = pc, ToolsDir = tools, StateDir = Path.Combine(root, "etat"), ProjetsRoot = Path.Combine(root, "Projets"), ServerFiles = files, Progress = steps.Add, ConfirmFingerprint = fp => fp.Contains("SHA256:") };
                string known = Path.Combine(req.StateDir, "ssh", "known_hosts");
                var shell = new Enroller.SshShell(req, known, rootKey, "root@" + baseCfg.Host, baseCfg.User);
                var r = Enroller.Run(req, shell);
                // hub d'essai propre (donnees de test uniquement) : apres l'inscription, quand la cle du serveur est memorisee
                if (pc == "PC-E2E-B" && r.Ok) shell.RunScript("cd " + hubDir + " && rm -rf projets _corbeille _anciennes claude desktop .rsync-partial && mkdir -p .rsync-tmp && rm -rf .rsync-tmp/* ; true");
                T.Check(pc + " : inscription reussie", r.Ok, r.Message + " | " + string.Join(", ", steps));
                if (r.Ok) { cfgs[pc] = r.Config!; }
                T.Check(pc + " : les 6 etapes ont ete parcourues", steps.Count == 6, string.Join(", ", steps));
            }
            T.Check("deux cles differentes", cfgs.Count == 2 && cfgs["PC-E2E-A"].KeyFile != cfgs["PC-E2E-B"].KeyFile && File.ReadAllText(cfgs["PC-E2E-A"].KeyFile + ".pub") != File.ReadAllText(cfgs["PC-E2E-B"].KeyFile + ".pub"));
            T.Check("configuration ecrite et relisible", cfgs.Count == 2 && AppConfig.Load(Path.Combine(roots["PC-E2E-A"], "etat"))!.Folder == hubDir);
        });
        if (cfgs.Count != 2) return;

        T.Run("T111 synchronisation COMPLETE entre deux PC par SSH reel : propagation, modification, suppression, verrou", delegate
        {
            using var a = new RPc("PC-E2E-A", cfgs["PC-E2E-A"], roots["PC-E2E-A"]); using var b = new RPc("PC-E2E-B", cfgs["PC-E2E-B"], roots["PC-E2E-B"]);
            int ms = LinkQuality.MeasureMs(a.Hub);
            T.Check("liaison mesuree (0 ms possible sur un reseau local)", ms >= 0 && ms < 2000, ms + " ms");
            a.Put("a.txt", "A1"); a.Put("d/é b.txt", "E1"); a.Put("sp ace/c.txt", "C1");
            int codeA = a.Sync();
            T.Check("A envoie 3 fichiers (code 0)", codeA == 0 && a.E!.P.DoneFiles == 3 && a.OnHub("d/é b.txt"), "code " + codeA + " | " + a.E!.Summary + " | fichiers " + a.E.P.DoneFiles + " erreurs " + a.E.Errors + " | " + a.LogText);
            T.Check("B recupere (contenus exacts)", b.Sync() == 0 && b.Read("a.txt") == "A1" && b.Read("d/é b.txt") == "E1" && b.Read("sp ace/c.txt") == "C1", b.LogText);
            b.Put("a.txt", "B2 modifie plus long");
            T.Check("B envoie sa modification, A la recupere", b.Sync() == 0 && a.Sync() == 0 && a.Read("a.txt") == "B2 modifie plus long", a.LogText + b.LogText);
            File.Delete(a.P("sp ace/c.txt"));
            T.Check("A supprime : corbeille du serveur puis chez B", a.Sync() == 0 && !a.OnHub("sp ace/c.txt") && b.Sync() == 0 && b.Read("sp ace/c.txt") == null, a.LogText + b.LogText);
            T.Check("la corbeille du serveur liste la suppression (par PC-E2E-A)", a.E!.ListEntries().Any(e => e.Pc == "PC-E2E-A"));
            using var x = HubServeur.OpenSsh(cfgs["PC-E2E-A"].ToTarget());
            T.Check("un 3e acces prend le verrou", x.AcquireLock("AUTRE").Ok);
            T.Check("B refuse (code 5)", b.Sync() == 5 && b.E!.Summary.Contains("AUTRE"), b.E!.Summary);
            x.ReleaseLock();
            T.Check("verrou rendu : B synchronise", b.Sync() == 0);
            T.Check("stable ensuite", a.Sync() == 0 && a.E!.P.TotalFiles == 0 && b.Sync() == 0 && b.E!.P.TotalFiles == 0);
        });

        T.Run("T112 console ClaudeSync2.exe sur un FAUX profil : ping, simulation, synchro, conflits, corbeille", delegate
        {
            string root = roots["PC-E2E-B"]; string st = Path.Combine(root, "etat"); string fake = Path.Combine(root, "faux-profil"); Directory.CreateDirectory(fake);
            var env = new Dictionary<string, string> { ["USERPROFILE"] = fake };
            string tl = tools.Replace('\\', '/');
            var cfg = AppConfig.Load(st)!; cfg.ToolsDir = tools; cfg.Save(st);
            var (c1, o1) = RunApp("ping --state \"" + st + "\"", env);
            T.Check("ping : liaison affichee", c1 == 0 && o1.Contains("Liaison :"), o1);
            var (c2, o2) = RunApp("sync --state \"" + st + "\"", env);
            T.Check("simulation (sans --apply) : rien n'est ecrit", c2 == 0 && o2.Contains("SIMULATION"), o2);
            var (c3, o3) = RunApp("sync --apply --claude no --state \"" + st + "\"", env);
            T.Check("synchro reelle : code 0, resume affiche", c3 == 0 && o3.Contains("Termine"), o3);
            var (c4, o4) = RunApp("conflicts --state \"" + st + "\"", env);
            T.Check("conflits : aucun", c4 == 0 && o4.Contains("Aucun conflit"), o4);
            var (c5, o5) = RunApp("trash --state \"" + st + "\"", env);
            T.Check("corbeille : la suppression de T111 est listee", c5 == 0 && o5.Contains("PC-E2E-A"), o5);
            var (c6, o6) = RunApp("sync --apply --state \"" + Path.Combine(root, "inconnu") + "\"", env);
            T.Check("non configure : message clair, code 10", c6 == 10 && o6.Contains("n'est pas configure"), o6);
        });
    }
}
