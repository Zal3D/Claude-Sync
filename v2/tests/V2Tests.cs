// Claude-Sync V2 : tests du client du serveur (jalon J1). C# 5.
// Le "serveur" des tests est le VRAI programme claudesync-serveur lance en local (python) sur un dossier temporaire ;
// les pannes sont jouees par de faux serveurs ecrits en python. Aucun reseau, aucun NAS.
// Usage : V2Tests.exe --serveur <chemin de claudesync-serveur> [--python <python.exe>]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests
{
    static class T
    {
        public static int Ok, Ko;
        public static List<string> Echecs = new List<string>();

        public static void Check(string nom, bool cond, string detail)
        {
            if (cond) { Ok++; Console.WriteLine("  ok     " + nom); }
            else { Ko++; Echecs.Add(nom); Console.WriteLine("  ECHEC  " + nom + (string.IsNullOrEmpty(detail) ? "" : "  -> " + detail)); }
        }
        public static void Check(string nom, bool cond) { Check(nom, cond, ""); }

        public static void Throws(string nom, HubErrKind kind, Action a)
        {
            try { a(); Check(nom, false, "aucune exception"); }
            catch (HubException e) { Check(nom, e.Kind == kind, "type " + e.Kind + " : " + e.Message); }
            catch (Exception e) { Check(nom, false, "exception inattendue " + e.GetType().Name + " : " + e.Message); }
        }

        public static void Run(string nom, Action a)
        {
            Console.WriteLine(nom);
            try { a(); }
            catch (Exception e) { Ko++; Echecs.Add(nom + " (exception)"); Console.WriteLine("  ECHEC  exception : " + e.GetType().Name + " : " + e.Message); }
        }
    }

    static class Env
    {
        public static string Python, Script, Tmp;
        static int n;

        public static string FindPython(string demande)
        {
            List<string> c = new List<string>();
            if (!string.IsNullOrEmpty(demande)) c.Add(demande);
            string e = Environment.GetEnvironmentVariable("CS_PYTHON");
            if (!string.IsNullOrEmpty(e)) c.Add(e);
            c.Add("python"); c.Add("python3");
            foreach (string cand in c)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(cand, "-c \"print(40+2)\"");
                    psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
                    using (Process p = Process.Start(psi))
                    {
                        string o = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(10000);
                        if (o.Trim() == "42") return cand;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        public static string NewHub()
        {
            string root = Path.Combine(Tmp, "hub" + (++n));
            Directory.CreateDirectory(Path.Combine(root, "_etat"));
            File.WriteAllText(Path.Combine(root, "_etat", "hub.id"), "hub-test");
            return root;
        }

        public static HubServeur Open(string root, string silence)
        {
            Dictionary<string, string> env = new Dictionary<string, string>();
            if (silence != null) env["CS_SILENCE"] = silence;
            return HubServeur.OpenLocal(Python, Script, root, env);
        }
        public static HubServeur Open(string root) { return Open(root, null); }

        public static void Touch(string root, string relDir, string rel, int size, DateTime mtimeUtc)
        {
            string p = Path.Combine(root, relDir.Replace('/', '\\'), rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            byte[] b = new byte[size];
            for (int i = 0; i < size; i++) b[i] = (byte)(i * 7 + 3);
            File.WriteAllBytes(p, b);
            File.SetLastWriteTimeUtc(p, mtimeUtc);
        }

        // Faux serveur : le code python est ecrit dans un fichier et lance a la place du vrai programme.
        public static HubServeur Fake(string code, int timeoutMs)
        {
            string f = Path.Combine(Tmp, "fake" + (++n) + ".py");
            File.WriteAllText(f, code, new UTF8Encoding(false));
            ProcessStartInfo psi = new ProcessStartInfo(Python, "\"" + f + "\"");
            HubServeur h = new HubServeur(new ServerSession(psi));
            h.Session.TimeoutMs = timeoutMs;
            return h;
        }

        public const string FakeReply = @"
import sys, json, gzip
def rep(o, payload=b''):
    o['len'] = len(payload)
    sys.stdout.buffer.write((json.dumps(o) + '\n').encode()); sys.stdout.buffer.write(payload); sys.stdout.buffer.flush()
";
    }

    static class TestProgram
    {
        static readonly DateTime D0 = new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Utc);

        static bool Eq(FileEntry e, long size, DateTime t) { return e != null && e.Size == size && e.Ticks == t.Ticks; }

        public static int Main(string[] args)
        {
            string script = null, py = null, outils = null, enrol = null, rootKey = null; bool lente = args.Contains("--lente");
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--serveur") script = args[i + 1];
                if (args[i] == "--python") py = args[i + 1];
                if (args[i] == "--outils") outils = args[i + 1];
                if (args[i] == "--enrol") enrol = args[i + 1];
                if (args[i] == "--root-key") rootKey = args[i + 1];
            }
            if (script == null || !File.Exists(script)) { Console.WriteLine("Usage : V2Tests.exe --serveur <claudesync-serveur> [--python <exe>]"); return 2; }
            Env.Script = Path.GetFullPath(script);
            Env.Python = Env.FindPython(py);
            if (Env.Python == null) { Console.WriteLine("Python 3 introuvable (option --python ou variable CS_PYTHON) : tests impossibles."); return 2; }
            Env.Tmp = Path.Combine(Path.GetTempPath(), "cs-v2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Env.Tmp);
            Console.WriteLine("Claude-Sync V2 - tests du client du serveur (python : " + Env.Python + ")");
            if (lente)      // essais de liaison lente seulement (longs) : reseau simule entre deux PC d'essai et le serveur d'essai
            {
                if (outils == null || enrol == null || rootKey == null) { Console.WriteLine("--lente demande --outils, --enrol et --root-key"); return 2; }
                SlowLinkTests.Run(Path.GetFullPath(outils), Path.GetFullPath(enrol), rootKey);
                Console.WriteLine(); Console.WriteLine("Resultat : " + T.Ok + " verifications reussies, " + T.Ko + " echec(s)");
                foreach (string e in T.Echecs) Console.WriteLine("  - " + e);
                try { Directory.Delete(Env.Tmp, true); } catch (Exception) { }
                return T.Ko == 0 ? 0 : 1;
            }

            T.Run("T01 hello : version, protocole, identifiant du hub, heure", delegate
            {
                using (HubServeur h = Env.Open(Env.NewHub()))
                {
                    HelloInfo i = h.Hello();
                    T.Check("version renseignee", i.ServerVersion.Length > 0, i.ServerVersion);
                    T.Check("protocole = " + HubServeur.ProtoAttendu, i.Proto == HubServeur.ProtoAttendu);
                    T.Check("hub_id lu dans _etat/hub.id", i.HubId == "hub-test", i.HubId);
                    T.Check("heure du serveur coherente", Math.Abs(DateTime.UtcNow.Ticks - i.ServerTicks) < 30L * 10000000);
                }
            });

            T.Run("T02 listage : tailles, dates exactes, noms accentues et espaces", delegate
            {
                string root = Env.NewHub();
                Env.Touch(root, "projets", "a.txt", 10, D0);
                Env.Touch(root, "projets", "d1/d2/é ç.json", 2000, D0.AddSeconds(5));
                using (HubServeur h = Env.Open(root))
                {
                    ListResult r = h.List("projets", null);
                    T.Check("2 fichiers", r.Count == 2 && r.Files.Count == 2, r.Count.ToString());
                    T.Check("a.txt : taille et date exactes", r.Files.ContainsKey("a.txt") && Eq(r.Files["a.txt"], 10, D0));
                    T.Check("nom accentue avec espace, chemin en /", r.Files.ContainsKey("d1/d2/é ç.json") && Eq(r.Files["d1/d2/é ç.json"], 2000, D0.AddSeconds(5)));
                    T.Check("pas d'empreinte 'inchange' au premier appel", !r.Unchanged && r.Token != null && r.Token.Length == 40);
                }
            });

            T.Run("T03 les cinq jeux pointent sur les bons dossiers du hub", delegate
            {
                string root = Env.NewHub();
                Env.Touch(root, "claude/projects", "c1/s.jsonl", 5, D0);
                Env.Touch(root, "claude/reglages", "settings.json", 6, D0);
                Env.Touch(root, "claude/file-history", "h/f", 7, D0);
                Env.Touch(root, "projets", "p/x", 8, D0);
                Env.Touch(root, "desktop/sessions", "u/local_1.json", 9, D0);
                using (HubServeur h = Env.Open(root))
                {
                    T.Check("conversations", h.List("conversations", null).Files.ContainsKey("c1/s.jsonl"));
                    T.Check("reglages", h.List("reglages", null).Files.ContainsKey("settings.json"));
                    T.Check("historique", h.List("historique", null).Files.ContainsKey("h/f"));
                    T.Check("projets", h.List("projets", null).Files.ContainsKey("p/x"));
                    T.Check("sessions", h.List("sessions", null).Files.ContainsKey("u/local_1.json"));
                    T.Throws("jeu inconnu refuse", HubErrKind.Refused, delegate { h.List("../etc", null); });
                }
            });

            T.Run("T04 fichiers temporaires et dossier de reprise ignores", delegate
            {
                string root = Env.NewHub();
                Env.Touch(root, "projets", "ok.txt", 3, D0);
                Env.Touch(root, "projets", "gros.bin.syncpart-PC1", 3, D0);
                Env.Touch(root, "projets", "x.tmp", 3, D0);
                Env.Touch(root, "projets", ".rsync-partial/gros.bin", 3, D0);
                Env.Touch(root, "projets", "sub/.rsync-partial/z", 3, D0);
                using (HubServeur h = Env.Open(root))
                {
                    ListResult r = h.List("projets", null);
                    T.Check("seul ok.txt est liste", r.Files.Count == 1 && r.Files.ContainsKey("ok.txt"), string.Join(",", r.Files.Keys.ToArray()));
                }
            });

            T.Run("T05 empreinte : 'rien de nouveau', puis detection d'un ajout et d'une modification", delegate
            {
                string root = Env.NewHub();
                Env.Touch(root, "projets", "a.txt", 10, D0);
                using (HubServeur h = Env.Open(root))
                {
                    ListResult r1 = h.List("projets", null);
                    ListResult r2 = h.List("projets", r1.Token);
                    T.Check("inchange : Unchanged, pas de fichiers renvoyes", r2.Unchanged && r2.Files == null && r2.Count == 1);
                    Env.Touch(root, "projets", "b.txt", 4, D0);
                    ListResult r3 = h.List("projets", r1.Token);
                    T.Check("ajout detecte (liste complete renvoyee)", !r3.Unchanged && r3.Files.Count == 2 && r3.Token != r1.Token);
                    File.SetLastWriteTimeUtc(Path.Combine(root, "projets", "a.txt"), D0.AddMinutes(1));
                    ListResult r4 = h.List("projets", r3.Token);
                    T.Check("modification de date detectee", !r4.Unchanged && r4.Token != r3.Token);
                }
            });

            T.Run("T06 stat : present, absent, chemins dangereux refuses", delegate
            {
                string root = Env.NewHub();
                Env.Touch(root, "projets", "a/b.txt", 12, D0);
                using (HubServeur h = Env.Open(root))
                {
                    Dictionary<string, FileEntry> s = h.Stat("projets", new string[] { "a/b.txt", "absent.txt" });
                    T.Check("present", Eq(s["a/b.txt"], 12, D0));
                    T.Check("absent = null", s.ContainsKey("absent.txt") && s["absent.txt"] == null);
                    T.Check("liste vide acceptee", h.Stat("projets", new string[0]).Count == 0);
                    T.Throws("'..' refuse", HubErrKind.Refused, delegate { h.Stat("projets", new string[] { "../x" }); });
                    T.Throws("chemin absolu refuse", HubErrKind.Refused, delegate { h.Stat("projets", new string[] { "/etc/passwd" }); });
                    T.Throws("lecteur Windows refuse", HubErrKind.Refused, delegate { h.Stat("projets", new string[] { "C:\\x" }); });
                    T.Throws("chemin vide refuse", HubErrKind.Refused, delegate { h.Stat("projets", new string[] { "" }); });
                    T.Check("le serveur reste utilisable apres un refus", h.Hello().Proto == 1);
                }
            });

            T.Run("T07 verrou : exclusion entre deux PC, liberation, reprise par le meme PC", delegate
            {
                string root = Env.NewHub();
                using (HubServeur a = Env.Open(root)) using (HubServeur b = Env.Open(root))
                {
                    T.Check("A prend le verrou", a.AcquireLock("PC-A").Ok);
                    LockResult rb = b.AcquireLock("PC-B");
                    T.Check("B refuse, le tenant est PC-A", !rb.Ok && rb.Holder == "PC-A", rb.Holder);
                    T.Check("A le redemande : toujours ok", a.AcquireLock("PC-A").Ok);
                    a.ReleaseLock();
                    T.Check("apres liberation B le prend", b.AcquireLock("PC-B").Ok);
                    T.Check("A est maintenant refuse", !a.AcquireLock("PC-A").Ok);
                    b.ReleaseLock();
                }
            });

            T.Run("T08 verrou orphelin : le PC qui le tient est tue sans rien liberer", delegate
            {
                string root = Env.NewHub();
                using (HubServeur a = Env.Open(root)) using (HubServeur b = Env.Open(root))
                {
                    a.AcquireLock("PC-A");
                    a.Session.Kill();
                    Stopwatch sw = Stopwatch.StartNew(); LockResult rb = null;
                    while (sw.ElapsedMilliseconds < 5000) { rb = b.AcquireLock("PC-B"); if (rb.Ok) break; Thread.Sleep(100); }
                    T.Check("B obtient le verrou en moins de 5 s", rb != null && rb.Ok, sw.ElapsedMilliseconds + " ms");
                    T.Check("verrou orphelin signale comme repris", rb != null && rb.TookOrphan);
                }
            });

            T.Run("T09 verrou : libere apres un silence (minuteur de secours)", delegate
            {
                string root = Env.NewHub();
                using (HubServeur a = Env.Open(root, "2")) using (HubServeur b = Env.Open(root))
                {
                    a.AcquireLock("PC-A");
                    T.Check("tenu pendant le silence", !b.AcquireLock("PC-B").Ok);
                    Thread.Sleep(4500);
                    T.Check("libere apres ~2 s de silence", b.AcquireLock("PC-B").Ok);
                }
            });

            T.Run("T10 corbeille : deplacement cote serveur, _par.txt, dossiers vides nettoyes", delegate
            {
                string root = Env.NewHub();
                Env.Touch(root, "projets", "p/q/x.txt", 5, D0);
                Env.Touch(root, "projets", "p/y.txt", 5, D0);
                using (HubServeur h = Env.Open(root))
                {
                    T.Check("mise a la corbeille reussie", h.Trash("projets", "p/q/x.txt", "2026-11-09_100000", "PC-A"));
                    T.Check("fichier parti du jeu", !File.Exists(Path.Combine(root, "projets", "p", "q", "x.txt")));
                    T.Check("fichier dans _corbeille/<stamp>/<jeu>/", File.Exists(Path.Combine(root, "_corbeille", "2026-11-09_100000", "projets", "p", "q", "x.txt")));
                    T.Check("_par.txt = PC", File.ReadAllText(Path.Combine(root, "_corbeille", "2026-11-09_100000", "_par.txt")) == "PC-A");
                    T.Check("dossier vide p/q supprime, p garde y.txt", !Directory.Exists(Path.Combine(root, "projets", "p", "q")) && File.Exists(Path.Combine(root, "projets", "p", "y.txt")));
                    T.Check("2e fois : fichier absent -> faux", !h.Trash("projets", "p/q/x.txt", "2026-11-09_100000", "PC-A"));
                    T.Throws("tampon dangereux refuse", HubErrKind.Refused, delegate { h.Trash("projets", "p/y.txt", "../evasion", "PC-A"); });
                    T.Throws("chemin dangereux refuse", HubErrKind.Refused, delegate { h.Trash("projets", "../../x", "s1", "PC-A"); });
                    T.Check("y.txt intact apres les refus", File.Exists(Path.Combine(root, "projets", "p", "y.txt")));
                }
            });

            T.Run("T11 archive : meme mecanisme vers _anciennes", delegate
            {
                string root = Env.NewHub();
                Env.Touch(root, "claude/projects", "c/s.jsonl", 5, D0);
                using (HubServeur h = Env.Open(root))
                {
                    T.Check("archivage reussi", h.Archive("conversations", "c/s.jsonl", "2026-11-09_100001"));
                    T.Check("dans _anciennes/<stamp>/conversations/", File.Exists(Path.Combine(root, "_anciennes", "2026-11-09_100001", "conversations", "c", "s.jsonl")));
                    T.Check("retire du jeu", !File.Exists(Path.Combine(root, "claude", "projects", "c", "s.jsonl")));
                }
            });

            T.Run("T12 purge : simulation puis reelle, seulement au-dela de 30 jours", delegate
            {
                string root = Env.NewHub();
                Env.Touch(root, "_anciennes/vieux1", "f", 1, D0); Env.Touch(root, "_anciennes/recent1", "f", 1, D0);
                Env.Touch(root, "_corbeille/vieux2", "f", 1, D0);
                Directory.SetLastWriteTimeUtc(Path.Combine(root, "_anciennes", "vieux1"), DateTime.UtcNow.AddDays(-40));
                Directory.SetLastWriteTimeUtc(Path.Combine(root, "_corbeille", "vieux2"), DateTime.UtcNow.AddDays(-31));
                using (HubServeur h = Env.Open(root))
                {
                    List<string> sim = h.Purge(30, true);
                    T.Check("simulation : 2 dossiers annonces", sim.Count == 2 && sim.Contains("_anciennes/vieux1") && sim.Contains("_corbeille/vieux2"), string.Join(",", sim.ToArray()));
                    T.Check("simulation : rien n'est supprime", Directory.Exists(Path.Combine(root, "_anciennes", "vieux1")));
                    List<string> reel = h.Purge(30, false);
                    T.Check("purge reelle : 2 dossiers supprimes", reel.Count == 2 && !Directory.Exists(Path.Combine(root, "_anciennes", "vieux1")) && !Directory.Exists(Path.Combine(root, "_corbeille", "vieux2")));
                    T.Check("le recent est conserve", Directory.Exists(Path.Combine(root, "_anciennes", "recent1")));
                }
            });

            T.Run("T13 presence : ecriture, lecture, noms refuses", delegate
            {
                string root = Env.NewHub();
                using (HubServeur h = Env.Open(root))
                {
                    h.WritePresence("PC-A", true); h.WritePresence("PC-B", false);
                    List<PresenceInfo> p = h.ReadPresence();
                    PresenceInfo a = p.FirstOrDefault(x => x.Pc == "PC-A"), b = p.FirstOrDefault(x => x.Pc == "PC-B");
                    T.Check("PC-A : Claude ouvert, date recente", a != null && a.Running && Math.Abs(DateTime.UtcNow.Ticks - a.Ticks) < 30L * 10000000);
                    T.Check("PC-B : ferme", b != null && !b.Running);
                    T.Throws("nom '../x' refuse", HubErrKind.Refused, delegate { h.WritePresence("../x", true); });
                    T.Throws("nom 'a/b' refuse", HubErrKind.Refused, delegate { h.WritePresence("a/b", true); });
                }
            });

            T.Run("T14 robustesse : JSON invalide, commande inconnue, le serveur survit", delegate
            {
                using (HubServeur h = Env.Open(Env.NewHub()))
                {
                    Dictionary<string, object> r1 = h.Session.CallLine("{pas du json");
                    T.Check("JSON invalide -> ok=false", !Json.Bool(r1, "ok"));
                    Dictionary<string, object> r2 = h.Session.CallLine("[1,2]");
                    T.Check("JSON non-objet -> ok=false", !Json.Bool(r2, "ok"));
                    Dictionary<string, object> r3 = h.Session.CallLine("{\"cmd\":\"rm -rf\"}");
                    T.Check("commande inconnue -> ok=false", !Json.Bool(r3, "ok") && (Json.Str(r3, "err") ?? "").Contains("inconnue"));
                    T.Check("le serveur repond toujours", h.Hello().Proto == 1);
                }
            });

            T.Run("T15 delai depasse : serveur muet -> erreur reseau rapide, processus arrete", delegate
            {
                HubServeur h = Env.Fake("import time\ntime.sleep(60)\n", 1500);
                Stopwatch sw = Stopwatch.StartNew();
                try { h.Hello(); T.Check("exception attendue", false); }
                catch (HubException e) { T.Check("type reseau + message 'delai'", e.Kind == HubErrKind.Network && e.Message.Contains("delai"), e.Message); }
                T.Check("termine en moins de 6 s", sw.ElapsedMilliseconds < 6000, sw.ElapsedMilliseconds + " ms");
                T.Check("processus tue", h.Session.HasExited);
                h.Dispose();
            });

            T.Run("T16 serveur qui plante : erreur reseau avec le message d'erreur du serveur", delegate
            {
                HubServeur h = Env.Fake("import sys\nsys.stderr.write('boum cote serveur\\n')\nsys.exit(3)\n", 5000);
                try { h.Hello(); T.Check("exception attendue", false); }
                catch (HubException e) { T.Check("type reseau, message du serveur repris", e.Kind == HubErrKind.Network && e.Message.Contains("fermee"), e.Message); }
                h.Dispose();
            });

            T.Run("T17 protocole incompatible : refuse avec un message clair", delegate
            {
                HubServeur h = Env.Fake(Env.FakeReply + "for l in sys.stdin.buffer:\n    rep({'ok':True,'version':'9.9','proto':99,'hub_id':'x','temps_ticks':1})\n", 5000);
                try { h.Hello(); T.Check("exception attendue", false); }
                catch (HubException e) { T.Check("type protocole, mentionne 99", e.Kind == HubErrKind.Protocol && e.Message.Contains("99"), e.Message); }
                h.Dispose();
            });

            T.Run("T18 reponses corrompues : JSON illisible, gzip invalide, ligne invalide, comptage faux", delegate
            {
                HubServeur h1 = Env.Fake("import sys\nfor l in sys.stdin.buffer:\n    sys.stdout.buffer.write(b'pas du json\\n'); sys.stdout.buffer.flush()\n", 5000);
                T.Throws("JSON illisible", HubErrKind.Protocol, delegate { h1.Hello(); }); h1.Dispose();
                HubServeur h2 = Env.Fake(Env.FakeReply + "for l in sys.stdin.buffer:\n    rep({'ok':True,'token':'t','count':1,'gz':True}, b'xxxxx')\n", 5000);
                T.Throws("gzip invalide", HubErrKind.Protocol, delegate { h2.List("projets", null); }); h2.Dispose();
                HubServeur h3 = Env.Fake(Env.FakeReply + "for l in sys.stdin.buffer:\n    rep({'ok':True,'token':'t','count':1,'gz':True}, gzip.compress(b'ligne sans tabulation\\n'))\n", 5000);
                T.Throws("ligne de listage invalide", HubErrKind.Protocol, delegate { h3.List("projets", null); }); h3.Dispose();
                HubServeur h4 = Env.Fake(Env.FakeReply + "for l in sys.stdin.buffer:\n    rep({'ok':True,'token':'t','count':3,'gz':True}, gzip.compress(b'a\\t1\\t100\\nb\\t2\\t200\\n'))\n", 5000);
                T.Throws("nombre de fichiers incoherent", HubErrKind.Protocol, delegate { h4.List("projets", null); }); h4.Dispose();
                HubServeur h5 = Env.Fake(Env.FakeReply + "for l in sys.stdin.buffer:\n    sys.stdout.buffer.write(b'{\"ok\":true,\"len\":10}\\n'); sys.stdout.buffer.write(b'abc'); sys.stdout.buffer.flush()\n    sys.exit(0)\n", 5000);
                T.Throws("flux coupe au milieu des donnees", HubErrKind.Network, delegate { h5.List("projets", null); }); h5.Dispose();
            });

            T.Run("T19 noms ne differant que par la casse : signales, pas de perte silencieuse", delegate
            {
                HubServeur h = Env.Fake(Env.FakeReply + "for l in sys.stdin.buffer:\n    rep({'ok':True,'token':'t','count':3,'gz':True}, gzip.compress(b'Dup.txt\\t1\\t100\\ndup.txt\\t2\\t200\\nz.txt\\t3\\t300\\n'))\n", 5000);
                ListResult r = h.List("projets", null);
                T.Check("2 fichiers retenus, 1 collision signalee", r.Files.Count == 2 && r.CaseCollisions.Count == 1 && r.CaseCollisions[0] == "dup.txt");
                T.Check("le premier (ordre du serveur) est conserve", r.Files["Dup.txt"].Size == 1);
                h.Dispose();
            });

            T.Run("T20 gros listage : 5 000 fichiers", delegate
            {
                string root = Env.NewHub();
                string d = Path.Combine(root, "projets");
                for (int i = 0; i < 5000; i++)
                {
                    string sub = Path.Combine(d, "d" + (i % 50));
                    if (i < 50) Directory.CreateDirectory(sub);
                    File.WriteAllBytes(Path.Combine(sub, "f" + i + ".dat"), new byte[(i % 7) + 1]);
                }
                using (HubServeur h = Env.Open(root))
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    ListResult r = h.List("projets", null);
                    T.Check("5 000 fichiers listes", r.Files.Count == 5000, r.Files.Count.ToString());
                    T.Check("en moins de 10 s (local)", sw.ElapsedMilliseconds < 10000, sw.ElapsedMilliseconds + " ms");
                    sw.Restart();
                    ListResult r2 = h.List("projets", r.Token);
                    T.Check("'rien de nouveau' : pas de donnees renvoyees", r2.Unchanged && r2.Files == null);
                }
            });

            T.Run("T21 mesure de liaison et appels simultanes de deux fils", delegate
            {
                using (HubServeur h = Env.Open(Env.NewHub()))
                {
                    int ms = h.PingMs(5);
                    T.Check("PingMs plausible", ms >= 0 && ms < 2000, ms + " ms");
                    int ok = 0; Exception ex = null; object gate = new object();
                    ThreadStart w = delegate
                    {
                        try { for (int i = 0; i < 20; i++) { h.PingMs(1); lock (gate) ok++; } }
                        catch (Exception e) { ex = e; }
                    };
                    Thread t1 = new Thread(w), t2 = new Thread(w);
                    t1.Start(); t2.Start(); t1.Join(); t2.Join();
                    T.Check("2 x 20 appels en parallele, aucune erreur", ok == 40 && ex == null, ok + " ok " + (ex == null ? "" : ex.Message));
                }
            });

            T.Run("T22 fermeture : Dispose arrete le processus, appel apres fermeture = erreur reseau", delegate
            {
                HubServeur h = Env.Open(Env.NewHub());
                h.Hello();
                ServerSession s = h.Session;
                h.Dispose();
                T.Check("processus arrete", s.HasExited);
                T.Throws("appel apres Dispose", HubErrKind.Network, delegate { h.Hello(); });
                h.Dispose();
                T.Check("double Dispose sans erreur", true);
            });

            T.Run("T23 le verrou est libere a la fermeture normale de la session", delegate
            {
                string root = Env.NewHub();
                using (HubServeur b = Env.Open(root))
                {
                    HubServeur a = Env.Open(root);
                    a.AcquireLock("PC-A");
                    T.Check("verrou tenu", !b.AcquireLock("PC-B").Ok);
                    a.Dispose();
                    T.Check("libere des la fermeture de A (sans reprise d'orphelin)", b.AcquireLock("PC-B").Ok);
                }
                string lk = Path.Combine(root, "_etat", "sync.lock");
                Stopwatch sw = Stopwatch.StartNew();
                while (File.Exists(lk) && sw.ElapsedMilliseconds < 4000) Thread.Sleep(100);
                T.Check("fichier sync.lock supprime a la fermeture de B", !File.Exists(lk), sw.ElapsedMilliseconds + " ms");
            });

            T.Run("T24 SshTarget : commande SSH construite correctement", delegate
            {
                SshTarget t = new SshTarget { SshExe = @"C:\Prog Files\cs\ssh.exe", KeyFile = @"C:\Users\x y\.ssh\id", KnownHosts = @"C:\Users\x y\kh", Host = "nas.exemple.fr", User = "alice", Port = 2222 };
                string a = t.BuildArguments();
                T.Check("cle entre guillemets, barres obliques", a.Contains("-i \"C:/Users/x y/.ssh/id\""), a);
                T.Check("known_hosts entre guillemets", a.Contains("UserKnownHostsFile=\"C:/Users/x y/kh\""), a);
                T.Check("verification stricte de l'hote, pas de mot de passe", a.Contains("StrictHostKeyChecking=yes") && a.Contains("BatchMode=yes"));
                T.Check("port, utilisateur@hote, commande distante", a.Contains("-p 2222") && a.EndsWith("alice@nas.exemple.fr claudesync-serveur"), a);
                T.Check("fichier a lancer", t.ToStartInfo().FileName == @"C:\Prog Files\cs\ssh.exe");
            });

            T.Run("T25 serveur : CS_ROOT absent -> sortie en erreur, pas de plantage du client", delegate
            {
                ProcessStartInfo psi = new ProcessStartInfo(Env.Python, "\"" + Env.Script + "\"");
                psi.EnvironmentVariables["CS_ROOT"] = Path.Combine(Env.Tmp, "n-existe-pas");
                HubServeur h = new HubServeur(new ServerSession(psi));
                h.Session.TimeoutMs = 5000;
                T.Throws("erreur reseau explicite", HubErrKind.Network, delegate { h.Hello(); });
                T.Check("message du serveur repris", h.Session.StderrText.Contains("CS_ROOT"), h.Session.StderrText);
                h.Dispose();
            });

            WatcherTests.Run();
            SetupTests.Run();
            UpdateTests.Run();
            MigrateTests.Run();
            UiTests.Run();
            ServerExtraTests.Run();
            PlannerTests.Run();
            SetDefsTests.Run();
            AppTests.Run(outils == null ? null : Path.GetFullPath(outils), enrol == null ? null : Path.GetFullPath(enrol), rootKey);
            RsyncTests.Run(outils == null ? null : Path.GetFullPath(outils), enrol == null ? null : Path.GetFullPath(enrol));

            Console.WriteLine();
            Console.WriteLine("Resultat : " + T.Ok + " verifications reussies, " + T.Ko + " echec(s)");
            foreach (string e in T.Echecs) Console.WriteLine("  - " + e);
            try { Directory.Delete(Env.Tmp, true); } catch (Exception) { }
            return T.Ko == 0 ? 0 : 1;
        }
    }
}
