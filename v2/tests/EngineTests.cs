// Claude-Sync V2 : tests de bout en bout du moteur (jalon J3). DEUX PC simules (dossiers, etat et session serveur separes) autour
// d'un meme hub : le VRAI programme serveur (python, en local) et le VRAI rsync (mode local). Aucun reseau, aucun NAS.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

sealed class Tap : ITransfer                      // enveloppe du transport : permet de jouer des incidents entre deux etapes
{
    readonly ITransfer inner;
    public Action<string, IList<string>>? Before, After;
    public Tap(ITransfer i) { inner = i; }
    public Action<long> OnBytes { get => inner.OnBytes; set => inner.OnBytes = value; }
    public Func<bool> Cancel { get => inner.Cancel; set => inner.Cancel = value; }
    public TransferResult Push(string localRoot, IList<string> rels, string hubDir, string hubBackupDir)
    {
        Before?.Invoke(localRoot, rels);
        var r = inner.Push(localRoot, rels, hubDir, hubBackupDir);
        After?.Invoke(localRoot, rels);
        return r;
    }
    public TransferResult Pull(string hubDir, IList<string> rels, string localRoot, string localBackupDir) => inner.Pull(hubDir, rels, localRoot, localBackupDir);
}

sealed class Pc : IDisposable
{
    static int n;
    public readonly string Name, Projets, Profile, State, HubRoot;
    public HubServeur Hub;
    public ITransfer Tx;
    public Engine2? E;
    public string LogText = "";

    public Pc(string name, string hubRoot, string tools)
    {
        Name = name; HubRoot = hubRoot;
        string root = Path.Combine(Env.Tmp, "pc" + (++n) + "-" + name);
        Projets = Path.Combine(root, "Projets"); Profile = Path.Combine(root, "profil"); State = Path.Combine(root, "etat");
        Directory.CreateDirectory(Projets); Directory.CreateDirectory(Profile);
        Hub = Env.Open(hubRoot);
        Tx = new RsyncTransport(new RsyncOptions { ToolsDir = tools, TimeoutSeconds = 30 }, new RsyncRemote { LocalHubRoot = hubRoot });
    }

    public int Sync(Action<EngineOptions>? cfg = null, Action<Engine2>? pre = null)
    {
        var o = new EngineOptions { Pc = Name, Profile = Profile, ProjetsRoot = Projets, IndexDir = null, StateDir = State, Apply = true, ClaudeRunning = "no", StableSeconds = 0, RecheckTicks = 0 };
        cfg?.Invoke(o);
        E = new Engine2(o, Hub, Tx);
        pre?.Invoke(E);
        var old = Console.Out; var sw = new StringWriter(); Console.SetOut(sw);
        try { return E.Run(); } finally { Console.SetOut(old); LogText = sw.ToString(); }
    }

    public string P(string rel) => Path.Combine(Projets, rel.Replace('/', '\\'));
    public void Put(string rel, string text, DateTime? mtime = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(P(rel), mtime ?? DateTime.UtcNow.AddHours(-1));
    }
    public string? Read(string rel) => File.Exists(P(rel)) ? File.ReadAllText(P(rel)) : null;
    public string? HubRead(string rel) { string p = Path.Combine(HubRoot, "projets", rel.Replace('/', '\\')); return File.Exists(p) ? File.ReadAllText(p) : null; }
    public IEnumerable<string> Files(string dir) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>();
    public void Dispose() { try { Hub.Dispose(); } catch (Exception) { } }
}

static class EngineTests
{
    static string tools = "";

    // Deux PC deja synchronises sur trois fichiers.
    static (string hub, Pc a, Pc b) Duo(params (string rel, string text)[] files)
    {
        string hub = Env.NewHub();
        var a = new Pc("PC-A", hub, tools); var b = new Pc("PC-B", hub, tools);
        foreach (var (rel, text) in files.Length > 0 ? files : new[] { ("a.txt", "A1"), ("d/é b.txt", "E1"), ("sp ace/c.txt", "C1") }) a.Put(rel, text);
        a.Sync(); b.Sync();
        return (hub, a, b);
    }

    static string? FindFile(string root, string name) => Directory.Exists(root) ? Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault() : null;

    public static void Run(string? toolsDir)
    {
        if (toolsDir == null || !File.Exists(Path.Combine(toolsDir, "rsync.exe"))) { Console.WriteLine("(tests du moteur ignores : --outils absent)"); return; }
        tools = toolsDir;

        T.Run("T80 premiere synchro : A envoie tout, B recupere tout, puis plus rien a faire", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools); using var b = new Pc("PC-B", hub, tools);
            a.Put("a.txt", "A1"); a.Put("d/é b.txt", "E1"); a.Put("sp ace/c.txt", "C1");
            T.Check("A : code 0", a.Sync() == 0, a.E!.Summary);
            T.Check("A : 3 fichiers envoyes", a.E!.P.DoneFiles == 3 && a.E.Errors == 0, a.LogText);
            T.Check("le serveur a les 3 fichiers, contenus exacts", a.HubRead("a.txt") == "A1" && a.HubRead("d/é b.txt") == "E1" && a.HubRead("sp ace/c.txt") == "C1");
            T.Check("B : code 0", b.Sync() == 0, b.E!.Summary);
            T.Check("B : 3 fichiers recuperes, contenus exacts", b.Read("a.txt") == "A1" && b.Read("d/é b.txt") == "E1" && b.Read("sp ace/c.txt") == "C1", b.LogText);
            T.Check("dates conservees (2 s)", Math.Abs((File.GetLastWriteTimeUtc(b.P("a.txt")) - File.GetLastWriteTimeUtc(a.P("a.txt"))).TotalSeconds) <= 2);
            T.Check("A rejoue : rien a envoyer ni recuperer", a.Sync() == 0 && a.E!.P.TotalFiles == 0 && a.E.Plan.All(i => i.Kind == Act.Same));
            T.Check("B rejoue : rien a faire", b.Sync() == 0 && b.E!.P.TotalFiles == 0);
            T.Check("verrou libere apres chaque synchro", a.Hub.AcquireLock("X").Ok);
        });

        T.Run("T81 modification dans un sens puis l'autre, avec archives des deux cotes", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                b.Put("a.txt", "B2 modifie", DateTime.UtcNow.AddMinutes(-30));
                T.Check("B envoie", b.Sync() == 0 && b.HubRead("a.txt") == "B2 modifie", b.LogText);
                string? arch = FindFile(Path.Combine(hub, "_anciennes"), "a.txt");
                T.Check("ancienne version archivee sur le serveur", arch != null && File.ReadAllText(arch) == "A1");
                T.Check("A recupere", a.Sync() == 0 && a.Read("a.txt") == "B2 modifie", a.LogText);
                string? la = FindFile(Path.Combine(a.State, "anciennes"), "a.txt");
                T.Check("ancienne version archivee chez A", la != null && File.ReadAllText(la) == "A1");
                T.Check("stable ensuite", a.Sync() == 0 && a.E!.P.TotalFiles == 0 && b.Sync() == 0 && b.E!.P.TotalFiles == 0);
            }
        });

        T.Run("T82 suppression : corbeille du serveur puis corbeille locale de l'autre PC", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                File.Delete(a.P("d/é b.txt"));
                T.Check("A : code 0, 1 mis a la corbeille", a.Sync() == 0 && a.E!.Trashed == 1, a.LogText);
                T.Check("fichier parti du serveur", a.HubRead("d/é b.txt") == null);
                string? t = FindFile(Path.Combine(hub, "_corbeille"), "é b.txt");
                T.Check("dans _corbeille du serveur, avec _par.txt = PC-A", t != null && File.ReadAllText(Path.Combine(Path.GetDirectoryName(t)!, "..", "..", "_par.txt")) == "PC-A", t);
                T.Check("B : le fichier disparait, corbeille locale", b.Sync() == 0 && b.Read("d/é b.txt") == null && FindFile(Path.Combine(b.State, "corbeille"), "é b.txt") != null, b.LogText);
                T.Check("dossier vide nettoye chez B", !Directory.Exists(b.P("d")));
                T.Check("stable ensuite", a.Sync() == 0 && b.Sync() == 0 && a.E!.P.TotalFiles == 0);
            }
        });

        T.Run("T83 supprime ici mais modifie ailleurs : la modification l'emporte", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                File.Delete(a.P("a.txt"));
                b.Put("a.txt", "B modifie plus long", DateTime.UtcNow.AddMinutes(-20));
                T.Check("B envoie sa modification", b.Sync() == 0 && b.HubRead("a.txt") == "B modifie plus long");
                T.Check("A : le fichier supprime est recupere (pas de suppression)", a.Sync() == 0 && a.Read("a.txt") == "B modifie plus long" && a.E!.Trashed == 0, a.LogText);
            }
        });

        T.Run("T84 conflit : rien n'est ecrase, puis resolution 'both', 'hub' et 'local'", delegate
        {
            var (hub, a, b) = Duo(("f1.txt", "base"), ("f2.txt", "base"), ("f3.txt", "base")); using (a) using (b)
            {
                foreach (var f in new[] { "f1.txt", "f2.txt", "f3.txt" }) { a.Put(f, "version A " + f, DateTime.UtcNow.AddMinutes(-30)); b.Put(f, "version B " + f + "!", DateTime.UtcNow.AddMinutes(-20)); }
                T.Check("A envoie ses 3 versions", a.Sync() == 0);
                int code = b.Sync();
                T.Check("B : conflits signales (code 1), 3 conflits", code == 1 && b.E!.Conflicts.Count == 3, "code " + code + " " + b.E!.Conflicts.Count);
                T.Check("rien n'est ecrase des deux cotes", b.Read("f1.txt") == "version B f1.txt!" && b.HubRead("f1.txt") == "version A f1.txt");
                T.Check("conflits memorises", new StateStore(b.State).LoadConflicts().Count == 3);
                string r1 = b.E!.Resolve(1, "both");
                T.Check("both : resolu", r1.StartsWith("Conflit resolu"), r1);
                T.Check("both : le serveur a la version locale, l'autre est gardee a cote et archivee", b.HubRead("f1.txt") == "version B f1.txt!" && b.Files(b.Projets).Any(p => Path.GetFileName(p).StartsWith("f1.conflit-") && File.ReadAllText(p) == "version A f1.txt") && FindFile(Path.Combine(hub, "_anciennes"), "f1.txt") != null);
                string r2 = b.E.Resolve(1, "hub");
                T.Check("hub : la version du serveur remplace la locale (archivee chez B)", r2.StartsWith("Conflit resolu") && b.Read("f2.txt") == "version A f2.txt" && FindFile(Path.Combine(b.State, "anciennes"), "f2.txt") != null, r2);
                string r3 = b.E.Resolve(1, "local");
                T.Check("local : la version locale remplace celle du serveur (archivee)", r3.StartsWith("Conflit resolu") && b.HubRead("f3.txt") == "version B f3.txt!", r3);
                T.Check("plus aucun conflit", new StateStore(b.State).LoadConflicts().Count == 0);
                T.Check("B resynchronise : le fichier 'conflit' part vers le serveur, sans erreur", b.Sync() == 0 && b.E!.Errors == 0);
                T.Check("A recupere le fichier 'conflit'", a.Sync() == 0 && a.Files(a.Projets).Any(p => Path.GetFileName(p).StartsWith("f1.conflit-")));
                T.Check("numero invalide / choix invalide refuses", b.E!.Resolve(9, "hub").Contains("invalide") && b.E.Resolve(1, "zzz").Length > 0);
            }
        });

        T.Run("T84b conflit avec Claude OUVERT : 'garder la mienne' fonctionne, 'garder celle du serveur' est refuse (defaut trouve en reel : le bouton ne faisait rien)", delegate
        {
            var (hub, a, b) = Duo(("g1.txt", "base"), ("g2.txt", "base")); using (a) using (b)
            {
                foreach (var f in new[] { "g1.txt", "g2.txt" }) { a.Put(f, "version A " + f, DateTime.UtcNow.AddMinutes(-30)); b.Put(f, "version B " + f + "!", DateTime.UtcNow.AddMinutes(-20)); }
                a.Sync();
                int code = b.Sync(o => o.ClaudeRunning = "yes");
                T.Check("conflits detectes meme avec Claude ouvert", b.E!.Conflicts.Count == 2, "code " + code);
                string r1 = b.E!.Resolve(1, "hub");
                T.Check("garder celle du serveur : refuse, message clair, rien d'ecrase", r1.Contains("Fermez Claude") && b.Read("g1.txt") == "version B g1.txt!", r1);
                string r2 = b.E.Resolve(1, "local");
                T.Check("garder la mienne : OK avec Claude ouvert, le serveur recoit ma version", r2.StartsWith("Conflit resolu") && b.HubRead("g1.txt") == "version B g1.txt!", r2);
                string r3 = b.E.Resolve(1, "both");
                T.Check("garder les deux : OK avec Claude ouvert", r3.StartsWith("Conflit resolu"), r3);
                T.Check("plus aucun conflit", new StateStore(b.State).LoadConflicts().Count == 0);
            }
        });

        T.Run("T85 Claude ouvert : jamais de recuperation, les envois continuent ; Claude ferme : tout rentre", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                a.Put("nouveau-de-A.txt", "NA"); a.Sync();
                b.Put("nouveau-de-B.txt", "NB");
                int code = b.Sync(o => o.ClaudeRunning = "yes");
                T.Check("B (Claude ouvert) : envoie, ne recupere rien", code == 0 && b.HubRead("nouveau-de-B.txt") == "NB" && b.Read("nouveau-de-A.txt") == null, b.LogText);
                T.Check("recuperation signalee comme bloquee", b.E!.Plan.Any(i => i.Kind == Act.PullBlocked && i.Rel == "nouveau-de-A.txt"));
                T.Check("Claude ferme : B recupere", b.Sync() == 0 && b.Read("nouveau-de-A.txt") == "NA");
                T.Check("CountIncoming compte les arrivees", a.Sync() == 0 && a.E!.CountIncoming() == 0);
            }
        });

        T.Run("T86 garde-fou : un grand jeu soudain vide cote local n'efface rien", delegate
        {
            var files = Enumerable.Range(0, 25).Select(i => ("g" + i + ".txt", "contenu " + i)).ToArray();
            var (hub, a, b) = Duo(files); using (a) using (b)
            {
                foreach (var f in Directory.GetFiles(a.Projets)) File.Delete(f);
                int code = a.Sync();
                T.Check("code 2 (avertissement), jeu ignore", code == 2 && a.E!.Warnings.Count == 1 && a.E!.Warnings[0].Contains("local est vide"), a.E!.Summary);
                T.Check("le serveur est intact, rien a la corbeille", Directory.GetFiles(Path.Combine(hub, "projets")).Length == 25 && a.E.Trashed == 0);
                T.Check("B ne perd rien non plus", b.Sync() == 0 && Directory.GetFiles(b.Projets).Length == 25);
            }
        });

        T.Run("T87 serveur remplace (nouvelle identite) : memoire oubliee, aucune suppression deduite", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                File.WriteAllText(Path.Combine(hub, "_etat", "hub.id"), "autre-serveur");
                int code = a.Sync();
                T.Check("code 0, aucune suppression ni erreur", code == 0 && a.E!.Trashed == 0 && a.E.Errors == 0 && !a.E.Plan.Any(i => i.Kind == Act.TrashHub || i.Kind == Act.TrashLocal), a.LogText);
                T.Check("nouvelle identite memorisee, fichiers intacts", new StateStore(a.State).KnownHubId == "autre-serveur" && a.HubRead("a.txt") == "A1" && a.Read("a.txt") == "A1");
                T.Check("le journal le dit", a.LogText.Contains("memoire des synchros precedentes est oubliee"));
            }
        });

        T.Run("T88 verrou : refus si un autre PC tient la main, libere a la fin", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools); using var x = Env.Open(hub);
            a.Put("a.txt", "A1");
            T.Check("un autre PC prend le verrou", x.AcquireLock("PC-AUTRE").Ok);
            T.Check("A : code 5, l'autre PC est nomme", a.Sync() == 5 && a.E!.Summary.Contains("PC-AUTRE"), a.E!.Summary);
            T.Check("rien n'a ete envoye", a.HubRead("a.txt") == null);
            x.ReleaseLock();
            T.Check("verrou libere : A synchronise", a.Sync() == 0 && a.HubRead("a.txt") == "A1");
            T.Check("et le verrou est rendu a la fin", x.AcquireLock("PC-AUTRE").Ok);
        });

        T.Run("T99 presence ecrite AVANT l'attente du verrou (correction de la V1 : l'autre PC voyait Claude ouvert pendant l'attente)", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools); using var x = Env.Open(hub);
            T.Check("un autre PC tient le verrou", x.AcquireLock("PC-AUTRE").Ok);
            T.Check("A (Claude ouvert) : refuse, code 5", a.Sync(o => o.ClaudeRunning = "yes") == 5);
            var p = x.ReadPresence().FirstOrDefault(q => q.Pc == "PC-A");
            T.Check("pourtant la presence de A est ecrite : Claude ouvert", p != null && p.Running, p == null ? "absente" : p.Running.ToString());
            T.Check("A relance Claude ferme : la presence passe a 'ferme' meme sans verrou", a.Sync(o => o.ClaudeRunning = "no") == 5 && !x.ReadPresence().First(q => q.Pc == "PC-A").Running);
            T.Check("mode 'idle' (fenetre masquee) : presence 'ferme'", a.Sync(o => o.ClaudeRunning = "idle") == 5 && !x.ReadPresence().First(q => q.Pc == "PC-A").Running);
        });

        T.Run("T89 presence : refus si Claude est ouvert recemment sur un autre PC", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools); using var x = Env.Open(hub);
            a.Put("a.txt", "A1");
            x.WritePresence("PC-Z", true);
            T.Check("code 4", a.Sync() == 4 && a.E!.Summary.Contains("PC-Z"), a.E!.Summary);
            T.Check("avec --ignore-presence : synchronise", a.Sync(o => o.IgnorePresence = true) == 0 && a.HubRead("a.txt") == "A1");
            x.WritePresence("PC-Z", false);
            T.Check("Claude ferme sur l'autre PC : ok", a.Sync() == 0);
        });

        T.Run("T90 mode econome : recuperations reportees, envois maintenus, reprise ensuite", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools); using var b = new Pc("PC-B", hub, tools);
            a.Put("x.txt", "XX"); a.Put("y.txt", "YY"); a.Sync();
            b.Put("b-only.txt", "BB");
            int code = b.Sync(null, e => e.StopPulls = true);
            T.Check("B : rien recupere, 2 reportes, son envoi est parti", b.E!.PullsSkipped == 2 && b.Read("x.txt") == null && b.HubRead("b-only.txt") == "BB", "code " + code + " " + b.E.Summary);
            T.Check("le resume le dit", b.E.Summary.Contains("reporte"));
            T.Check("synchro suivante : tout est recupere", b.Sync() == 0 && b.Read("x.txt") == "XX" && b.Read("y.txt") == "YY");
        });

        T.Run("T91 fichier modifie PENDANT son envoi : jamais declare synchronise, renvoye ensuite sans conflit", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools);
            a.Put("a.txt", "v1"); a.Sync();
            a.Put("a.txt", "v2 plus long");
            var tap = new Tap(a.Tx); tap.After = (root, rels) => { File.WriteAllText(Path.Combine(root, "a.txt"), "v3 encore plus long que la v2"); };
            a.Tx = tap;
            int code = a.Sync();
            T.Check("erreur signalee (code 2), le serveur a la v2", code == 2 && a.E!.Errors == 1 && a.HubRead("a.txt") == "v2 plus long", "code " + code + a.LogText);
            a.Tx = ((Tap)a.Tx);
            tap.After = null;
            T.Check("synchro suivante : envoi de la v3 sans conflit", a.Sync() == 0 && a.E!.Conflicts.Count == 0 && a.HubRead("a.txt") == "v3 encore plus long que la v2", a.LogText);
        });

        T.Run("T92 fichier disparu entre l'analyse et l'envoi : seul lui est en erreur", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools);
            a.Put("ok1.txt", "1"); a.Put("gone.txt", "g"); a.Put("ok2.txt", "2");
            var tap = new Tap(a.Tx); tap.Before = (root, rels) => File.Delete(Path.Combine(root, "gone.txt")); a.Tx = tap;
            int code = a.Sync();
            T.Check("code 2, 1 erreur, les 2 autres sont partis", code == 2 && a.E!.Errors == 1 && a.HubRead("ok1.txt") == "1" && a.HubRead("ok2.txt") == "2" && a.HubRead("gone.txt") == null, "code " + code + a.LogText);
            tap.Before = null;
            T.Check("synchro suivante propre", a.Sync() == 0 && a.E!.Errors == 0);
        });

        T.Run("T93 serveur qui tombe : code 3, message clair, aucun fichier local touche", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                a.Put("a.txt", "modifie localement", DateTime.UtcNow.AddMinutes(-10));
                a.Hub.Session.Kill();
                int code = a.Sync();
                T.Check("code 3, 'injoignable'", code == 3 && a.E!.Summary.Contains("injoignable"), a.E!.Summary);
                T.Check("fichiers locaux intacts", a.Read("a.txt") == "modifie localement" && a.Read("d/é b.txt") == "E1");
            }
        });

        T.Run("T94 purge : seuls les lots de plus de 30 jours disparaissent (serveur et local)", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools);
            string recent = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (string p in new[] { Path.Combine(hub, "_anciennes", "20200101-000000", "x.txt"), Path.Combine(hub, "_corbeille", "20200101-000000", "y.txt"), Path.Combine(hub, "_anciennes", recent, "z.txt"), Path.Combine(a.State, "anciennes", "20200101-000000", "l.txt"), Path.Combine(a.State, "corbeille", recent, "m.txt") })
            { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); }
            a.Put("a.txt", "A1");
            T.Check("synchro ok", a.Sync() == 0);
            T.Check("lots anciens supprimes (serveur et local)", !Directory.Exists(Path.Combine(hub, "_anciennes", "20200101-000000")) && !Directory.Exists(Path.Combine(hub, "_corbeille", "20200101-000000")) && !Directory.Exists(Path.Combine(a.State, "anciennes", "20200101-000000")));
            T.Check("lots recents conserves", File.Exists(Path.Combine(hub, "_anciennes", recent, "z.txt")) && File.Exists(Path.Combine(a.State, "corbeille", recent, "m.txt")));
        });

        T.Run("T95 la base de la V1 (chemins avec \\) reste utilisable", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                string bp = Path.Combine(a.State, "base-projets.tsv");
                File.WriteAllLines(bp, File.ReadAllLines(bp).Select(l => { var f = l.Split('\t'); f[0] = f[0].Replace('/', '\\'); return string.Join("\t", f); }).ToArray());
                T.Check("rien a faire malgre le format ancien", a.Sync() == 0 && a.E!.P.TotalFiles == 0 && a.E.Plan.All(i => i.Kind == Act.Same), a.LogText);
                T.Check("la base est reecrite avec des '/'", File.ReadAllLines(bp).All(l => !l.Split('\t')[0].Contains('\\')) && File.ReadAllLines(bp).Any(l => l.StartsWith("d/")));
            }
        });

        T.Run("T96 suppression differee : Claude ferme (double verification), Claude ouvert (attente 60 s)", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                File.Delete(a.P("a.txt"));
                T.Check("Claude ferme : double verification puis corbeille dans la meme synchro", a.Sync(o => { o.StableSeconds = 3; o.RecheckTicks = 0; }) == 0 && a.E!.Trashed == 1 && a.HubRead("a.txt") == null, a.LogText);
                File.Delete(a.P("d/é b.txt"));
                a.Sync(o => { o.ClaudeRunning = "yes"; o.StableSeconds = 60; });
                T.Check("Claude ouvert : premiere observation = en attente, rien supprime", a.E!.Trashed == 0 && a.HubRead("d/é b.txt") == "E1" && a.E.Plan.Any(i => i.Kind == Act.PendingDelete));
                T.Check("l'attente est memorisee", new StateStore(a.State).LoadPending("projets").ContainsKey("d/é b.txt"));
                new StateStore(a.State).SavePending("projets", new Dictionary<string, long> { ["d/é b.txt"] = DateTime.UtcNow.Ticks - 70 * 10_000_000L });
                a.Sync(o => { o.ClaudeRunning = "yes"; o.StableSeconds = 60; });
                T.Check("vue depuis plus de 60 s : corbeille", a.E!.Trashed == 1 && a.HubRead("d/é b.txt") == null);
            }
        });

        T.Run("T98 fenetre Corbeille : liste des suppressions, restauration, redistribution sur tous les PC", delegate
        {
            var (hub, a, b) = Duo(); using (a) using (b)
            {
                File.Delete(a.P("d/é b.txt")); File.Delete(a.P("sp ace/c.txt"));
                T.Check("A supprime 2 fichiers", a.Sync() == 0 && a.E!.Trashed == 2, a.LogText);
                var l = a.E!.ListEntries();
                T.Check("2 entrees 'projet' (d et sp ace), supprimees par PC-A", l.Count == 2 && l.All(e => e.Kind == "projet" && e.Pc == "PC-A" && e.Files == 1) && l.Any(e => e.Title == "d") && l.Any(e => e.Title == "sp ace"), string.Join(",", l.Select(e => e.Title)));
                var d = l.First(e => e.Title == "d");
                string msg = a.E!.RestoreItems(d);
                T.Check("restauration : message", msg.Contains("1 fichier(s) restaure(s)") && msg.Contains("0 ignore(s)"), msg);
                T.Check("le fichier est revenu sur le serveur", a.HubRead("d/é b.txt") == "E1");
                T.Check("il n'est plus dans la corbeille", a.E!.ListEntries().Count == 1);
                T.Check("B et A le recuperent a la synchro suivante", b.Sync() == 0 && b.Read("d/é b.txt") == "E1" && a.Sync() == 0 && a.Read("d/é b.txt") == "E1", b.LogText + a.LogText);
                T.Check("numero invalide refuse", a.E!.RestoreEntry(9) == "Numero invalide.");
            }
        });

        T.Run("T97 arrivee d'un PC vierge dont le dossier des projets n'existe pas encore", delegate
        {
            string hub = Env.NewHub(); using var a = new Pc("PC-A", hub, tools);
            a.Put("proj/un.txt", "1"); a.Put("proj/sous/deux.txt", "2"); a.Sync();
            using var c = new Pc("PC-C", hub, tools);
            Directory.Delete(c.Projets);
            T.Check("synchro : dossier cree, fichiers recuperes", c.Sync() == 0 && c.Read("proj/un.txt") == "1" && c.Read("proj/sous/deux.txt") == "2", c.LogText);
        });
    }
}
