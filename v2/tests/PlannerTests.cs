// Claude-Sync V2 : tests du planificateur (jalon J3, morceau 1). Chaque regle de la V1 a son cas. Aucun disque, aucun reseau.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class PlannerTests
{
    const long Sec = 10_000_000L;
    static readonly long Now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;
    static readonly long Old = Now - 3600 * Sec;                  // modifie il y a 1 h : stable
    static readonly SyncSet Normal = new() { Name = "projets" };
    static readonly SyncSet Newest = new() { Name = "sessions", NewestWins = true };

    static FileEntry E(long size, long ticks) => new() { Rel = "x", Size = size, Ticks = ticks };
    static Dictionary<string, FileEntry> D(params (string rel, FileEntry? e)[] items)
    {
        var d = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, e) in items) if (e != null) d[rel] = e;
        return d;
    }

    // Planifie un seul fichier "f" et retourne (action, note, planification complete).
    static (Act kind, string? note, PlanResult r) One(FileEntry? l, FileEntry? h, FileEntry? b, Action<PlanInput>? cfg = null)
    {
        var inp = new PlanInput { Set = Normal, L = D(("f", l)), H = D(("f", h)), B = D(("f", b)), NowTicks = Now, StableTicks = 3 * Sec };
        cfg?.Invoke(inp);
        var r = Planner.PlanSet(inp);
        var it = r.Items.FirstOrDefault(i => i.Rel == "f");
        return it == null ? ((Act)(-1), null, r) : (it.Kind, it.Note, r);
    }

    static void Is(string nom, Act attendu, (Act kind, string? note, PlanResult r) got) =>
        T.Check(nom, got.kind == attendu, "obtenu " + got.kind + " attendu " + attendu);

    public static void Run()
    {
        T.Run("T60 cas de base : un seul cote, ou identiques", delegate
        {
            Is("seulement local, jamais synchronise -> envoi", Act.Push, One(E(5, Old), null, null));
            Is("seulement serveur, jamais synchronise -> recuperation", Act.Pull, One(null, E(5, Old), null));
            Is("identiques -> rien", Act.Same, One(E(5, Old), E(5, Old), E(5, Old)));
            Is("identiques sans base -> rien", Act.Same, One(E(5, Old), E(5, Old), null));
            Is("absent partout mais connu de la base -> oublier", Act.Forget, One(null, null, E(5, Old)));
            T.Check("absent partout et inconnu : aucun element", One(null, null, null).r.Items.Count == 0);
        });

        T.Run("T61 tolerance de 2 s sur les dates, taille exacte", delegate
        {
            Is("ecart de 2,0 s : identiques", Act.Same, One(E(5, Old), E(5, Old + 2 * Sec), null));
            T.Check("ecart de 2,0000001 s : different (sans base : conflit)", One(E(5, Old), E(5, Old + 2 * Sec + 1), null).kind == Act.Conflict);
            T.Check("meme date, taille differente : different", One(E(5, Old), E(6, Old), null).kind == Act.Conflict);
        });

        T.Run("T62 modification d'un seul cote, base connue", delegate
        {
            Is("local modifie, serveur = base -> envoi", Act.Push, One(E(9, Old + 100 * Sec), E(5, Old), E(5, Old)));
            Is("serveur modifie, local = base -> recuperation", Act.Pull, One(E(5, Old), E(9, Old + 100 * Sec), E(5, Old)));
        });

        T.Run("T63 modification des deux cotes : conflit, ou le plus recent gagne pour les jeux de metadonnees", delegate
        {
            var l = E(7, Old + 50 * Sec); var h = E(8, Old + 90 * Sec); var b = E(5, Old);
            Is("modifies des deux cotes avec base -> conflit", Act.Conflict, One(l, h, b));
            Is("modifies des deux cotes sans base -> conflit", Act.Conflict, One(l, h, null));
            Is("jeu 'le plus recent gagne' : local plus recent -> envoi", Act.Push, One(h, l, b, i => i.Set = Newest));
            Is("jeu 'le plus recent gagne' : serveur plus recent -> recuperation", Act.Pull, One(l, h, b, i => i.Set = Newest));
            var same = One(E(5, Old + 50 * Sec), E(5, Old + 90 * Sec), E(4, Old), i => i.SameContent = rel => true);
            T.Check("contenu identique malgre les dates : rien a copier (note 'touch')", same.kind == Act.Same && same.note == "touch");
            Is("contenu comparable mais different -> conflit", Act.Conflict, One(E(5, Old + 50 * Sec), E(5, Old + 90 * Sec), E(4, Old), i => i.SameContent = rel => false));
            Is("tailles differentes : le contenu n'est pas compare", Act.Conflict, One(E(5, Old + 50 * Sec), E(6, Old + 90 * Sec), E(4, Old), i => i.SameContent = rel => throw new Exception("ne doit pas etre appele")));
        });

        T.Run("T64 suppression par l'autre PC : inchange ici -> corbeille locale, modifie -> la modification l'emporte", delegate
        {
            Is("supprime sur le serveur, local = base -> corbeille locale", Act.TrashLocal, One(E(5, Old), null, E(5, Old), i => i.StableTicks = 3 * Sec));
            Is("supprime sur le serveur, mais modifie ici -> envoi", Act.Push, One(E(9, Old + 10 * Sec), null, E(5, Old)));
            Is("supprime ici, serveur = base -> corbeille du serveur", Act.TrashHub, One(null, E(5, Old), E(5, Old)));
            Is("supprime ici, mais modifie sur le serveur -> recuperation", Act.Pull, One(null, E(9, Old + 10 * Sec), E(5, Old)));
            Is("supprime ici, jamais synchronise -> recuperation (pas de base)", Act.Pull, One(null, E(5, Old), null));
        });

        T.Run("T65 delai de stabilite d'une suppression : double verification puis execution", delegate
        {
            var first = One(null, E(5, Old), E(5, Old), i => { i.StableTicks = 3 * Sec; });
            T.Check("Claude ferme, 1re observation : suppression gardee avec la note 'recheck'", first.kind == Act.TrashHub && first.note == "recheck");
            T.Check("la 1re observation est memorisee a l'heure actuelle", first.r.NewPending.TryGetValue("f", out var t) && t == Now);
            var second = One(null, E(5, Old), E(5, Old), i => { i.StableTicks = 3 * Sec; i.Pending = new Dictionary<string, long> { ["f"] = Now - 10 * Sec }; });
            T.Check("deja vue depuis 10 s (> 3 s) : suppression executable, sans note", second.kind == Act.TrashHub && second.note == null);
            T.Check("la date de 1re observation est conservee", second.r.NewPending["f"] == Now - 10 * Sec);
            var open1 = One(null, E(5, Old), E(5, Old), i => { i.StableTicks = 60 * Sec; i.ClaudeRunning = true; });
            Is("Claude ouvert, 1re observation -> suppression en attente (60 s)", Act.PendingDelete, open1);
            Is("Claude ouvert, vue depuis 30 s -> encore en attente", Act.PendingDelete, One(null, E(5, Old), E(5, Old), i => { i.StableTicks = 60 * Sec; i.ClaudeRunning = true; i.Pending = new Dictionary<string, long> { ["f"] = Now - 30 * Sec }; }));
            Is("Claude ouvert, vue depuis 70 s -> executable", Act.TrashHub, One(null, E(5, Old), E(5, Old), i => { i.StableTicks = 60 * Sec; i.ClaudeRunning = true; i.Pending = new Dictionary<string, long> { ["f"] = Now - 70 * Sec }; }));
            T.Check("un fichier plus supprime n'est plus 'en attente'", One(E(5, Old), E(5, Old), E(5, Old), i => i.Pending = new Dictionary<string, long> { ["f"] = Now - 10 * Sec }).r.NewPending.Count == 0);
        });

        T.Run("T66 protections : erreurs de lecture, Claude ouvert, recuperation interdite", delegate
        {
            Is("erreurs de lecture : suppression ignoree (local)", Act.Ignored, One(E(5, Old), null, E(5, Old), i => i.NoDelete = true));
            Is("erreurs de lecture : suppression ignoree (serveur)", Act.Ignored, One(null, E(5, Old), E(5, Old), i => i.NoDelete = true));
            Is("erreurs de lecture : un envoi normal reste possible", Act.Push, One(E(9, Old + 10 * Sec), E(5, Old), E(5, Old), i => i.NoDelete = true));
            Is("Claude ouvert : la corbeille locale est bloquee", Act.PullBlocked, One(E(5, Old), null, E(5, Old), i => i.ClaudeRunning = true));
            Is("Claude ouvert : aucune recuperation", Act.PullBlocked, One(E(5, Old), E(9, Old + 100 * Sec), E(5, Old), i => i.ClaudeRunning = true));
            Is("recuperation interdite (NoPull)", Act.PullBlocked, One(null, E(5, Old), null, i => i.NoPull = true));
            Is("Claude ouvert : un envoi reste possible", Act.Push, One(E(9, Old + 100 * Sec), E(5, Old), E(5, Old), i => { i.ClaudeRunning = true; i.StableTicks = 60 * Sec; }));
        });

        T.Run("T67 fichier encore en cours d'ecriture : reporte", delegate
        {
            Is("modifie il y a 1 s, delai 3 s -> instable (reporte)", Act.Unstable, One(E(9, Now - 1 * Sec), E(5, Old), E(5, Old)));
            Is("modifie il y a 5 s, delai 3 s -> envoi", Act.Push, One(E(9, Now - 5 * Sec), E(5, Old), E(5, Old)));
            Is("Claude ouvert (60 s) : modifie il y a 30 s -> instable", Act.Unstable, One(E(9, Now - 30 * Sec), E(5, Old), E(5, Old), i => { i.StableTicks = 60 * Sec; i.ClaudeRunning = true; }));
            Is("un conflit sur un fichier instable est reporte aussi", Act.Unstable, One(E(7, Now - 1 * Sec), E(8, Old + 90 * Sec), E(5, Old)));
        });

        T.Run("T68 garde-fou : un grand jeu soudain vide d'un cote est ignore", delegate
        {
            var base21 = Enumerable.Range(0, 21).Select(i => ("f" + i, (FileEntry?)E(5, Old))).ToArray();
            var full = D(base21);
            var rVide = Planner.PlanSet(new PlanInput { Set = Normal, L = D(), H = full, B = full, NowTicks = Now, StableTicks = 3 * Sec });
            T.Check("local vide, base de 21 fichiers : jeu ignore avec avertissement", rVide.SetSkipped && rVide.Items.Count == 0 && rVide.Warnings.Count == 1 && rVide.Warnings[0].Contains("local est vide"));
            var hVide = Planner.PlanSet(new PlanInput { Set = Normal, L = full, H = D(), B = full, NowTicks = Now, StableTicks = 3 * Sec });
            T.Check("serveur vide, base de 21 fichiers : jeu ignore avec avertissement", hVide.SetSkipped && hVide.Warnings[0].Contains("serveur est vide"));
            var base20 = D(Enumerable.Range(0, 20).Select(i => ("f" + i, (FileEntry?)E(5, Old))).ToArray());
            var petit = Planner.PlanSet(new PlanInput { Set = Normal, L = D(), H = base20, B = base20, NowTicks = Now, StableTicks = 3 * Sec });
            T.Check("base de 20 fichiers seulement : pas de garde-fou, 20 suppressions locales a traiter", !petit.SetSkipped && petit.Items.Count(i => i.Kind == Act.TrashHub) == 20);
            var neuf = Planner.PlanSet(new PlanInput { Set = Normal, L = D(), H = full, B = D(), NowTicks = Now, StableTicks = 3 * Sec });
            T.Check("nouveau PC (base vide, local vide) : tout est recupere", !neuf.SetSkipped && neuf.Items.Count(i => i.Kind == Act.Pull) == 21);
        });

        T.Run("T69 noms : la casse ne cree pas de doublon ; ordre deterministe", delegate
        {
            var r = Planner.PlanSet(new PlanInput { Set = Normal, L = D(("Dossier/A.txt", E(5, Old)), ("b.txt", E(1, Old))), H = D(("dossier/a.txt", E(5, Old)), ("c.txt", E(2, Old))), B = D(), NowTicks = Now, StableTicks = 3 * Sec });
            T.Check("A.txt / a.txt : un seul element, identiques", r.Items.Count(i => i.Rel.Equals("dossier/a.txt", StringComparison.OrdinalIgnoreCase)) == 1 && r.Items.First(i => i.Rel.Equals("dossier/a.txt", StringComparison.OrdinalIgnoreCase)).Kind == Act.Same);
            T.Check("ordre alphabetique stable", string.Join(",", r.Items.Select(i => i.Rel)) == "b.txt,c.txt,Dossier/A.txt" || string.Join(",", r.Items.Select(i => i.Rel)) == "b.txt,c.txt,dossier/a.txt", string.Join(",", r.Items.Select(i => i.Rel)));
            var again = Planner.PlanSet(new PlanInput { Set = Normal, L = D(("b.txt", E(1, Old))), H = D(("c.txt", E(2, Old))), B = D(), NowTicks = Now, StableTicks = 3 * Sec });
            T.Check("b envoye, c recupere", again.Items[0].Kind == Act.Push && again.Items[1].Kind == Act.Pull);
        });

        T.Run("T70 la planification ne modifie pas ses entrees", delegate
        {
            var l = D(("f", E(5, Old))); var h = D(("f", null)); var b = D(("f", E(5, Old)));
            var pend = new Dictionary<string, long>();
            Planner.PlanSet(new PlanInput { Set = Normal, L = l, H = h, B = b, NowTicks = Now, StableTicks = 3 * Sec, Pending = pend });
            T.Check("dictionnaires d'entree intacts", l.Count == 1 && h.Count == 0 && b.Count == 1 && pend.Count == 0);
        });
    }
}
