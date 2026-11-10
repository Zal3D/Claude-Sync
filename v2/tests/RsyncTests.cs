// Claude-Sync V2 : tests du transport rsync (jalon J2). C# 5.
// Trois familles : fonctions pures ; transferts en mode LOCAL (un dossier joue le role du hub, aucun reseau) ;
// integration par SSH contre le serveur d'essai (seulement si --enrol <dossier> est fourni).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests
{
    static class RsyncTests
    {
        static string tools;
        static readonly DateTime D0 = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        static string Hash(string p) { using (SHA256 s = SHA256.Create()) using (FileStream f = File.OpenRead(p)) return BitConverter.ToString(s.ComputeHash(f)); }
        static void Rand(string path, int bytes, int seed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] b = new byte[bytes]; new Random(seed).NextBytes(b); File.WriteAllBytes(path, b);
        }
        static void Append(string path, int bytes, int seed)
        {
            byte[] b = new byte[bytes]; new Random(seed).NextBytes(b);
            using (FileStream f = new FileStream(path, FileMode.Append)) f.Write(b, 0, b.Length);
        }
        static void Txt(string path, string text, DateTime mtime)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false)); File.SetLastWriteTimeUtc(path, mtime);
        }
        static string Dir(string name) { string d = Path.Combine(Env.Tmp, name); Directory.CreateDirectory(d); return d; }

        static RsyncTransport Local(string hubRoot, int bw)
        {
            return new RsyncTransport(new RsyncOptions { ToolsDir = tools, BwLimitKBps = bw, TimeoutSeconds = 30 }, new RsyncRemote { LocalHubRoot = hubRoot });
        }

        public static void Run(string toolsDir, string enrolDir)
        {
            tools = toolsDir;
            Pure();
            if (tools == null || !File.Exists(Path.Combine(tools, "rsync.exe")))
            {
                Console.WriteLine("(tests rsync en mode local ignores : --outils absent ou sans rsync.exe)");
                return;
            }
            LocalMode();
            EngineTests.Run(tools);
            if (enrolDir != null) Integration(enrolDir); else Console.WriteLine("(tests rsync par SSH ignores : --enrol absent)");
        }

        // ------------------------------------------------------------------ fonctions pures
        static void Pure()
        {
            T.Run("T30 chemins Windows -> chemins de rsync (cygdrive, UNC, accents, espaces)", delegate
            {
                T.Check("lecteur C:", RsyncUtil.Cyg(@"C:\Users\x\f.txt") == "/cygdrive/c/Users/x/f.txt");
                T.Check("espaces et accents conserves", RsyncUtil.Cyg(@"D:\Mes Projets\é à") == "/cygdrive/d/Mes Projets/é à");
                T.Check("partage reseau", RsyncUtil.Cyg(@"\\nas\Partage\x") == "//nas/Partage/x");
                T.Check("chemin deja Unix inchange", RsyncUtil.Cyg("/projets/") == "/projets/");
            });

            T.Run("T31 quotes : ligne de commande Windows et valeur de -e", delegate
            {
                T.Check("simple inchange", RsyncUtil.Arg("-tIz") == "-tIz");
                T.Check("espace -> guillemets", RsyncUtil.Arg("a b") == "\"a b\"");
                T.Check("guillemet interne echappe", RsyncUtil.Arg("a\"b") == "\"a\\\"b\"");
                T.Check("antislash final double", RsyncUtil.Arg("a b\\") == "\"a b\\\\\"");
                T.Check("vide -> \"\"", RsyncUtil.Arg("") == "\"\"");
                T.Check("Shq simple", RsyncUtil.Shq("ssh.exe") == "ssh.exe");
                T.Check("Shq espace -> '...'", RsyncUtil.Shq("a b") == "'a b'");
                T.Check("Shq apostrophe -> \"...\"", RsyncUtil.Shq("l'a b") == "\"l'a b\"");
            });

            T.Run("T32 liste de fichiers : UTF-8 sans BOM, fins de ligne LF", delegate
            {
                byte[] b = RsyncUtil.ListBytes(new string[] { "a.txt", "d/é b.txt" });
                T.Check("pas de BOM", b[0] == (byte)'a');
                T.Check("LF seulement, termine par LF", Encoding.UTF8.GetString(b) == "a.txt\nd/é b.txt\n");
            });

            T.Run("T33 analyse de la sortie de rsync : fichiers, statistiques, progression", delegate
            {
                TransferResult r = new TransferResult(); long last = -1;
                Action<long> on = delegate (long v) { last = v; };
                string[] lines = {
                    "<f+++++++++|10|a.txt", "cd+++++++++|0|d/", "<f..t......|2048|d/é b.txt", ">f.st......|7|reçu|avec barre.txt", "<f+++++++++|sans octets|mal forme.txt",
                    "        1,048,576  50%  2.00MB/s    0:00:00", "Literal data: 1,234 bytes", "Matched data: 5,678 bytes",
                    "Total bytes sent: 1,000", "Total bytes received: 2,000", "ligne quelconque sans rien" };
                foreach (string l in lines) RsyncUtil.ParseLine(l, r, on);
                T.Check("3 fichiers (dossiers ignores)", r.Done.Count == 3 && r.Done[0] == "a.txt" && r.Done[1] == "d/é b.txt" && r.Done[2] == "reçu|avec barre.txt", string.Join(";", r.Done.ToArray()));
                T.Check("statistiques", r.Literal == 1234 && r.Matched == 5678 && r.BytesSent == 1000 && r.BytesReceived == 2000);
                T.Check("progression en octets", last == 1048576, last.ToString());
            });

            T.Run("T34 codes de sortie -> nature de l'erreur", delegate
            {
                T.Check("0 : rien", RsyncUtil.FatalKind(0, "") == null);
                T.Check("23 : transfert partiel, pas fatal", RsyncUtil.FatalKind(23, "") == null);
                T.Check("24 : fichiers disparus, pas fatal", RsyncUtil.FatalKind(24, "") == null);
                T.Check("30 : delai -> reseau", RsyncUtil.FatalKind(30, "") == HubErrKind.Network);
                T.Check("12 : flux coupe -> reseau", RsyncUtil.FatalKind(12, "") == HubErrKind.Network);
                T.Check("255 : ssh -> reseau", RsyncUtil.FatalKind(255, "Connection refused") == HubErrKind.Network);
                T.Check("255 + cle refusee -> refus (pas de nouvel essai)", RsyncUtil.FatalKind(255, "claudesync@x: Permission denied (publickey).") == HubErrKind.Refused);
                T.Check("255 + hote inconnu -> refus", RsyncUtil.FatalKind(255, "Host key verification failed.") == HubErrKind.Refused);
                T.Check("11 : disque -> serveur", RsyncUtil.FatalKind(11, "") == HubErrKind.Server);
                T.Check("1 : usage -> protocole", RsyncUtil.FatalKind(1, "") == HubErrKind.Protocol);
            });

            T.Run("T35 composition des arguments : jamais de suppression, options de securite presentes", delegate
            {
                SshTarget ssh = new SshTarget { KeyFile = @"C:\Users\x y\k", KnownHosts = @"C:\Users\x y\kh", Host = "nas", User = "u", Port = 2222 };
                RsyncTransport rt = new RsyncTransport(new RsyncOptions { ToolsDir = @"C:\Program Files\cs", TimeoutSeconds = 45, BwLimitKBps = 100 }, new RsyncRemote { Ssh = ssh });
                List<string> push = rt.BuildPushArgs(@"C:\Projets", "/projets", "/_anciennes/s1/projets", @"C:\tmp\l.lst");
                string all = string.Join(" ", push.ToArray());
                T.Check("-I (le moteur decide, pas le controle rapide)", push.Contains("-tI"));
                T.Check("compression, delai, limite de debit", push.Contains("-z") && push.Contains("--timeout=45") && push.Contains("--bwlimit=100"));
                T.Check("reprise : partial-dir relatif + temp-dir a la racine du hub", push.Contains("--partial-dir=.rsync-partial") && push.Contains("--temp-dir=/.rsync-tmp"));
                T.Check("archive cote serveur", push.Contains("--backup") && push.Contains("--backup-dir=/_anciennes/s1/projets"));
                T.Check("droits des fichiers crees", push.Contains("-p") && push.Contains("--chmod=Fug=rw,Fo=r,Dug=rwx,Do=rx"));
                T.Check("liste de fichiers au format cygdrive", push.Contains("--files-from=/cygdrive/c/tmp/l.lst"));
                T.Check("AUCUNE option de suppression", !all.Contains("--delete") && !all.Contains("--remove-source") && !all.Contains("--del"));
                T.Check("source et destination", push[push.Count - 2] == "/cygdrive/c/Projets/" && push[push.Count - 1] == "u@nas:/projets/", push[push.Count - 1]);
                int ie = push.IndexOf("-e");
                string e = ie >= 0 ? push[ie + 1] : "";
                T.Check("-e : ssh embarque, cle et known_hosts entre apostrophes, hote verifie", e.Contains("'/cygdrive/c/Program Files/cs/ssh.exe'") && e.Contains("'UserKnownHostsFile=/cygdrive/c/Users/x y/kh'") && e.Contains("StrictHostKeyChecking=yes") && e.Contains("BatchMode=yes") && e.Contains("-p 2222"), e);
                List<string> pull = rt.BuildPullArgs("/projets", @"C:\Projets", @"C:\bk\s", @"C:\tmp\l.lst");
                T.Check("recuperation : archive locale, reprise locale, destination locale", pull.Contains("--backup-dir=/cygdrive/c/bk/s") && pull.Contains("--partial-dir=/cygdrive/c/Projets/.rsync-partial") && pull[pull.Count - 2] == "u@nas:/projets/" && pull[pull.Count - 1] == "/cygdrive/c/Projets/");
                T.Check("recuperation : pas de -p ni de chmod", !pull.Contains("-p") && !string.Join(" ", pull.ToArray()).Contains("--chmod"));
            });
        }

        // ------------------------------------------------------------------ mode local (un dossier tient lieu de hub)
        static void LocalMode()
        {
            T.Run("T36 lot d'envoi : accents, espaces (aussi dans le chemin local), liste des fichiers reussis", delegate
            {
                string hub = Env.NewHub(), loc = Dir("é loc 1");
                Txt(Path.Combine(loc, "a.txt"), "v1 a", D0); Txt(Path.Combine(loc, "d", "é b.txt"), "v1 e", D0); Txt(Path.Combine(loc, "sp ace", "c.txt"), "v1 c", D0);
                string[] rels = { "a.txt", "d/é b.txt", "sp ace/c.txt" };
                TransferResult r = Local(hub, 0).Push(loc, rels, "/projets/", null);
                T.Check("sortie 0, 3 fichiers confirmes, 0 echec", r.ExitCode == 0 && r.Success && r.Done.Count == 3 && r.Failed.Count == 0, "code " + r.ExitCode + " " + r.ErrorText);
                T.Check("contenus identiques", File.ReadAllText(Path.Combine(hub, "projets", "a.txt")) == "v1 a" && File.ReadAllText(Path.Combine(hub, "projets", "d", "é b.txt")) == "v1 e" && File.ReadAllText(Path.Combine(hub, "projets", "sp ace", "c.txt")) == "v1 c");
                T.Check("dates conservees", File.GetLastWriteTimeUtc(Path.Combine(hub, "projets", "a.txt")) == D0);
                T.Check("octets envoyes comptes", r.BytesSent > 0);
            });

            T.Run("T37 remplacement : l'ancienne version part dans l'archive du hub, la nouvelle est en place", delegate
            {
                string hub = Env.NewHub(), loc = Dir("loc2");
                Txt(Path.Combine(loc, "a.txt"), "v1 a", D0); Txt(Path.Combine(loc, "d", "é b.txt"), "v1 e", D0);
                RsyncTransport rt = Local(hub, 0);
                rt.Push(loc, new string[] { "a.txt", "d/é b.txt" }, "/projets/", null);
                Txt(Path.Combine(loc, "a.txt"), "v2 a modifie", D0.AddMinutes(1)); Txt(Path.Combine(loc, "d", "é b.txt"), "v2 e modifie", D0.AddMinutes(1));
                TransferResult r = rt.Push(loc, new string[] { "a.txt", "d/é b.txt" }, "/projets/", "/_anciennes/s1/projets");
                T.Check("2 fichiers confirmes", r.Success && r.Done.Count == 2, r.ErrorText);
                T.Check("nouvelle version en place", File.ReadAllText(Path.Combine(hub, "projets", "a.txt")) == "v2 a modifie");
                T.Check("ancienne version archivee (meme arborescence)", File.ReadAllText(Path.Combine(hub, "_anciennes", "s1", "projets", "a.txt")) == "v1 a" && File.ReadAllText(Path.Combine(hub, "_anciennes", "s1", "projets", "d", "é b.txt")) == "v1 e");
            });

            T.Run("T38 differences : un fichier de 12 Mo qui grossit de 1 Mo ne renvoie que la fin", delegate
            {
                string hub = Env.NewHub(), loc = Dir("loc3"); string f = Path.Combine(loc, "big.bin");
                Rand(f, 12 * 1024 * 1024, 1);
                RsyncTransport rt = Local(hub, 0);
                TransferResult r1 = rt.Push(loc, new string[] { "big.bin" }, "/projets/", null);
                T.Check("premier envoi complet", r1.Success && r1.Literal >= 12 * 1024 * 1024, r1.Literal.ToString());
                Append(f, 1024 * 1024, 2);
                TransferResult r2 = rt.Push(loc, new string[] { "big.bin" }, "/projets/", null);
                T.Check("second envoi : ~1 Mo envoye, le reste reconnu", r2.Success && r2.Literal < 2 * 1024 * 1024 && r2.Matched > 10 * 1024 * 1024, "literal " + r2.Literal + " matched " + r2.Matched);
                T.Check("fichiers identiques", Hash(f) == Hash(Path.Combine(hub, "projets", "big.bin")));
            });

            T.Run("T39 transfert partiel : un fichier absent n'empeche pas les autres", delegate
            {
                string hub = Env.NewHub(), loc = Dir("loc4");
                Txt(Path.Combine(loc, "ok1.txt"), "1", D0); Txt(Path.Combine(loc, "ok2.txt"), "2", D0);
                TransferResult r = Local(hub, 0).Push(loc, new string[] { "ok1.txt", "absent.txt", "ok2.txt" }, "/projets/", null);
                T.Check("code 23, pas d'erreur d'ensemble", r.ExitCode == 23 && r.Fatal == null && !r.Success, "code " + r.ExitCode);
                T.Check("2 confirmes", r.Done.Count == 2 && r.Done.Contains("ok1.txt") && r.Done.Contains("ok2.txt"));
                T.Check("l'absent est signale en echec, seul", r.Failed.Count == 1 && r.Failed[0] == "absent.txt", string.Join(",", r.Failed.ToArray()));
                T.Check("les 2 autres sont bien arrives", File.Exists(Path.Combine(hub, "projets", "ok1.txt")) && File.Exists(Path.Combine(hub, "projets", "ok2.txt")) && !File.Exists(Path.Combine(hub, "projets", "absent.txt")));
            });

            T.Run("T40 recuperation : mise a jour d'un fichier existant, ancienne version archivee en local", delegate
            {
                string hub = Env.NewHub(), loc = Dir("é loc 5"), bk = Path.Combine(Env.Tmp, "bk5");
                Txt(Path.Combine(hub, "projets", "d", "é x.txt"), "version serveur", D0.AddMinutes(5));
                Txt(Path.Combine(hub, "projets", "n.txt"), "nouveau", D0);
                Txt(Path.Combine(loc, "d", "é x.txt"), "ancienne locale", D0);
                TransferResult r = Local(hub, 0).Pull("/projets/", new string[] { "d/é x.txt", "n.txt" }, loc, bk);
                T.Check("2 confirmes", r.Success && r.Done.Count == 2 && r.Failed.Count == 0, r.ErrorText);
                T.Check("fichier mis a jour", File.ReadAllText(Path.Combine(loc, "d", "é x.txt")) == "version serveur");
                T.Check("nouveau fichier cree", File.ReadAllText(Path.Combine(loc, "n.txt")) == "nouveau");
                T.Check("ancienne version archivee localement", File.ReadAllText(Path.Combine(bk, "d", "é x.txt")) == "ancienne locale");
                T.Check("date du serveur conservee", File.GetLastWriteTimeUtc(Path.Combine(loc, "n.txt")) == D0);
            });

            T.Run("T41 annulation : processus arrete vite, aucun fichier incomplet sous son vrai nom, reprise possible", delegate
            {
                string hub = Env.NewHub(), loc = Dir("loc6"); string f = Path.Combine(loc, "gros.bin");
                Rand(f, 15 * 1024 * 1024, 3);
                RsyncTransport rt = Local(hub, 800);
                Stopwatch sw = Stopwatch.StartNew(); rt.Cancel = delegate { return sw.ElapsedMilliseconds > 1500; };
                TransferResult r = rt.Push(loc, new string[] { "gros.bin" }, "/projets/", null);
                T.Check("annule, termine en moins de 15 s", r.Cancelled && !r.Success && sw.ElapsedMilliseconds < 15000, sw.ElapsedMilliseconds + " ms");
                T.Check("rien sous le vrai nom", !File.Exists(Path.Combine(hub, "projets", "gros.bin")));
                T.Check("annulation n'est pas une erreur d'ensemble", r.Fatal == null);
                TransferResult r2 = Local(hub, 0).Push(loc, new string[] { "gros.bin" }, "/projets/", null);
                T.Check("nouvel envoi complet reussi", r2.Success && r2.Done.Count == 1, r2.ErrorText);
                T.Check("fichier identique", Hash(f) == Hash(Path.Combine(hub, "projets", "gros.bin")));
            });

            T.Run("T42 liste vide : aucun processus lance ; outil manquant : erreur claire", delegate
            {
                TransferResult r = Local(Env.NewHub(), 0).Push(Dir("loc7"), new string[0], "/projets/", null);
                T.Check("liste vide = succes immediat", r.Success && r.Done.Count == 0 && r.Failed.Count == 0);
                RsyncTransport rt = new RsyncTransport(new RsyncOptions { ToolsDir = Path.Combine(Env.Tmp, "pas-d-outils") }, new RsyncRemote { LocalHubRoot = Env.NewHub() });
                T.Throws("rsync.exe introuvable", HubErrKind.Server, delegate { rt.Push(Dir("loc8"), new string[] { "x" }, "/projets/", null); });
            });

            T.Run("T43 serveur injoignable par SSH : erreur reseau rapide, rien n'est marque comme reussi", delegate
            {
                if (!File.Exists(Path.Combine(tools, "ssh.exe"))) { T.Check("ssh.exe present dans les outils", false); return; }
                string loc = Dir("loc9"); Txt(Path.Combine(loc, "a.txt"), "x", D0);
                SshTarget ssh = new SshTarget { KeyFile = Path.Combine(Env.Tmp, "cle-inexistante"), KnownHosts = Path.Combine(Env.Tmp, "kh-inexistant"), Host = "127.0.0.1", User = "u", Port = 1, ConnectTimeoutSeconds = 3 };
                RsyncTransport rt = new RsyncTransport(new RsyncOptions { ToolsDir = tools, TimeoutSeconds = 10 }, new RsyncRemote { Ssh = ssh });
                Stopwatch sw = Stopwatch.StartNew();
                TransferResult r = rt.Push(loc, new string[] { "a.txt" }, "/projets/", null);
                T.Check("erreur d'ensemble de nature reseau", r.Fatal == HubErrKind.Network && !r.Success, r.Fatal + " code " + r.ExitCode + " " + r.ErrorText);
                T.Check("aucun fichier confirme, un en echec", r.Done.Count == 0 && r.Failed.Count == 1);
                T.Check("en moins de 20 s", sw.ElapsedMilliseconds < 20000, sw.ElapsedMilliseconds + " ms");
            });
        }

        // ------------------------------------------------------------------ integration : SSH reel vers le serveur d'essai
        static SshTarget Target(string enrol)
        {
            Dictionary<string, object> c = Json.ParseObject(File.ReadAllText(Path.Combine(enrol, "config.json")));
            return new SshTarget { SshExe = Path.Combine(tools, "ssh.exe"), KeyFile = Json.Str(c, "cle"), KnownHosts = Json.Str(c, "known_hosts"), Host = Json.Str(c, "hote"), User = Json.Str(c, "utilisateur"), Port = (int)Json.Lng(c, "port") };
        }

        static void Integration(string enrol)
        {
            SshTarget tg = Target(enrol);
            string remote = "/projets/it-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "/";
            string rel = remote.Substring("/projets/".Length);            // chemin vu par le jeu "projets"
            Console.WriteLine("Integration SSH vers " + tg.User + "@" + tg.Host + " dans " + remote);

            T.Run("T50 SSH : lot (accents, espaces), puis le serveur le liste avec les memes tailles", delegate
            {
                string loc = Dir("é iloc 1");
                Txt(Path.Combine(loc, "a.txt"), "v1 a", D0); Txt(Path.Combine(loc, "d", "é b.txt"), "v1 e", D0); Txt(Path.Combine(loc, "sp ace", "c.txt"), "v1 c", D0);
                RsyncTransport rt = new RsyncTransport(new RsyncOptions { ToolsDir = tools }, new RsyncRemote { Ssh = tg });
                TransferResult r = rt.Push(loc, new string[] { "a.txt", "d/é b.txt", "sp ace/c.txt" }, remote, null);
                T.Check("3 confirmes", r.Success && r.Done.Count == 3, "code " + r.ExitCode + " " + r.ErrorText);
                using (HubServeur h = HubServeur.OpenSsh(tg))
                {
                    h.Hello();
                    ListResult l = h.List("projets", null);
                    T.Check("le serveur voit les 3 fichiers, noms exacts", l.Files.ContainsKey(rel + "a.txt") && l.Files.ContainsKey(rel + "d/é b.txt") && l.Files.ContainsKey(rel + "sp ace/c.txt"));
                    T.Check("tailles et dates exactes", l.Files[rel + "a.txt"].Size == 4 && Math.Abs(l.Files[rel + "a.txt"].Ticks - D0.Ticks) <= FileEntry.Tol);
                    T.Check("aucun fichier temporaire visible", !l.Files.Keys.Any(k => k.Contains(".rsync-") || k.Contains(".syncpart")));
                }
            });

            T.Run("T51 SSH : remplacement avec archive cote serveur, ancienne version recuperable", delegate
            {
                string loc = Dir("iloc2"), back = Dir("iback2");
                Txt(Path.Combine(loc, "a.txt"), "v1 a", D0);
                RsyncTransport rt = new RsyncTransport(new RsyncOptions { ToolsDir = tools }, new RsyncRemote { Ssh = tg });
                rt.Push(loc, new string[] { "a.txt" }, remote + "arch/", null);
                Txt(Path.Combine(loc, "a.txt"), "v2 a modifie", D0.AddMinutes(1));
                string stamp = "it-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                TransferResult r = rt.Push(loc, new string[] { "a.txt" }, remote + "arch/", "/_anciennes/" + stamp + "/projets");
                T.Check("remplacement confirme", r.Success && r.Done.Count == 1, r.ErrorText);
                TransferResult p = rt.Pull("/_anciennes/" + stamp + "/projets/", new string[] { "a.txt" }, back, null);
                T.Check("l'archive du serveur contient l'ancienne version", p.Success && File.Exists(Path.Combine(back, "a.txt")) && File.ReadAllText(Path.Combine(back, "a.txt")) == "v1 a", p.ErrorText);
            });

            T.Run("T52 SSH : annulation en plein envoi, rien d'incomplet visible, reprise qui reutilise la partie recue", delegate
            {
                string loc = Dir("iloc3"); string f = Path.Combine(loc, "big.bin");
                Rand(f, 30 * 1024 * 1024, 5);
                RsyncTransport rt = new RsyncTransport(new RsyncOptions { ToolsDir = tools, BwLimitKBps = 1500, Compress = false }, new RsyncRemote { Ssh = tg });
                Stopwatch sw = Stopwatch.StartNew(); rt.Cancel = delegate { return sw.ElapsedMilliseconds > 5000; };
                TransferResult r1 = rt.Push(loc, new string[] { "big.bin" }, remote + "reprise/", null);
                T.Check("annule, pas d'erreur d'ensemble", r1.Cancelled && r1.Fatal == null && r1.Done.Count == 0, sw.ElapsedMilliseconds + " ms, annule=" + r1.Cancelled + " fatal=" + r1.Fatal + " fichiers=" + r1.Done.Count + " code=" + r1.ExitCode + " " + r1.ErrorText);
                using (HubServeur h = HubServeur.OpenSsh(tg))
                {
                    ListResult l = h.List("projets", null);
                    T.Check("le serveur ne voit aucun fichier incomplet", !l.Files.ContainsKey(rel + "reprise/big.bin") && !l.Files.Keys.Any(k => k.StartsWith(rel + "reprise/")), string.Join(",", l.Files.Keys.Where(k => k.StartsWith(rel + "reprise/")).ToArray()));
                }
                System.Threading.Thread.Sleep(4000);   // le serveur met quelques secondes a ranger la partie recue (la reprise reelle a lieu bien plus tard)
                RsyncTransport rt2 = new RsyncTransport(new RsyncOptions { ToolsDir = tools, Compress = false }, new RsyncRemote { Ssh = tg });
                TransferResult r2 = rt2.Push(loc, new string[] { "big.bin" }, remote + "reprise/", null);
                T.Check("reprise reussie", r2.Success && r2.Done.Count == 1, r2.ErrorText);
                // La reprise "au milieu du fichier" depend de ce que le rsync du serveur garde dans .rsync-partial quand la liaison tombe :
                // observe tantot conserve (jusqu'a 10 Mo), tantot non (course cote serveur entre sshd et rsync). Ce n'est donc PAS une garantie :
                // on verifie seulement l'essentiel (le fichier arrive complet et exact) et on affiche ce qui a ete reutilise.
                Console.WriteLine("  info   reprise : " + (r2.Matched / 1024) + " Ko reutilises, " + (r2.Literal / 1024) + " Ko renvoyes (non garanti)");
                string back = Dir("iback3");
                TransferResult p = rt2.Pull(remote + "reprise/", new string[] { "big.bin" }, back, null);
                T.Check("aller-retour : fichier identique", p.Success && Hash(f) == Hash(Path.Combine(back, "big.bin")), p.ErrorText);
            });

            T.Run("T53 SSH : recuperation par differences avec archive locale", delegate
            {
                string loc = Dir("iloc4"), back = Dir("iback4"), bk = Path.Combine(Env.Tmp, "ibk4"); string f = Path.Combine(loc, "conv.jsonl");
                Rand(f, 8 * 1024 * 1024, 7);
                RsyncTransport rt = new RsyncTransport(new RsyncOptions { ToolsDir = tools }, new RsyncRemote { Ssh = tg });
                rt.Push(loc, new string[] { "conv.jsonl" }, remote + "conv/", null);
                TransferResult p0 = rt.Pull(remote + "conv/", new string[] { "conv.jsonl" }, back, null);
                T.Check("premiere recuperation", p0.Success, p0.ErrorText);
                Append(f, 512 * 1024, 8);
                rt.Push(loc, new string[] { "conv.jsonl" }, remote + "conv/", null);
                TransferResult p1 = rt.Pull(remote + "conv/", new string[] { "conv.jsonl" }, back, bk);
                T.Check("seconde : seule la fin est recue", p1.Success && p1.Literal < 1024 * 1024 && p1.Matched > 6 * 1024 * 1024, "envoye " + p1.Literal + " reconnu " + p1.Matched);
                T.Check("identique a la source, ancienne version archivee", Hash(f) == Hash(Path.Combine(back, "conv.jsonl")) && File.Exists(Path.Combine(bk, "conv.jsonl")));
            });

            T.Run("T54 SSH : cle non autorisee -> refus (pas de nouvel essai), delai de connexion court", delegate
            {
                string badKey = Path.Combine(Dir("cle-inconnue"), "id_inconnue");         // cle neuve, jamais declaree au serveur
                if (!Enroller.GenerateKey(tools, badKey, "contre-essai").ok) { Console.WriteLine("  (generation de la cle de contre-essai impossible : test ignore)"); return; }
                SshTarget bad = new SshTarget { SshExe = tg.SshExe, KeyFile = badKey, KnownHosts = tg.KnownHosts, Host = tg.Host, User = tg.User, Port = tg.Port, ConnectTimeoutSeconds = 5 };
                string loc = Dir("iloc5"); Txt(Path.Combine(loc, "a.txt"), "x", D0);
                RsyncTransport rt = new RsyncTransport(new RsyncOptions { ToolsDir = tools, TimeoutSeconds = 10 }, new RsyncRemote { Ssh = bad });
                TransferResult r = rt.Push(loc, new string[] { "a.txt" }, remote + "refus/", null);
                T.Check("refus d'authentification", r.Fatal == HubErrKind.Refused && r.Done.Count == 0, r.Fatal + " " + r.ErrorText);
            });
        }
    }
}
