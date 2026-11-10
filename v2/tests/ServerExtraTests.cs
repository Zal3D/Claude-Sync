// Claude-Sync V2 : tests des commandes ajoutees au serveur pour le moteur (identifiant du hub, hash, touch, erreurs de listage, corbeille).
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class ServerExtraTests
{
    static readonly DateTime D0 = new(2026, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc);

    static void W(string root, string rel, string text)
    {
        string p = Path.Combine(root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text, new UTF8Encoding(false));
    }

    public static void Run()
    {
        T.Run("T26 identifiant du hub : cree une fois s'il manque, jamais ecrase", delegate
        {
            string root = Path.Combine(Env.Tmp, "hubsansid"); Directory.CreateDirectory(root);
            string id1, id2;
            using (var h = Env.Open(root)) id1 = h.Hello().HubId;
            using (var h = Env.Open(root)) id2 = h.Hello().HubId;
            T.Check("un identifiant est cree", id1.Length >= 16, id1);
            T.Check("le meme pour toutes les sessions", id1 == id2);
            T.Check("ecrit dans _etat/hub.id", File.ReadAllText(Path.Combine(root, "_etat", "hub.id")).Trim() == id1);
            File.WriteAllText(Path.Combine(root, "_etat", "hub.id"), "choisi-a-la-main");
            using (var h = Env.Open(root)) T.Check("un identifiant existant n'est jamais ecrase", h.Hello().HubId == "choisi-a-la-main");
        });

        T.Run("T27 hash et touch : empreinte calculee par le serveur, date remise a l'identique", delegate
        {
            string root = Env.NewHub(); W(root, "projets/d/é x.txt", "contenu à hacher");
            using var h = Env.Open(root);
            string exp = BitConverter.ToString(SHA256.Create().ComputeHash(File.ReadAllBytes(Path.Combine(root, "projets", "d", "é x.txt")))).Replace("-", "").ToLowerInvariant();
            T.Check("SHA-256 identique a celui du PC", h.Hash("projets", "d/é x.txt") == exp);
            T.Check("fichier absent -> null", h.Hash("projets", "nope.txt") == null);
            T.Check("touch : fichier existant", h.Touch("projets", "d/é x.txt", D0.Ticks));
            T.Check("la date est exactement celle demandee", h.Stat("projets", new[] { "d/é x.txt" })["d/é x.txt"].Ticks == D0.Ticks);
            T.Check("touch : fichier absent -> faux", !h.Touch("projets", "nope.txt", D0.Ticks));
            T.Throws("chemin dangereux refuse (hash)", HubErrKind.Refused, () => h.Hash("projets", "../x"));
            T.Throws("chemin dangereux refuse (touch)", HubErrKind.Refused, () => h.Touch("projets", "../x", 1));
        });

        T.Run("T28 listage : les erreurs de lecture du serveur sont transmises au moteur", delegate
        {
            using (var h = Env.Open(Env.NewHub())) T.Check("serveur sain : 0 erreur", h.List("projets", null).Errors == 0);
            var f = Env.Fake(Env.FakeReply + "for l in sys.stdin.buffer:\n    rep({'ok':True,'token':'t','count':1,'gz':True,'errors':2}, gzip.compress(b'a\\t1\\t100\\n'))\n", 5000);
            T.Check("2 erreurs annoncees -> ListResult.Errors = 2", f.List("projets", null).Errors == 2);
            f.Dispose();
        });

        T.Run("T29 corbeille du serveur : liste (PC, titres des sessions), restauration sans ecrasement", delegate
        {
            string root = Env.NewHub(); string lot = "20261109-100000";
            W(root, "_corbeille/" + lot + "/_par.txt", "PC-A");
            W(root, "_corbeille/" + lot + "/projets/p/q/a.txt", "AAA");
            W(root, "_corbeille/" + lot + "/projets/p/b.txt", "BB");
            W(root, "_corbeille/" + lot + "/sessions/u/local_1.json", "{\"cliSessionId\":\"cli-123\",\"title\":\"Ma session \\\"test\\\"\"}");
            W(root, "_corbeille/" + lot + "/conversations/proj/cli-123.jsonl", "x");
            W(root, "_corbeille/pas-un-lot/projets/z.txt", "ignore");
            using var h = Env.Open(root);
            var tl = h.TrashList();
            T.Check("un lot reconnu, PC lu dans _par.txt", tl.PcByStamp.Count == 1 && tl.PcByStamp[lot] == "PC-A", string.Join(",", tl.PcByStamp.Keys));
            T.Check("4 fichiers (sans _par.txt, sans lot invalide)", tl.Files.Count == 4, tl.Files.Count.ToString());
            var fiche = tl.Files.First(f => f.Set == "sessions");
            T.Check("fiche de session : identifiant et titre extraits par le serveur", fiche.Cli == "cli-123" && fiche.Title.Contains("Ma session"), fiche.Cli + "|" + fiche.Title);
            T.Check("tailles lues", tl.Files.First(f => f.Rel == "p/q/a.txt").Size == 3);
            T.Check("restauration d'un fichier", h.RestoreFromTrash(lot, "projets", "p/q/a.txt") && File.ReadAllText(Path.Combine(root, "projets", "p", "q", "a.txt")) == "AAA");
            T.Check("il a quitte la corbeille, dossiers vides nettoyes", !Directory.Exists(Path.Combine(root, "_corbeille", lot, "projets", "p", "q")));
            T.Check("deja restaure : faux, sans erreur", !h.RestoreFromTrash(lot, "projets", "p/q/a.txt"));
            W(root, "projets/p/b.txt", "NOUVEAU");
            T.Check("un fichier existe deja a cette place : rien n'est ecrase", !h.RestoreFromTrash(lot, "projets", "p/b.txt") && File.ReadAllText(Path.Combine(root, "projets", "p", "b.txt")) == "NOUVEAU" && File.Exists(Path.Combine(root, "_corbeille", lot, "projets", "p", "b.txt")));
            T.Throws("lot dangereux refuse", HubErrKind.Refused, () => h.RestoreFromTrash("../x", "projets", "a"));
            T.Throws("jeu inconnu refuse", HubErrKind.Refused, () => h.RestoreFromTrash(lot, "zzz", "a"));
        });
    }
}
