// Claude-Sync V2 : le moteur (morceaux 2 et 3 : executeur et session), au-dessus de IHub (serveur) et ITransfer (rsync).
// Memes regles que la V1 ; ce qui change : le serveur liste lui-meme, les copies partent par lots, le verrou est lie a la session,
// et CHAQUE transfert est reverifie aupres du serveur avant d'etre inscrit dans la base.
//   - la base d'un fichier envoye = ce que le serveur contient reellement apres l'envoi (jamais une supposition) ;
//   - un fichier modifie pendant son envoi n'est pas declare synchronise : la base reflete le serveur, le prochain passage renvoie sans conflit ;
//   - rien n'est supprime definitivement ici : corbeille (serveur ou locale) puis purge a 30 jours.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeSync.V2;

public sealed class EngineOptions
{
    public string Pc = Environment.MachineName;
    public string Profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
    public string ProjetsRoot = @"C:\Projets";
    public string? IndexDir;                     // dossier des sessions de Claude Desktop
    public string StateDir = "";
    public bool Apply;                           // faux = simulation (rien n'est ecrit)
    public bool NoPull;
    public bool IgnorePresence;
    public string ClaudeRunning = "auto";        // auto | yes | no | idle (fenetre masquee, processus residant)
    public int StableSeconds = 60;
    public int WaitLockSeconds;                  // attente d'un verrou tenu par l'autre PC
    public int BatchFiles = 200;                 // taille maximale d'un lot rsync, en fichiers...
    public long BatchBytes = 512L << 20;         // ... et en octets (un gros fichier forme un lot a lui seul)
    public int PurgeDays = 30;
    public long RecheckTicks = 32_000_000L;      // double verification d'une suppression : 3,2 s apres l'analyse
    public Func<bool>? ClaudeProbe;              // essais : remplace la detection du processus Claude
}

public sealed class Engine2
{
    const long Sec = 10_000_000L;
    readonly EngineOptions o;
    readonly IHub hub;
    readonly ITransfer tx;
    readonly StateStore store;

    public event Action<ProgressInfo>? OnProgress;
    public ProgressInfo P = new();
    public List<PlanItem> Plan = new();
    public List<ConflictRec> Conflicts = new();
    public int Errors;
    public List<string> Warnings = new();
    public List<string> Notices = new();         // informations non bloquantes (jamais d'icone rouge)
    public int Trashed;
    public string Summary = "";
    public volatile bool StopPulls;              // mode econome : plus aucune ecriture sur le PC, les envois continuent
    public int PullsSkipped;
    bool ignoreBase;
    long planTicks = DateTime.UtcNow.Ticks;
    bool haveLock;

    public Engine2(EngineOptions options, IHub hub, ITransfer transfer)
    {
        o = options; this.hub = hub; tx = transfer;
        store = new StateStore(o.StateDir);
    }

    public StateStore Store => store;
    public List<ConflictRec> LoadConflicts() => store.LoadConflicts();     // meme nom que le moteur V1 (fenetre Conflits)

    // ------------------------------------------------------------------ outils
    public static string Mb(long b) => b >= 1024L * 1024 * 1024
        ? (b / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " Go"
        : (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mo";

    static string HubMessage(HubException e) => e.Kind switch
    {
        HubErrKind.Network => "Serveur injoignable : " + e.Message + ".",
        HubErrKind.Refused => "Acces refuse par le serveur : " + e.Message + ".",
        HubErrKind.Protocol => "Serveur incompatible : " + e.Message + ".",
        _ => "Erreur du serveur : " + e.Message + "."
    };

    void Report(string phase, string current)
    {
        P.Phase = phase; P.Current = current;
        OnProgress?.Invoke(P);
    }

    static string LocalPath(SyncSet s, string rel) => Path.Combine(s.LocalRoot, rel.Replace('/', '\\'));

    static FileEntry? LocalEntry(string path, string rel)
    {
        var fi = new FileInfo(Fs.Long(path));
        return fi.Exists ? new FileEntry { Rel = rel, Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks } : null;
    }

    List<SyncSet> Sets() => SetDefs.Build(o.Profile, o.ProjetsRoot, o.IndexDir);

    public bool ClaudeIsRunning()
    {
        if (o.ClaudeRunning is "yes" or "idle") return true;
        if (o.ClaudeRunning == "no") return false;
        if (o.ClaudeProbe != null) return o.ClaudeProbe();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase)) return true; } catch (Exception) { }
            }
        }
        catch (Exception) { }
        return false;
    }

    // Un autre PC avec Claude ouvert depuis moins de 10 minutes : on ne synchronise pas en meme temps.
    public string? OtherPcActive()
    {
        try
        {
            foreach (var p in hub.ReadPresence())
            {
                if (p.Pc.Equals(o.Pc, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Running && DateTime.UtcNow.Ticks - p.Ticks < 10 * 60 * Sec) return p.Pc;
            }
        }
        catch (HubException) { }
        return null;
    }

    public void WritePresence(bool running)
    {
        try { hub.WritePresence(o.Pc, running); } catch (HubException) { }
    }

    // ------------------------------------------------------------------ plan
    public void BuildPlan(bool claudeRunning)
    {
        bool idle = o.ClaudeRunning == "idle";
        Plan.Clear();
        long now = DateTime.UtcNow.Ticks; planTicks = now;
        // Claude ferme = plus personne n'ecrit : delai de stabilite court (3 s) ; Claude ouvert : delai normal
        long stable = ((claudeRunning && !idle) ? o.StableSeconds : Math.Min(3, o.StableSeconds)) * Sec;
        var oldConf = store.LoadConflicts();
        foreach (var s in Sets())
        {
            Report("Analyse : " + s.Name, "");
            var B = ignoreBase ? new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase) : store.LoadBase(s.Name);
            if (string.IsNullOrEmpty(s.LocalRoot) || !Directory.Exists(Fs.Long(s.LocalRoot)))
            {
                if (s.Name == "sessions" || B.Count > 0)
                {
                    Log.W("  [" + s.Name + "] dossier local introuvable, jeu ignore (aucune suppression deduite) : " + s.LocalRoot);
                    continue;
                }
            }
            int links = 0, localErrors = 0;
            Dictionary<string, FileEntry> L = new(StringComparer.OrdinalIgnoreCase);
            var scan = Task.Factory.StartNew(() => { L = SetDefs.ScanLocal(s, out links, out localErrors); });
            var lr = hub.List(s.Name, null);                       // peut lever HubException : la synchro est reportee
            scan.Wait();
            var H = SetDefs.FilterHub(lr.Files!, s);
            if (links > 0) Log.W("  [" + s.Name + "] " + links + " lien(s) symbolique(s)/jonction(s) ignore(s) en local");
            if (lr.CaseCollisions.Count > 0) Notices.Add("[" + s.Name + "] " + lr.CaseCollisions.Count + " fichier(s) du serveur ne different que par la casse : un seul est utilise (Windows ne sait pas les distinguer).");
            if (localErrors > 0 || lr.Errors > 0) Log.W("  [" + s.Name + "] erreurs de lecture : local " + localErrors + ", serveur " + lr.Errors + " (aucune suppression deduite pour ce jeu)");
            var pending = store.LoadPending(s.Name);
            string root = s.LocalRoot;
            var r = Planner.PlanSet(new PlanInput
            {
                Set = s, L = L, H = H, B = B,
                NoDelete = localErrors > 0 || lr.Errors > 0,
                ClaudeRunning = claudeRunning, NoPull = o.NoPull, NowTicks = now, StableTicks = stable, Pending = pending,
                SameContent = rel =>
                {
                    try { return string.Equals(Fs.Hash(LocalPath(s, rel)), hub.Hash(s.Name, rel), StringComparison.OrdinalIgnoreCase); }
                    catch (Exception) { return false; }
                }
            });
            foreach (var w in r.Warnings) { Log.W("  ATTENTION " + w); Warnings.Add(w); }
            if (r.SetSkipped) continue;
            Plan.AddRange(r.Items);
            if (o.Apply) { try { store.SavePending(s.Name, r.NewPending); } catch (Exception) { } }
            Log.W("  [" + s.Name + "] local " + L.Count + " fichiers, serveur " + H.Count + " fichiers");
        }
        var newConf = new List<ConflictRec>();
        foreach (var it in Plan.Where(x => x.Kind == Act.Conflict))
        {
            var prev = oldConf.FirstOrDefault(c => c.Set == it.Set.Name && c.Rel.Equals(it.Rel, StringComparison.OrdinalIgnoreCase));
            newConf.Add(new ConflictRec { Set = it.Set.Name, Rel = it.Rel, LSize = it.L!.Size, LTicks = it.L.Ticks, HSize = it.H!.Size, HTicks = it.H.Ticks, Detected = prev?.Detected ?? DateTime.UtcNow.Ticks });
        }
        Conflicts = newConf;
        var todo = Plan.Where(x => x.Kind == Act.Push || x.Kind == Act.Pull).ToList();
        P.TotalFiles = todo.Count; P.TotalBytes = todo.Sum(x => x.Size);
        P.PerSet.Clear();
        foreach (var g in todo.GroupBy(x => x.Set.Name)) P.PerSet[g.Key] = new long[] { 0, g.Count() };
    }

    // Simulation : combien de fichiers doivent arriver du serveur (recuperations + suppressions venues de l'autre PC).
    public int CountIncoming()
    {
        o.Apply = false;
        BuildPlan(false);
        return Plan.Count(x => x.Kind == Act.Pull || x.Kind == Act.TrashLocal);
    }

    public string PlanText()
    {
        var sb = new StringBuilder();
        foreach (var g in Plan.GroupBy(x => x.Set.Name))
        {
            int push = g.Count(x => x.Kind == Act.Push), pull = g.Count(x => x.Kind == Act.Pull), conf = g.Count(x => x.Kind == Act.Conflict);
            int uns = g.Count(x => x.Kind == Act.Unstable), blk = g.Count(x => x.Kind == Act.PullBlocked), same = g.Count(x => x.Kind == Act.Same);
            long pb = g.Where(x => x.Kind == Act.Push).Sum(x => x.Size), qb = g.Where(x => x.Kind == Act.Pull).Sum(x => x.Size);
            int trash = g.Count(x => x.Kind == Act.TrashHub || x.Kind == Act.TrashLocal), pend = g.Count(x => x.Kind == Act.PendingDelete);
            sb.AppendLine($"  {g.Key,-14} a envoyer {push} ({Mb(pb)}) | a recuperer {pull} ({Mb(qb)}) | conflits {conf} | instables {uns} | recuperation bloquee {blk} | corbeille {trash} | suppression en attente {pend} | deja a jour {same}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ session
    public int Run()
    {
        Log.W("Claude-Sync " + o.Pc + " - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + (o.Apply ? " - MODE APPLIQUER" : " - SIMULATION (rien n'est ecrit)"));
        try
        {
            HelloInfo hello = hub.Hello();
            bool claude = ClaudeIsRunning();
            Log.W("Claude ouvert sur ce PC : " + (claude ? "oui (envoi seulement, aucune recuperation)" : "non"));
            string? other = OtherPcActive();
            if (other != null && !o.IgnorePresence) { Summary = "Claude est ouvert sur " + other + " (presence recente) : synchro refusee (--ignore-presence pour forcer)."; Log.W(Summary); return 4; }
            if (hello.HubId.Length == 0) { Summary = "Le serveur n'a pas d'identifiant de hub : synchro refusee."; Log.W(Summary); return 3; }
            // identite du hub : si le dossier est nouveau ou remplace, la memoire des synchros precedentes ne vaut plus rien
            bool resetBase = store.KnownHubId != hello.HubId;
            if (resetBase && store.HasMemory) Log.W("Nouveau dossier de synchronisation ou serveur remplace : la memoire des synchros precedentes est oubliee (rien n'est supprime).");
            ignoreBase = resetBase;
            if (o.Apply)
            {
                WritePresence(claude && o.ClaudeRunning != "idle");     // avant l'attente du verrou : l'autre PC doit savoir si Claude est ouvert ici
                DateTime until = DateTime.UtcNow.AddSeconds(o.WaitLockSeconds);
                LockResult lk = hub.AcquireLock(o.Pc); bool said = false;
                while (!lk.Ok && DateTime.UtcNow < until)
                {
                    if (!said) { Log.W("Synchro en cours sur " + lk.Holder + " : j'attends qu'elle se termine..."); said = true; }
                    Report("En attente de l'autre PC (" + lk.Holder + ")...", "");
                    Thread.Sleep(2000);
                    lk = hub.AcquireLock(o.Pc);
                }
                if (!lk.Ok) { Summary = "Synchro deja en cours sur " + lk.Holder + "."; Log.W(Summary); return 5; }
                haveLock = true;
                WritePresence(claude && o.ClaudeRunning != "idle");
                if (resetBase)
                {
                    store.ResetMemory(); store.SetHubId(hello.HubId); ignoreBase = false;
                }
            }
            BuildPlan(claude);
            Log.W("Plan :"); Log.W(PlanText().TrimEnd());
            Log.W("Total : " + P.TotalFiles + " fichier(s), " + Mb(P.TotalBytes) + " ; conflits : " + Conflicts.Count);
            if (!o.Apply) { Summary = "SIMULATION terminee."; return 0; }
            store.SaveConflicts(Conflicts);
            Execute(DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try { int pu = PurgeAll(o.PurgeDays); if (pu > 0) Log.W("Corbeille et anciennes versions : " + pu + " lot(s) de plus de " + o.PurgeDays + " jours supprime(s) definitivement."); } catch (Exception) { }
            string v = "Termine : " + P.DoneFiles + "/" + P.TotalFiles + " fichier(s), " + Mb(P.DoneBytes) + ", " + Trashed + " mis a la corbeille, " + Errors + " erreur(s), " + Conflicts.Count + " conflit(s) en attente.";
            if (PullsSkipped > 0) v += " " + PullsSkipped + " element(s) a recuperer reporte(s) (liaison lente) : repris a la prochaine synchro Claude ferme.";
            if (Warnings.Count > 0) v += " ATTENTION : " + string.Join(" ", Warnings);
            Summary = v; Log.W(v);
            return (Errors > 0 || Warnings.Count > 0) ? 2 : (Conflicts.Count > 0 ? 1 : 0);
        }
        catch (HubException e)
        {
            Summary = HubMessage(e) + " Synchro reportee.";
            Log.W(Summary);
            return 3;
        }
        finally
        {
            if (haveLock)
            {
                try { WritePresence(ClaudeIsRunning() && o.ClaudeRunning != "idle"); hub.ReleaseLock(); } catch (Exception) { }
                haveLock = false;
            }
        }
    }

    // ------------------------------------------------------------------ execution
    IEnumerable<List<PlanItem>> Batches(List<PlanItem> items)
    {
        var cur = new List<PlanItem>(); long bytes = 0;
        foreach (var it in items)
        {
            if (cur.Count > 0 && (cur.Count >= o.BatchFiles || bytes + it.Size > o.BatchBytes)) { yield return cur; cur = new List<PlanItem>(); bytes = 0; }
            cur.Add(it); bytes += it.Size;
        }
        if (cur.Count > 0) yield return cur;
    }

    void Execute(string stamp)
    {
        P.Start = DateTime.UtcNow;
        string[] order = { "reglages", "conversations", "historique", "projets", "sessions" };
        var sets = Sets().ToDictionary(s => s.Name);
        var bases = new Dictionary<string, Dictionary<string, FileEntry>>();
        foreach (var s in sets.Values) bases[s.Name] = ignoreBase ? new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase) : store.LoadBase(s.Name);
        var planned = new HashSet<string>(Plan.Select(i => i.Set.Name));
        // fichiers deja identiques : la base est mise a jour (et la date du serveur remise si seul le contenu est identique)
        foreach (var it in Plan.Where(x => x.Kind == Act.Same))
        {
            if (it.Note == "touch") { try { hub.Touch(it.Set.Name, it.Rel, it.L!.Ticks); } catch (HubException) { } }
            bases[it.Set.Name][it.Rel] = new FileEntry { Rel = it.Rel, Size = it.L!.Size, Ticks = it.L.Ticks };
        }
        var sw = Stopwatch.StartNew(); long lastReport = 0, doneBytes = 0;
        bool down = false;                       // liaison perdue : on arrete d'insister pour cette synchro
        foreach (string name in order)
        {
            if (!planned.Contains(name)) continue;
            var s = sets[name]; var b = bases[name];
            var pushes = Plan.Where(x => x.Set.Name == name && x.Kind == Act.Push).ToList();
            var pulls = Plan.Where(x => x.Set.Name == name && x.Kind == Act.Pull).ToList();
            var others = Plan.Where(x => x.Set.Name == name && (x.Kind == Act.TrashHub || x.Kind == Act.TrashLocal || x.Kind == Act.Forget)).ToList();
            if (pushes.Count + pulls.Count + others.Count == 0) { SaveBase(name, b); continue; }
            Report(name == "sessions" ? "Sessions" : name, "");

            // ---- envois (avant les recuperations)
            foreach (var batch in Batches(pushes))
            {
                if (down) { Errors += batch.Count; continue; }
                long baseline = doneBytes, last = 0;
                tx.Cancel = null;
                tx.OnBytes = v => { doneBytes += v - last; last = v; Progress(sw, ref lastReport, doneBytes); };
                TransferResult tr;
                try { tr = tx.Push(s.LocalRoot, batch.Select(i => i.Rel).ToList(), SetDefs.HubDir(name), "/_anciennes/" + stamp + "/" + name); }
                catch (HubException e) { Errors += batch.Count; down = true; Log.W("  ERREUR envoi " + name + " : " + e.Message); continue; }
                if (tr.Fatal != null) { Errors += batch.Count; down = tr.Fatal == HubErrKind.Network; Log.W("  ERREUR envoi " + name + " (" + tr.Fatal + ") : " + FirstLine(tr.ErrorText)); continue; }
                VerifyPush(name, s, b, batch, tr);
                doneBytes = baseline + batch.Where(i => tr.Done.Contains(i.Rel)).Sum(i => i.Size);
                Progress(sw, ref lastReport, doneBytes, true);
            }

            // ---- recuperations
            foreach (var batch in Batches(pulls))
            {
                if (StopPulls || down) { PullsSkipped += batch.Count; continue; }
                long baseline = doneBytes, last = 0;
                tx.Cancel = () => StopPulls;
                tx.OnBytes = v => { doneBytes += v - last; last = v; Progress(sw, ref lastReport, doneBytes); };
                TransferResult tr;
                try { tr = tx.Pull(SetDefs.HubDir(name), batch.Select(i => i.Rel).ToList(), s.LocalRoot, Path.Combine(store.ArchiveDir, stamp, name)); }
                catch (HubException e) { Errors += batch.Count; down = true; Log.W("  ERREUR recuperation " + name + " : " + e.Message); continue; }
                finally { tx.Cancel = null; }
                if (tr.Cancelled) { PullsSkipped += batch.Count; continue; }
                if (tr.Fatal != null) { Errors += batch.Count; down = tr.Fatal == HubErrKind.Network; Log.W("  ERREUR recuperation " + name + " (" + tr.Fatal + ") : " + FirstLine(tr.ErrorText)); continue; }
                VerifyPull(name, s, b, batch, tr);
                doneBytes = baseline + batch.Where(i => tr.Done.Contains(i.Rel)).Sum(i => i.Size);
                Progress(sw, ref lastReport, doneBytes, true);
            }

            // ---- suppressions (corbeilles) et oublis
            foreach (var it in others)
            {
                try
                {
                    if (it.Kind == Act.Forget) { b.Remove(it.Rel); continue; }
                    if (it.Kind == Act.TrashLocal && StopPulls) { PullsSkipped++; continue; }
                    if (down) continue;
                    if (it.Note == "recheck" && !Recheck(name, s, it)) continue;
                    if (it.Kind == Act.TrashHub) hub.Trash(name, it.Rel, stamp, o.Pc);
                    else { LocalTrash(LocalPath(s, it.Rel), store.TrashDir, stamp, name, it.Rel); PruneEmpty(Path.GetDirectoryName(LocalPath(s, it.Rel)), s.LocalRoot); }
                    b.Remove(it.Rel);
                    Trashed++;
                }
                catch (HubException e) { Errors++; Log.W("  ERREUR corbeille " + name + " : " + it.Rel + " : " + e.Message); if (e.Kind == HubErrKind.Network) down = true; }
                catch (Exception e) { Errors++; Log.W("  ERREUR corbeille " + name + " : " + it.Rel + " : " + e.Message); }
            }
            SaveBase(name, b);
        }
        tx.OnBytes = null;
        Report("Termine", "");
    }

    static string FirstLine(string t) { int i = t.IndexOf('\n'); return (i < 0 ? t : t.Substring(0, i)).Trim(); }

    void SaveBase(string name, Dictionary<string, FileEntry> b) { try { store.SaveBase(name, b); } catch (Exception e) { Log.W("  base " + name + " : " + e.Message); Errors++; } }

    void Progress(Stopwatch sw, ref long lastReport, long done, bool force = false)
    {
        P.DoneBytes = done;
        long ms = sw.ElapsedMilliseconds;
        if (!force && ms - lastReport < 400) return;
        lastReport = ms;
        double secs = Math.Max(0.5, sw.Elapsed.TotalSeconds);
        P.BytesPerSec = done / secs;
        P.Eta = P.BytesPerSec > 1 ? TimeSpan.FromSeconds(Math.Max(0, P.TotalBytes - done) / P.BytesPerSec) : TimeSpan.Zero;
        OnProgress?.Invoke(P);
    }

    // Apres un envoi : le serveur doit contenir ce qui a ete annonce ET la source ne doit pas avoir change pendant la copie.
    void VerifyPush(string name, SyncSet s, Dictionary<string, FileEntry> b, List<PlanItem> batch, TransferResult tr)
    {
        var done = batch.Where(i => tr.Done.Contains(i.Rel)).ToList();
        foreach (var it in batch.Where(i => !tr.Done.Contains(i.Rel)))
        {
            Errors++;
            Log.W("  ERREUR " + name + " : " + it.Rel + " : " + (tr.Vanished ? "fichier disparu pendant la copie (reporte)" : "non transfere " + FirstLine(tr.ErrorText)));
        }
        if (done.Count == 0) return;
        Dictionary<string, FileEntry> st;
        try { st = hub.Stat(name, done.Select(i => i.Rel).ToList()); }
        catch (HubException e) { Errors += done.Count; Log.W("  ERREUR verification " + name + " : " + e.Message); return; }
        foreach (var it in done)
        {
            st.TryGetValue(it.Rel, out var onHub);
            if (onHub == null) { Errors++; Log.W("  ERREUR " + name + " : " + it.Rel + " : absent du serveur apres l'envoi"); continue; }
            b[it.Rel] = new FileEntry { Rel = it.Rel, Size = onHub.Size, Ticks = onHub.Ticks };      // la base = ce que le serveur contient reellement
            var nowLocal = LocalEntry(LocalPath(s, it.Rel), it.Rel);
            if (!FileEntry.Same(onHub, it.L!) || nowLocal == null || !FileEntry.Same(nowLocal, it.L!))
            {
                Errors++;
                Log.W("  ERREUR " + name + " : " + it.Rel + " : modifie pendant la copie (reporte, renvoye a la prochaine synchro)");
                continue;
            }
            P.DoneFiles++;
            lock (P.PerSet) { if (P.PerSet.TryGetValue(name, out var ps)) ps[0]++; }
        }
    }

    void VerifyPull(string name, SyncSet s, Dictionary<string, FileEntry> b, List<PlanItem> batch, TransferResult tr)
    {
        foreach (var it in batch)
        {
            if (!tr.Done.Contains(it.Rel)) { Errors++; Log.W("  ERREUR " + name + " : " + it.Rel + " : non recupere " + FirstLine(tr.ErrorText)); continue; }
            var nowLocal = LocalEntry(LocalPath(s, it.Rel), it.Rel);
            if (nowLocal == null) { Errors++; Log.W("  ERREUR " + name + " : " + it.Rel + " : absent apres la recuperation"); continue; }
            b[it.Rel] = new FileEntry { Rel = it.Rel, Size = nowLocal.Size, Ticks = nowLocal.Ticks };  // la base = ce qui est reellement ici
            P.DoneFiles++;
            lock (P.PerSet) { if (P.PerSet.TryGetValue(name, out var ps)) ps[0]++; }
        }
    }

    // Seconde observation d'une suppression, quelques secondes apres l'analyse : elle doit toujours etre vraie, et l'autre cote inchange.
    bool Recheck(string name, SyncSet s, PlanItem it)
    {
        long wait = o.RecheckTicks - (DateTime.UtcNow.Ticks - planTicks);
        if (wait > 0) Thread.Sleep((int)(wait / 10000));
        if (it.Kind == Act.TrashHub)
        {
            if (File.Exists(Fs.Long(LocalPath(s, it.Rel)))) return false;
            hub.Stat(name, new[] { it.Rel }).TryGetValue(it.Rel, out var h);
            return h != null && FileEntry.Same(h, it.H!);
        }
        var l = LocalEntry(LocalPath(s, it.Rel), it.Rel);
        hub.Stat(name, new[] { it.Rel }).TryGetValue(it.Rel, out var h2);
        return h2 == null && l != null && FileEntry.Same(l, it.L!);
    }

    // ---------- corbeille locale
    static void LocalTrash(string file, string trashRoot, string stamp, string setName, string rel)
    {
        if (!File.Exists(Fs.Long(file))) return;
        string dst = Path.Combine(trashRoot, stamp, setName, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(Fs.Long(dst))) File.Delete(Fs.Long(dst));
        File.Move(Fs.Long(file), Fs.Long(dst));
    }

    static void PruneEmpty(string? dir, string root)
    {
        try
        {
            string r = root.TrimEnd('\\');
            while (dir != null && dir.Length > r.Length && dir.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.EnumerateFileSystemEntries(Fs.Long(dir)).Any()) break;
                Directory.Delete(Fs.Long(dir));
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception) { }
    }

    // Purge definitive des corbeilles et des anciennes versions de plus de `days` jours (serveur puis local, 20 s au plus cote PC).
    public int PurgeAll(int days)
    {
        int removed = 0;
        try { removed += hub.Purge(days, false).Count; } catch (HubException) { }
        var budget = Stopwatch.StartNew();
        foreach (string root in new[] { store.TrashDir, store.ArchiveDir })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string d in Directory.EnumerateDirectories(root))
            {
                if (budget.Elapsed.TotalSeconds > 20) return removed;
                if (DateTime.TryParseExact(Path.GetFileName(d), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && (DateTime.Now - t).TotalDays > days)
                { try { Directory.Delete(d, true); removed++; } catch (Exception) { } }
            }
        }
        return removed;
    }

    // ------------------------------------------------------------------ corbeille consultable (fenetre Corbeille)
    // Une entree par suppression : session (fiche + conversation + historique) ou dossier de projet. La liste vient du serveur.
    public List<TrashEntry> ListEntries()
    {
        var res = new List<TrashEntry>();
        TrashListing tl;
        try { tl = hub.TrashList(); } catch (HubException) { return res; }
        foreach (var grp in tl.Files.GroupBy(f => f.Stamp).OrderByDescending(g => g.Key))
        {
            string stamp = grp.Key;
            if (!DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) continue;
            tl.PcByStamp.TryGetValue(stamp, out var pc);
            var titles = new Dictionary<string, string>();
            foreach (var f in grp.Where(f => f.Set == "sessions" && f.Cli.Length > 0 && f.Title.Length > 0)) titles[f.Cli] = f.Title;
            var map = new Dictionary<string, TrashEntry>();
            foreach (var f in grp)
            {
                string[] seg = f.Rel.Split('/'); string key, kind; string? cli = null;
                if (f.Set == "sessions") { cli = f.Cli.Length > 0 ? f.Cli : null; kind = "session"; key = "s:" + (cli ?? f.Rel); }
                else if (f.Set == "conversations") { string leaf = seg.Length > 1 ? seg[1] : seg[0]; cli = Path.GetFileNameWithoutExtension(leaf); kind = "session"; key = "s:" + cli; }
                else if (f.Set == "historique") { cli = seg[0]; kind = "session"; key = "s:" + cli; }
                else if (f.Set == "projets") { kind = "projet"; key = "p:" + seg[0]; }
                else { kind = "fichiers"; key = "f:" + f.Set; }
                if (!map.TryGetValue(key, out var e))
                {
                    string? title = kind == "projet" ? seg[0] : (kind == "session" ? cli : f.Set);
                    e = new TrashEntry { Kind = kind, Stamp = stamp, Date = dt, Pc = pc ?? "", Title = title!, Sample = f.Rel };
                    map[key] = e; res.Add(e);
                }
                e.Items.Add(new[] { stamp + "/" + f.Set + "/" + f.Rel, f.Set, f.Rel }); e.Files++; e.Bytes += f.Size;
                if (kind == "session" && cli != null && titles.TryGetValue(cli, out var t)) e.Title = t;
            }
        }
        res = res.OrderByDescending(x => x.Date).ToList();
        int n = 1;
        foreach (var e in res) { e.Num = n++; if (e.Kind == "session" && e.Title != null && e.Title.Length > 30 && e.Title.IndexOf(' ') < 0) e.Title = "session " + e.Title.Substring(0, 8); }
        return res;
    }

    // Remet une suppression a sa place sur le serveur ; la synchro suivante la redistribue sur tous les PC.
    public string RestoreEntry(int num)
    {
        var l = ListEntries();
        return num < 1 || num > l.Count ? "Numero invalide." : RestoreItems(l[num - 1]);
    }

    public string RestoreItems(TrashEntry e)
    {
        int n = 0, skipped = 0;
        try { foreach (var it in e.Items) { if (hub.RestoreFromTrash(e.Stamp, it[1], it[2])) n++; else skipped++; } }
        catch (HubException ex) { return HubMessage(ex); }
        return "\"" + e.Title + "\" : " + n + " fichier(s) restaure(s), " + skipped + " ignore(s) (deja presents). Lancez une synchro pour les redistribuer sur tous les PC.";
    }

    public int PurgeTrash(int days) => PurgeAll(days);

    // ------------------------------------------------------------------ conflits
    public string Resolve(int index, string choice)
    {
        var list = store.LoadConflicts();
        if (index < 1 || index > list.Count) return "Numero de conflit invalide.";
        var c = list[index - 1];
        if (choice != "local" && choice != "hub" && choice != "both") return "Choix inconnu (local | hub | both).";
        if (ClaudeIsRunning()) return "Fermez Claude avant de resoudre un conflit.";
        var s = Sets().First(x => x.Name == c.Set);
        string lp = LocalPath(s, c.Rel);
        try
        {
            if (!File.Exists(Fs.Long(lp))) return "Un des deux fichiers n'existe plus : relancez une synchro.";
            hub.Hello();
            if (!hub.Stat(s.Name, new[] { c.Rel }).TryGetValue(c.Rel, out var onHub) || onHub == null) return "Un des deux fichiers n'existe plus : relancez une synchro.";
            var lk = hub.AcquireLock(o.Pc);
            if (!lk.Ok) return "Synchro en cours sur " + lk.Holder + ".";
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var b = store.LoadBase(s.Name);
                string archHub = "/_anciennes/" + stamp + "/" + s.Name;
                if (choice == "local" || choice == "both")
                {
                    if (choice == "both")
                    {   // la version du serveur est gardee a cote de la version locale (le fichier "conflit" sera envoye a la synchro suivante)
                        string stage = Path.Combine(store.Dir, "tmp-conflit-" + stamp);
                        var pr = tx.Pull(SetDefs.HubDir(s.Name), new[] { c.Rel }, stage, null);
                        string staged = Path.Combine(stage, c.Rel.Replace('/', '\\'));
                        if (!pr.Success || !File.Exists(staged)) return "Echec de la recuperation de la version du serveur : " + FirstLine(pr.ErrorText);
                        string alt = Path.Combine(Path.GetDirectoryName(lp)!, Path.GetFileNameWithoutExtension(lp) + ".conflit-" + stamp + Path.GetExtension(lp));
                        File.Move(staged, alt);
                        try { Directory.Delete(stage, true); } catch (Exception) { }
                    }
                    var el = LocalEntry(lp, c.Rel)!;
                    var tr = tx.Push(s.LocalRoot, new[] { c.Rel }, SetDefs.HubDir(s.Name), archHub);
                    if (!tr.Success) return "Echec de l'envoi : " + FirstLine(tr.ErrorText);
                    hub.Stat(s.Name, new[] { c.Rel }).TryGetValue(c.Rel, out var after);
                    if (after == null) return "Echec de l'envoi : le fichier est absent du serveur.";
                    b[c.Rel] = new FileEntry { Rel = c.Rel, Size = after.Size, Ticks = after.Ticks };
                }
                else
                {
                    var pr = tx.Pull(SetDefs.HubDir(s.Name), new[] { c.Rel }, s.LocalRoot, Path.Combine(store.ArchiveDir, stamp, s.Name));
                    var nowLocal = LocalEntry(lp, c.Rel);
                    if (!pr.Success || nowLocal == null) return "Echec de la recuperation : " + FirstLine(pr.ErrorText);
                    b[c.Rel] = new FileEntry { Rel = c.Rel, Size = nowLocal.Size, Ticks = nowLocal.Ticks };
                }
                store.SaveBase(s.Name, b);
                list.RemoveAt(index - 1); store.SaveConflicts(list);
                return "Conflit resolu (" + choice + "). L'autre version est conservee dans les archives.";
            }
            finally { try { hub.ReleaseLock(); } catch (Exception) { } }
        }
        catch (HubException e) { return HubMessage(e); }
    }
}
