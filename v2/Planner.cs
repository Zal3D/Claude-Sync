// Claude-Sync V2 : le planificateur (morceau 1 du moteur). Fonction PURE : aucun acces au disque, au reseau ni a l'heure.
// Reprend a l'identique les regles du BuildPlan de la V1 (comparaison a trois : L = local, H = serveur, B = dernier etat synchronise).
//   - seul L a change -> envoi ; seul H a change -> recuperation ; les deux -> conflit (ou "le plus recent gagne" pour les jeux de metadonnees)
//   - fichier disparu d'un cote : si l'autre cote est inchange -> corbeille ; sinon la modification l'emporte sur la suppression
//   - une suppression n'est executee qu'apres un delai de stabilite (double verification) ; jamais si des erreurs de lecture ont eu lieu
//   - jeu ignore par precaution si un grand jeu (> 20 fichiers) devient soudain vide d'un cote
#nullable enable
using System;
using System.Collections.Generic;

namespace ClaudeSync.V2;

public sealed class PlanInput
{
    public SyncSet Set = null!;                                   // nom et regles du jeu (NewestWins...)
    public IReadOnlyDictionary<string, FileEntry> L = null!;      // etat local
    public IReadOnlyDictionary<string, FileEntry> H = null!;      // etat du serveur
    public IReadOnlyDictionary<string, FileEntry> B = null!;      // dernier etat synchronise
    public bool NoDelete;                                         // erreurs de lecture d'un cote : aucune suppression deduite
    public bool ClaudeRunning;                                    // Claude ouvert : jamais d'ecriture locale (envois seulement)
    public bool NoPull;
    public long NowTicks;
    public long StableTicks;                                      // delai de stabilite : 3 s Claude ferme, 60 s Claude ouvert
    public IReadOnlyDictionary<string, long>? Pending;            // suppressions deja vues (date de premiere observation)
    public Func<string, bool>? SameContent;                       // comparaison de contenu si tout a change des deux cotes sans base (null = different)
}

public sealed class PlanResult
{
    public List<PlanItem> Items = new();
    public Dictionary<string, long> NewPending = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings = new();
    public bool SetSkipped;                                       // jeu ignore par precaution (rien n'est fait pour ce jeu)
}

public static class Planner
{
    public const int EmptyGuard = 20;
    const long ShortDelay = 30_000_000L;                          // 3 s : "double verification dans la meme synchro"

    public static PlanResult PlanSet(PlanInput x)
    {
        var r = new PlanResult();
        string name = x.Set.Name;
        if (x.B.Count > EmptyGuard && x.L.Count == 0)
        {
            r.Warnings.Add($"[{name}] le dossier local est vide alors que la synchro en connaissait {x.B.Count} fichiers : jeu ignore par precaution.");
            r.SetSkipped = true; return r;
        }
        if (x.B.Count > EmptyGuard && x.H.Count == 0)
        {
            r.Warnings.Add($"[{name}] le dossier du serveur est vide alors que la synchro en connaissait {x.B.Count} fichiers : jeu ignore par precaution.");
            r.SetSkipped = true; return r;
        }

        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in x.L.Keys) keys.Add(k);
        foreach (var k in x.H.Keys) keys.Add(k);
        foreach (var k in x.B.Keys) keys.Add(k);

        foreach (string rel in keys)
        {
            x.L.TryGetValue(rel, out var l); x.H.TryGetValue(rel, out var h); x.B.TryGetValue(rel, out var b);
            var kind = Act.Same; string? note = null;

            if (l == null && h == null)
            {
                if (b != null) r.Items.Add(new PlanItem { Set = x.Set, Rel = rel, B = b, Kind = Act.Forget });
                continue;
            }
            if (l != null && h == null)
                kind = (b != null && FileEntry.Same(l, b)) ? Act.TrashLocal : Act.Push;      // supprime sur le serveur : inchange ici -> corbeille locale, sinon la modification l'emporte
            else if (l == null && h != null)
                kind = (b != null && FileEntry.Same(h, b)) ? Act.TrashHub : Act.Pull;        // supprime ici : serveur inchange -> corbeille du serveur, sinon la modification de l'autre PC l'emporte
            else if (FileEntry.Same(l!, h!))
                kind = Act.Same;
            else
            {
                bool lc = b == null || !FileEntry.Same(l!, b);
                bool hc = b == null || !FileEntry.Same(h!, b);
                if (b != null && lc && !hc) kind = Act.Push;
                else if (b != null && !lc && hc) kind = Act.Pull;
                else if (b != null && !lc && !hc) kind = Act.Same;
                else
                {
                    bool same = l!.Size == h!.Size && x.SameContent != null && x.SameContent(rel);
                    if (same) { kind = Act.Same; note = "touch"; }
                    else if (x.Set.NewestWins) kind = l.Ticks >= h.Ticks ? Act.Push : Act.Pull;
                    else kind = Act.Conflict;
                }
            }

            if (kind == Act.TrashHub || kind == Act.TrashLocal)
            {
                if (x.NoDelete) kind = Act.Ignored;
                else if (kind == Act.TrashLocal && (x.ClaudeRunning || x.NoPull)) kind = Act.PullBlocked;
                else
                {
                    long first = (x.Pending != null && x.Pending.TryGetValue(rel, out var f)) ? f : x.NowTicks;
                    r.NewPending[rel] = first;
                    if (x.NowTicks - first < x.StableTicks)
                    {
                        if (x.StableTicks <= ShortDelay) note = "recheck";    // delai court (Claude ferme) : double verification dans la meme synchro
                        else kind = Act.PendingDelete;
                    }
                }
            }
            if ((kind == Act.Push || kind == Act.Conflict) && l != null && x.NowTicks - l.Ticks < x.StableTicks) kind = Act.Unstable;
            if (kind == Act.Pull && (x.ClaudeRunning || x.NoPull)) kind = Act.PullBlocked;
            r.Items.Add(new PlanItem { Set = x.Set, Rel = rel, L = l, H = h, B = b, Kind = kind, Note = note });
        }
        return r;
    }
}
