// Claude-Sync V2 : tests de la reprise de la memoire de la V1 (migration). Etat de la V1 SIMULE dans un dossier d'essai : jamais les vrais fichiers.
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class MigrateTests
{
    static string Mk(string name) { string d = Path.Combine(Env.Tmp, "migr", name); if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); return d; }

    static string V1State(string name, string hubId = "hub-abc")
    {
        string d = Mk(name);
        File.WriteAllText(Path.Combine(d, "hub.id"), hubId);
        File.WriteAllText(Path.Combine(d, "base-projets.tsv"), "appli\\main.py\t135\t639263567673518955\nUltimate\\.git\\objects\\a4\\6e20\t4093\t639193203584071724\nligne illisible\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(d, "base-conversations.tsv"), "proj1/conv.jsonl\t3200000\t639263567673518956\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(d, "attente-projets.tsv"), "vieux\\fichier.txt\t639263567673518000\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(d, "conflits.tsv"), "", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(d, "config.ini"), "hub=\\\\nas\\partage\n");            // ne doit JAMAIS etre copie
        File.WriteAllText(Path.Combine(d, "log-20261110.txt"), "journal V1");
        return d;
    }

    public static void Run()
    {
        T.Run("T190 migration : meme hub -> memoire reprise (chemins a \\ convertis), V1 jamais modifiee", delegate
        {
            string v1 = V1State("v1a"), v2 = Mk("v2a");
            var before = Directory.GetFiles(v1).ToDictionary(f => f, f => File.ReadAllText(f));
            var r = Migrator.Run(v1, v2, "hub-abc", true, false, () => false);
            T.Check("reussie", r.Ok && r.Applied, r.Message);
            var s = new StateStore(v2);
            var b = s.LoadBase("projets");
            T.Check("la base des projets est reprise, chemins en /", b.Count == 2 && b.ContainsKey("appli/main.py") && b.ContainsKey("Ultimate/.git/objects/a4/6e20") && b["appli/main.py"].Size == 135 && b["appli/main.py"].Ticks == 639263567673518955, string.Join(",", b.Keys));
            T.Check("le fichier ecrit ne contient plus de \\", !File.ReadAllText(Path.Combine(v2, "base-projets.tsv")).Contains('\\'));
            T.Check("la base des conversations est reprise", s.LoadBase("conversations").Count == 1);
            T.Check("les suppressions deja vues sont reprises", s.LoadPending("projets").ContainsKey("vieux/fichier.txt"));
            T.Check("identite du hub reprise : la V2 reconnait le meme hub", s.KnownHubId == "hub-abc");
            T.Check("une ligne illisible est comptee, sans bloquer", r.Malformed == 1, r.Malformed.ToString());
            T.Check("config.ini et journaux de la V1 NON copies", !File.Exists(Path.Combine(v2, "config.ini")) && !File.Exists(Path.Combine(v2, "log-20261110.txt")));
            T.Check("fichiers de la V1 strictement inchanges", before.All(kv => File.ReadAllText(kv.Key) == kv.Value));
        });

        T.Run("T191 migration : hub different, V1 en marche, rien a reprendre -> refus net, rien n'est ecrit", delegate
        {
            string v1 = V1State("v1b"), v2 = Mk("v2b");
            var r1 = Migrator.Run(v1, v2, "AUTRE-HUB", true, false, () => false);
            T.Check("hub different : refuse avec un message clair", !r1.Ok && r1.Message.Contains("PAS le meme hub"), r1.Message);
            T.Check("rien n'a ete copie", Directory.GetFiles(v2).Length == 0);
            var r2 = Migrator.Run(v1, v2, "hub-abc", true, false, () => true);
            T.Check("V1 encore lancee : refuse", !r2.Ok && r2.Message.Contains("tourne encore"), r2.Message);
            var r3 = Migrator.Run(v1, v2, "hub-abc", true, true, () => true);
            T.Check("--allow-running (repetition) : accepte", r3.Ok, r3.Message);
            string vide = Mk("v1vide");
            T.Check("pas de memoire V1 : refus explicite", !Migrator.Run(vide, Mk("v2c"), "hub-abc", true, false, () => false).Ok);
            T.Check("dossier V1 absent : refus explicite", !Migrator.Run(Path.Combine(Env.Tmp, "migr", "nexistepas"), Mk("v2d"), "hub-abc", true, false, () => false).Ok);
            string sansId = V1State("v1sans"); File.Delete(Path.Combine(sansId, "hub.id"));
            T.Check("V1 sans hub.id : refus (memoire non rattachable)", !Migrator.Run(sansId, Mk("v2e"), "hub-abc", true, false, () => false).Ok);
            T.Check("identite du serveur V2 inconnue : refus", !Migrator.Run(v1, Mk("v2f"), null, true, false, () => false).Ok);
        });

        T.Run("T192 migration : simulation sans ecriture, memoire V2 existante mise de cote (jamais ecrasee)", delegate
        {
            string v1 = V1State("v1g"), v2 = Mk("v2g");
            var sim = Migrator.Run(v1, v2, "hub-abc", false, false, () => false);
            T.Check("simulation reussie, annoncee", sim.Ok && !sim.Applied && sim.Message.Contains("SIMULATION") && sim.Files.Count >= 4, sim.Message);
            T.Check("rien n'est ecrit en simulation", Directory.GetFiles(v2).Length == 0);
            File.WriteAllText(Path.Combine(v2, "base-projets.tsv"), "ancien\t1\t2\n"); File.WriteAllText(Path.Combine(v2, "hub.id"), "ancien-hub");
            var r = Migrator.Run(v1, v2, "hub-abc", true, false, () => false);
            T.Check("memoire V2 precedente sauvegardee", r.Ok && r.BackupDir != null && File.ReadAllText(Path.Combine(r.BackupDir, "base-projets.tsv")).StartsWith("ancien") && File.ReadAllText(Path.Combine(r.BackupDir, "hub.id")) == "ancien-hub", r.Message);
            T.Check("puis remplacee par celle de la V1", new StateStore(v2).LoadBase("projets").ContainsKey("appli/main.py"));
            var r2 = Migrator.Run(v1, v2, "hub-abc", true, false, () => false);
            T.Check("rejouable sans dommage (meme resultat)", r2.Ok && new StateStore(v2).LoadBase("projets").Count == 2);
        });
    }
}
