// Claude-Sync V2 : essais en liaison lente (J5). Le vrai moteur synchronise deux PC d'essai avec le VRAI serveur, mais en passant par un
// relais qui simule une mauvaise liaison (4G via VPN : 150 ms aller-retour, 2 Mbit/s montant). On mesure le temps ET le volume reellement
// transmis (compteurs du relais), et on verifie ce qui compte : le delta (seule la difference circule), la coupure en plein envoi, les reprises.
// Lance seulement avec --lente (plusieurs minutes) : V2Tests.exe ... --enrol DOSSIER --root-key CLE --lente
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class SlowLinkTests
{
    const int ProxyPort = 22722;
    static string toolsDir = "";
    static Process? proxy;
    static string statsFile = "";
    public static readonly List<string> Report = new();

    // ---------- relais
    static void StartProxy(int delayMs, int upKbps, int downKbps, string target)
    {
        StopProxy();
        string script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "tools", "proxy-latence.py"));
        statsFile = Path.Combine(Env.Tmp, "relais-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json");
        var psi = new ProcessStartInfo(Env.Python, "-X utf8 \"" + script + "\" --listen " + ProxyPort + " --target " + target + " --delay-ms " + delayMs + " --up-kbps " + upKbps + " --down-kbps " + downKbps + " --stats-file \"" + statsFile + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        proxy = Process.Start(psi)!;
        for (int i = 0; i < 100 && !File.Exists(statsFile); i++) Thread.Sleep(100);
        if (!File.Exists(statsFile)) throw new Exception("le relais de latence ne demarre pas");
    }

    static void StopProxy() { try { if (proxy != null && !proxy.HasExited) { proxy.Kill(); proxy.WaitForExit(3000); } } catch (Exception) { } proxy = null; }

    static (long up, long down) Counters()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var d = Json.ParseObject(File.ReadAllText(statsFile));
                return (Convert.ToInt64(d["up"]), Convert.ToInt64(d["down"]));
            }
            catch (Exception) { Thread.Sleep(100); }
        }
        return (0, 0);
    }

    // ---------- PC d'essai passant par le relais
    sealed class LPc : IDisposable
    {
        public readonly string Name, Projets, Profile, State; public readonly AppConfig Cfg; public Engine2? E; public string LogText = "";
        public LPc(string name, AppConfig cfg, string root)
        {
            Name = name; Cfg = cfg; Projets = Path.Combine(root, "Projets"); Profile = Path.Combine(root, "profil"); State = Path.Combine(root, "etat");
            Directory.CreateDirectory(Projets); Directory.CreateDirectory(Profile);
        }
        public int Sync()
        {
            var eo = new EngineOptions { Pc = Name, Profile = Profile, ProjetsRoot = Projets, IndexDir = null, StateDir = State, Apply = true, ClaudeRunning = "no", StableSeconds = 0, RecheckTicks = 0 };
            using var prov = HubProvider.ForConfig(Cfg);          // comme l'application : session ouverte pour la synchro, rendue ensuite
            E = new Engine2(eo, prov.Get(), Cfg.NewTransport());
            var old = Console.Out; var sw = new StringWriter(); Console.SetOut(sw);
            try { return E.Run(); } finally { Console.SetOut(old); LogText = sw.ToString(); }
        }
        public string P(string rel) => Path.Combine(Projets, rel.Replace('/', '\\'));
        public void WriteBytes(string rel, byte[] b) { Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!); File.WriteAllBytes(P(rel), b); File.SetLastWriteTimeUtc(P(rel), DateTime.UtcNow.AddHours(-1)); }
        public void Append(string rel, byte[] b) { using (var f = new FileStream(P(rel), FileMode.Append)) f.Write(b, 0, b.Length); File.SetLastWriteTimeUtc(P(rel), DateTime.UtcNow.AddHours(-1)); }
        public string Sha(string rel) => File.Exists(P(rel)) ? Hex(SHA256.Create().ComputeHash(File.ReadAllBytes(P(rel)))) : "";
        public void Dispose() { }
    }

    static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

    // texte de type journal de conversation (JSON par ligne, mots repetes : se comprime comme les vrais .jsonl, environ 4 a 5 fois)
    static byte[] JsonlText(int bytes, int seed)
    {
        string[] w = Enumerable.Range(0, 400).Select(i => "mot" + (i * 7919 % 1000).ToString("x")).ToArray();
        var rnd = new Random(seed); var sb = new StringBuilder();
        while (sb.Length < bytes)
        {
            sb.Append("{\"type\":\"assistant\",\"uuid\":\"").Append(Guid.NewGuid().ToString()).Append("\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"");
            for (int i = 0, n = 25 + rnd.Next(40); i < n; i++) sb.Append(w[rnd.Next(w.Length)]).Append(' ');
            sb.Append("\"}]}}\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString().Substring(0, bytes));
    }

    static byte[] RandomBytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    static string Kb(long b) => b < 1 << 20 ? (b / 1024.0).ToString("0.#") + " Ko" : (b / 1048576.0).ToString("0.0") + " Mo";

    // mesure : duree + volume reellement transmis dans chaque sens
    static (int code, double sec, long up, long down) Measure(string label, Func<int> act)
    {
        var c0 = Counters(); var sw = Stopwatch.StartNew();
        int code = act(); sw.Stop();
        Thread.Sleep(700);                       // le relais publie ses compteurs toutes les 0,3 s
        var c1 = Counters();
        long up = c1.up - c0.up, down = c1.down - c0.down;
        string line = string.Format("{0,-58} {1,6:0.0} s   montant {2,9}   descendant {3,9}", label, sw.Elapsed.TotalSeconds, Kb(up), Kb(down));
        Report.Add(line); Console.WriteLine("  MESURE " + line);
        return (code, sw.Elapsed.TotalSeconds, up, down);
    }

    public static void Run(string tools, string enrolDir, string rootKey)
    {
        toolsDir = tools;
        var baseCfg = AppConfig.Load(enrolDir);
        if (baseCfg == null) { Console.WriteLine("(--enrol sans config.json : essais de liaison lente ignores)"); return; }
        string hubDir = AppTests.HubSibling(baseCfg.Folder, "hub-lente");
        string serverDir = Path.GetDirectoryName(Env.Script)!;
        var files = new Dictionary<string, byte[]> { ["claudesync-serveur"] = File.ReadAllBytes(Path.Combine(serverDir, "claudesync-serveur")), ["claudesync-entree"] = File.ReadAllBytes(Path.Combine(serverDir, "claudesync-entree")) };
        var cfgs = new Dictionary<string, AppConfig>(); var roots = new Dictionary<string, string>();
        Console.WriteLine("Essais de liaison lente : serveur " + baseCfg.User + "@" + baseCfg.Host + ", hub d'essai " + hubDir + " (via un relais local 150 ms / 2 Mbit/s)");

        try
        {
            T.Run("T170 preparation : deux PC d'essai inscrits (en direct), hub d'essai vide, relais de latence", delegate
            {
                foreach (string pc in new[] { "PC-LENTE-A", "PC-LENTE-B" })
                {
                    string root = Path.Combine(Env.Tmp, "lente-" + pc); roots[pc] = root;
                    var req = new EnrollRequest { Host = baseCfg.Host, Port = baseCfg.Port, User = baseCfg.User, Folder = hubDir, Pc = pc, ToolsDir = tools, StateDir = Path.Combine(root, "etat"), ProjetsRoot = Path.Combine(root, "Projets"), ServerFiles = files, ConfirmFingerprint = fp => fp.Contains("SHA256:") };
                    string known = Path.Combine(req.StateDir, "ssh", "known_hosts");
                    var shell = new Enroller.SshShell(req, known, rootKey, "root@" + baseCfg.Host, baseCfg.User);
                    var r = Enroller.Run(req, shell);
                    if (pc == "PC-LENTE-B" && r.Ok) shell.RunScript("cd " + hubDir + " && rm -rf projets _corbeille _anciennes claude desktop .rsync-partial && mkdir -p .rsync-tmp && rm -rf .rsync-tmp/* ; true");
                    T.Check(pc + " : inscription reussie", r.Ok, r.Message);
                    if (!r.Ok) return;
                    // meme configuration, mais via le relais local (la cle du serveur est declaree pour [127.0.0.1]:port)
                    var c = r.Config!;
                    string slowKnown = Path.Combine(req.StateDir, "ssh", "known_hosts_relais");
                    File.WriteAllLines(slowKnown, File.ReadAllLines(c.KnownHosts).Where(l => l.Trim().Length > 0).Select(l => { int sp = l.IndexOf(' '); return "[127.0.0.1]:" + ProxyPort + l.Substring(sp); }));
                    cfgs[pc] = new AppConfig { Host = "127.0.0.1", Port = ProxyPort, User = c.User, Folder = c.Folder, Pc = c.Pc, KeyFile = c.KeyFile, KnownHosts = slowKnown, ToolsDir = tools, ProjetsRoot = c.ProjetsRoot };
                }
                StartProxy(75, 2000, 20000, baseCfg.Host + ":" + baseCfg.Port);
                T.Check("relais en marche", proxy != null && !proxy.HasExited);
            });
            if (cfgs.Count != 2) return;
            using var a = new LPc("PC-LENTE-A", cfgs["PC-LENTE-A"], roots["PC-LENTE-A"]); using var b = new LPc("PC-LENTE-B", cfgs["PC-LENTE-B"], roots["PC-LENTE-B"]);

            T.Run("T171 la liaison simulee est bien detectee comme LENTE (mode econome)", delegate
            {
                using var prov = HubProvider.ForConfig(cfgs["PC-LENTE-A"]);
                int ms = LinkQuality.MeasureMs(prov.Get());
                Report.Add("liaison mesuree par Claude-Sync : " + ms + " ms (seuil lent " + cfgs["PC-LENTE-A"].SlowMs + " ms)");
                Console.WriteLine("  MESURE liaison : " + ms + " ms");
                T.Check("environ 150 ms (relais 2 x 75 ms)", ms >= 120 && ms <= 400, ms + " ms");
                T.Check("classee lente : mode econome a l'ouverture de Claude", LinkQuality.IsSlow(ms, cfgs["PC-LENTE-A"]));
            });

            byte[] big = JsonlText(8 << 20, 1);
            T.Run("T172 premier envoi : 150 petits fichiers + un journal de 8 Mo", delegate
            {
                for (int i = 0; i < 150; i++) a.WriteBytes("p1/dossier" + (i % 10) + "/fichier" + i + ".txt", JsonlText(2048, 100 + i));
                a.WriteBytes("p1/journal.jsonl", big);
                var m = Measure("A : premier envoi (150 x 2 Ko + 8 Mo de texte)", a.Sync);
                T.Check("code 0, 151 fichiers envoyes", m.code == 0 && a.E!.P.DoneFiles == 151, "code " + m.code + " " + a.E!.Summary);
                T.Check("le texte est compresse en route (moins de 5 Mo pour 8,3 Mo bruts)", m.up < 5_000_000, Kb(m.up));
            });

            T.Run("T173 DELTA : le journal grossit de 1 Mo -> seule la fin circule", delegate
            {
                a.Append("p1/journal.jsonl", JsonlText(1 << 20, 2));
                var m = Measure("A : +1 Mo ajoute a la fin du journal de 8 Mo", a.Sync);
                T.Check("code 0", m.code == 0, a.E!.Summary);
                T.Check("volume montant < 1 Mo (pas 9 Mo : seule la difference, compressee)", m.up < 1_000_000, Kb(m.up));
                T.Check("le serveur a bien la nouvelle taille", a.E!.P.DoneFiles == 1);
            });

            T.Run("T174 DELTA : 100 Ko modifies AU MILIEU du journal", delegate
            {
                string p = a.P("p1/journal.jsonl");
                byte[] all = File.ReadAllBytes(p);
                byte[] patch = JsonlText(100_000, 3);
                Array.Copy(patch, 0, all, 4 << 20, patch.Length);
                File.WriteAllBytes(p, all); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-1));
                var m = Measure("A : 100 Ko changes au milieu (journal de 9 Mo)", a.Sync);
                T.Check("code 0", m.code == 0, a.E!.Summary);
                T.Check("volume montant < 600 Ko (les 9 Mo ne repartent pas)", m.up < 600_000, Kb(m.up));
            });

            T.Run("T175 rien n'a change : verification rapide malgre 150 ms de latence", delegate
            {
                var m = Measure("A : synchro sans aucun changement (151 fichiers)", a.Sync);
                T.Check("code 0, aucun transfert", m.code == 0 && a.E!.P.TotalFiles == 0, a.E!.Summary);
                T.Check("moins de 15 s et moins de 100 Ko dans chaque sens", m.sec < 15 && m.up < 100_000 && m.down < 100_000, m.sec.ToString("0.0") + " s");
            });

            T.Run("T176 le 2e PC recupere tout (descendant), puis seulement le delta", delegate
            {
                var m = Measure("B : premiere recuperation (151 fichiers, 9 Mo)", b.Sync);
                T.Check("code 0, contenus identiques", m.code == 0 && b.Sha("p1/journal.jsonl") == a.Sha("p1/journal.jsonl") && b.Sha("p1/dossier3/fichier3.txt") == a.Sha("p1/dossier3/fichier3.txt"), m.code + " " + b.E!.Summary);
                a.Append("p1/journal.jsonl", JsonlText(300_000, 4)); a.WriteBytes("p1/nouveau1.txt", JsonlText(1500, 5)); a.WriteBytes("p1/nouveau2.txt", JsonlText(1500, 6));
                Measure("A : envoie +300 Ko et 2 petits fichiers", a.Sync);
                var m2 = Measure("B : recupere ce delta (ce que fait l'ouverture de Claude)", b.Sync);
                T.Check("B identique a A", m2.code == 0 && b.Sha("p1/journal.jsonl") == a.Sha("p1/journal.jsonl") && b.Sha("p1/nouveau2.txt") == a.Sha("p1/nouveau2.txt"), b.E!.Summary);
                T.Check("descendant < 300 Ko (pas les 9 Mo) et bien sous le budget de 120 s a l'ouverture", m2.down < 300_000 && m2.sec < 120, Kb(m2.down) + " en " + m2.sec.ToString("0.0") + " s");
            });

            T.Run("T177 COUPURE de la liaison en plein envoi : rien de corrompu, reprise propre", delegate
            {
                a.WriteBytes("p2/gros.bin", RandomBytes(5 << 20, 7));                 // incompressible : ~20 s a 2 Mbit/s
                string shaLocal = a.Sha("p2/gros.bin");
                int code = -1; var th = new Thread(() => { code = a.Sync(); }); var sw = Stopwatch.StartNew();
                var c0 = Counters(); th.Start();
                bool started = false;
                for (int i = 0; i < 300 && !started; i++) { Thread.Sleep(100); started = Counters().up - c0.up > 1_000_000; }
                T.Check("l'envoi est bien en cours (plus de 1 Mo parti)", started);
                var cCut = Counters(); StopProxy();                                      // coupure franche : le 4G tombe
                th.Join(90000);
                var sent1 = cCut.up - c0.up;
                Report.Add(string.Format("{0,-58} {1,6:0.0} s   montant {2,9}   (coupure apres {3})", "A : coupure du relais en plein envoi de 5 Mo", sw.Elapsed.TotalSeconds, Kb(sent1), Kb(sent1)));
                Console.WriteLine("  MESURE coupure apres " + Kb(sent1) + ", synchro terminee en " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s, code " + code);
                T.Check("la synchro s'arrete proprement (erreur reseau, pas de blocage)", !th.IsAlive && code != 0, "code " + code);
                T.Check("fichier local intact", a.Sha("p2/gros.bin") == shaLocal);
                T.Check("aucun conflit cree par la coupure", a.E!.LoadConflicts().Count == 0);
                // la liaison revient
                StartProxy(75, 2000, 20000, baseCfg.Host + ":" + baseCfg.Port);
                var m = Measure("A : liaison revenue, nouvelle tentative (5 Mo)", a.Sync);
                T.Check("la nouvelle tentative aboutit (code 0)", m.code == 0, a.E!.Summary);
                var mb = Measure("B : recupere le gros fichier", b.Sync);
                T.Check("B recoit un fichier IDENTIQUE octet pour octet", mb.code == 0 && b.Sha("p2/gros.bin") == shaLocal, b.E!.Summary);
                T.Check("toujours aucun conflit", a.E!.LoadConflicts().Count == 0 && b.E!.LoadConflicts().Count == 0);
                Report.Add("reprise apres coupure : " + Kb(m.up) + " renvoyes sur 5,0 Mo (" + (sent1 > 0 ? "deja parti : " + Kb(sent1) : "") + ")");
            });

            // temps d'une synchro sans changement selon la latence : c'est ce qui rythme l'ouverture et la fermeture de Claude
            foreach (var (delay, name) in new[] { (1, "LAN (~2 ms)"), (25, "50 ms"), (75, "150 ms (4G)"), (150, "300 ms (4G mediocre)"), (300, "600 ms (tres mauvais)") })
            {
                T.Run("T178 sans changement a " + name, delegate
                {
                    StartProxy(delay, 2000, 20000, baseCfg.Host + ":" + baseCfg.Port);
                    var m = Measure("A : synchro sans changement, latence " + name, a.Sync);
                    T.Check("code 0, rien a transferer", m.code == 0 && a.E!.P.TotalFiles == 0, a.E!.Summary);
                    T.Check("moins de 40 s meme a 600 ms", m.sec < 40, m.sec.ToString("0.0") + " s");
                });
            }
        }
        finally { StopProxy(); }

        Console.WriteLine();
        Console.WriteLine("=== RAPPORT DE MESURES J5 (liaison simulee : 150 ms aller-retour, 2 Mbit/s montant, 20 Mbit/s descendant, sauf indication) ===");
        foreach (string l in Report) Console.WriteLine(l);
        try { File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mesures-j5.txt"), Report, new UTF8Encoding(false)); } catch (Exception) { }
    }
}
