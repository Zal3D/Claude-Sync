// Claude-Sync V2 : tests des jeux de fichiers, du scan local et du filtrage du serveur (jalon J3, morceau 4).
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class SetDefsTests
{
    static void Write(string root, string rel, string text = "x")
    {
        string p = Path.Combine(root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    static string Keys(IEnumerable<string> k) => string.Join(",", k.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    public static void Run()
    {
        T.Run("T71 les cinq jeux : racines, regles et exclusions comme en V1", delegate
        {
            var sets = SetDefs.Build(@"C:\Users\x", @"D:\Projets", @"C:\Idx");
            T.Check("5 jeux dans l'ordre des noms", string.Join(",", sets.Select(s => s.Name)) == string.Join(",", SetDefs.Names));
            T.Check("conversations -> .claude\\projects", sets[0].LocalRoot == @"C:\Users\x\.claude\projects");
            T.Check("reglages : .claude, plus recent gagne, sans sous-dossiers", sets[1].LocalRoot == @"C:\Users\x\.claude" && sets[1].NewestWins && sets[1].NoSubdirs);
            T.Check("reglages : seulement settings.json", sets[1].FileOk!("settings.json") && sets[1].FileOk!("Settings.JSON") && !sets[1].FileOk!("config.json") && !sets[1].FileOk!(".credentials.json"));
            T.Check("historique -> .claude\\file-history", sets[2].LocalRoot == @"C:\Users\x\.claude\file-history" && !sets[2].NewestWins);
            T.Check("projets : racine donnee, exclusions V1", sets[3].LocalRoot == @"D:\Projets" && new[] { "node_modules", "__pycache__", ".venv", "venv", ".mypy_cache", ".pytest_cache", "$RECYCLE.BIN", "System Volume Information" }.All(sets[3].ExcludedDirs.Contains));
            T.Check("sessions : plus recent gagne, seulement local_*.json", sets[4].NewestWins && sets[4].LocalRoot == @"C:\Idx" && sets[4].FileOk!("local_ab.json") && !sets[4].FileOk!("local_ab.txt") && !sets[4].FileOk!("autre.json"));
            T.Check("le dossier de reprise est exclu de TOUS les jeux", sets.All(s => s.ExcludedDirs.Contains(SetDefs.PartialDir)));
            T.Check("dossiers des jeux dans le hub", SetDefs.HubDir("conversations") == "/claude/projects/" && SetDefs.HubDir("sessions") == "/desktop/sessions/");
            T.Throws("jeu inconnu refuse", HubErrKind.Refused, () => { try { SetDefs.HubDir("zzz"); } catch (ArgumentException e) { throw new HubException(HubErrKind.Refused, e.Message); } });
        });

        T.Run("T72 scan local : chemins en '/', exclusions, fichiers ignores, jeux restreints", delegate
        {
            string root = Path.Combine(Env.Tmp, "scan1");
            Write(root, "a.txt"); Write(root, "d/é b.txt"); Write(root, "d/e/f.txt");
            Write(root, "node_modules/m/x.js"); Write(root, ".venv/v.py"); Write(root, "d/__pycache__/c.pyc");
            Write(root, ".rsync-partial/gros.bin"); Write(root, "d/.rsync-partial/y");
            Write(root, "Thumbs.db"); Write(root, "desktop.ini"); Write(root, "~$doc.docx"); Write(root, "t.tmp"); Write(root, "z.syncpart-PC1");
            var proj = SetDefs.Build(@"C:\u", root, null)[3];
            var l = SetDefs.ScanLocal(proj, out int links, out int errs);
            T.Check("seulement les vrais fichiers, chemins en '/'", Keys(l.Keys) == "a.txt,d/e/f.txt,d/é b.txt", Keys(l.Keys));
            T.Check("pas de lien, pas d'erreur", links == 0 && errs == 0);
            T.Check("taille et date lues", l["a.txt"].Size == 1 && Math.Abs(l["a.txt"].Ticks - File.GetLastWriteTimeUtc(Path.Combine(root, "a.txt")).Ticks) == 0);
            T.Check("cle insensible a la casse", l.ContainsKey("D/E/F.TXT"));

            string cl = Path.Combine(Env.Tmp, "scan2");
            Write(cl, ".claude/settings.json"); Write(cl, ".claude/config.json"); Write(cl, ".claude/.credentials.json"); Write(cl, ".claude/projects/p.jsonl"); Write(cl, ".claude/sous/settings.json");
            var reg = SetDefs.Build(cl, @"C:\p", null)[1];
            var lr = SetDefs.ScanLocal(reg, out _, out _);
            T.Check("reglages : settings.json seulement, jamais config.json ni les identifiants", Keys(lr.Keys) == "settings.json", Keys(lr.Keys));

            string idx = Path.Combine(Env.Tmp, "scan3");
            Write(idx, "u1/u2/local_aaa.json"); Write(idx, "u1/u2/local_bbb.txt"); Write(idx, "u1/u2/autre.json");
            var sess = SetDefs.Build(@"C:\u", @"C:\p", idx)[4];
            T.Check("sessions : local_*.json seulement", Keys(SetDefs.ScanLocal(sess, out _, out _).Keys) == "u1/u2/local_aaa.json");

            var absent = SetDefs.Build(@"C:\u", Path.Combine(Env.Tmp, "n-existe-pas"), null)[3];
            var none = SetDefs.ScanLocal(absent, out _, out int e2);
            T.Check("racine absente : liste vide (le planificateur decide), pas d'exception", none.Count == 0 && e2 == 0);
        });

        T.Run("T73 le serveur est filtre comme le scan local", delegate
        {
            var proj = SetDefs.Build(@"C:\u", @"C:\p", null)[3];
            var hub = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in new[] { "a.txt", "d/b.txt", "node_modules/x.js", "d/node_modules/y.js", ".venv/z", "d/.rsync-partial/p", "Thumbs.db", "d/desktop.ini", "~$w.docx", "q.tmp", "u.syncpart-PC" })
                hub[r] = new FileEntry { Rel = r, Size = 1, Ticks = 1 };
            T.Check("projets : seuls a.txt et d/b.txt restent", Keys(SetDefs.FilterHub(hub, proj).Keys) == "a.txt,d/b.txt", Keys(SetDefs.FilterHub(hub, proj).Keys));
            var reg = SetDefs.Build(@"C:\u", @"C:\p", null)[1];
            var hubReg = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in new[] { "settings.json", "config.json", "sous/settings.json" }) hubReg[r] = new FileEntry { Rel = r, Size = 1, Ticks = 1 };
            T.Check("reglages : settings.json a la racine seulement", Keys(SetDefs.FilterHub(hubReg, reg).Keys) == "settings.json");
        });

        T.Run("T74 le serveur et le client s'accordent : dossiers des jeux, fichiers ignores", delegate
        {
            string root = Env.NewHub();
            foreach (var set in SetDefs.Names)
            {
                string dir = SetDefs.HubDir(set).Trim('/').Replace('/', '\\');
                Directory.CreateDirectory(Path.Combine(root, dir));
                File.WriteAllText(Path.Combine(root, dir, set == "reglages" ? "settings.json" : set == "sessions" ? "local_x.json" : "f.txt"), "x");
                foreach (var junk in new[] { "Thumbs.db", "desktop.ini", "~$a.docx", "b.tmp", "c.syncpart-PC" }) File.WriteAllText(Path.Combine(root, dir, junk), "x");
            }
            using var h = Env.Open(root);
            foreach (var set in SetDefs.Names)
            {
                var l = h.List(set, null);
                T.Check($"{set} : le serveur voit le fichier du jeu et ignore Thumbs.db, desktop.ini, ~$, .tmp, .syncpart", l.Files!.Count == 1, Keys(l.Files.Keys));
            }
        });
    }
}
