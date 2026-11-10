// Claude-Sync V2 : reprise de la memoire de la V1 (migration). La V1 garde, par jeu de dossiers, ce qui etait identique des deux cotes a la
// derniere synchro (base-<jeu>.tsv), les suppressions deja vues (attente-<jeu>.tsv), les conflits (conflits.tsv) et l'identite du hub (hub.id).
// Si la V2 pointe sur le MEME hub (meme hub.id) et reprend ces fichiers, son premier passage ne trouve rien a envoyer, rien a recuperer et aucun
// conflit : sans eux, elle comparerait tout a nu et declarerait des conflits partout. Copie seulement : la V1 n'est jamais modifiee.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeSync.V2;

public sealed class MigrationReport
{
    public bool Ok, Applied;
    public string Message = "";
    public List<string> Files = new();
    public Dictionary<string, int> BaseLines = new();
    public int Malformed;
    public string? BackupDir;
}

public static class Migrator
{
    public static string DefaultV1Dir => Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "ClaudeSync");

    // Une V1 installee et lancee (ClaudeSync.exe, pas ClaudeSync2.exe)
    public static bool V1Running()
    {
        foreach (var p in Process.GetProcessesByName("ClaudeSync")) { p.Dispose(); return true; }
        return false;
    }

    static MigrationReport Fail(string m) => new() { Ok = false, Message = m };

    // hubIdOnServer : identite du hub reellement joint par la V2 (hello). apply=false : simulation (rien n'est ecrit).
    public static MigrationReport Run(string v1Dir, string v2Dir, string? hubIdOnServer, bool apply, bool allowRunning = false, Func<bool>? v1Running = null)
    {
        if (!Directory.Exists(v1Dir)) return Fail("Dossier d'etat de la V1 introuvable : " + v1Dir);
        string hubFile = Path.Combine(v1Dir, "hub.id");
        string? v1Hub = File.Exists(hubFile) ? File.ReadAllText(hubFile).Trim() : null;
        var bases = Directory.GetFiles(v1Dir, "base-*.tsv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (bases.Count == 0) return Fail("Aucune memoire de synchro (base-*.tsv) dans " + v1Dir + " : rien a reprendre (le premier passage de la V2 comparera tout).");
        if (!allowRunning && (v1Running ?? V1Running)()) return Fail("La V1 tourne encore : fermez-la d'abord (icone de la zone de notification, Quitter), pour que ses fichiers ne changent pas pendant la copie.");
        if (string.IsNullOrEmpty(v1Hub)) return Fail("La V1 n'a pas d'identite de hub (hub.id) : sa memoire ne peut pas etre rattachee a un serveur.");
        if (hubIdOnServer == null || hubIdOnServer.Length == 0) return Fail("Identite du serveur V2 inconnue : connectez la V2 d'abord.");
        if (!string.Equals(v1Hub, hubIdOnServer, StringComparison.Ordinal))
            return Fail("Le serveur de la V2 n'est PAS le meme hub que celui de la V1 (identites differentes) : la memoire de la V1 ne vaut rien ici. Rien n'a ete copie.");

        var rep = new MigrationReport { Ok = true, Applied = apply };
        var store = new StateStore(v2Dir);
        // memoire V2 deja presente : mise de cote, jamais ecrasee
        var existing = Directory.GetFiles(v2Dir).Where(f => { string n = Path.GetFileName(f); return n.StartsWith("base-") || n.StartsWith("attente-") || n == "conflits.tsv" || n == "hub.id"; }).ToList();
        if (apply && existing.Count > 0)
        {
            rep.BackupDir = Path.Combine(v2Dir, "migration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(rep.BackupDir);
            foreach (string f in existing) File.Copy(f, Path.Combine(rep.BackupDir, Path.GetFileName(f)), true);
        }
        var names = new List<string>();
        names.AddRange(bases.Select(Path.GetFileName)!);
        names.AddRange(Directory.GetFiles(v1Dir, "attente-*.tsv").Select(Path.GetFileName)!);
        if (File.Exists(Path.Combine(v1Dir, "conflits.tsv"))) names.Add("conflits.tsv");
        names.Add("hub.id");
        foreach (string n in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            rep.Files.Add(n);
            if (apply) File.Copy(Path.Combine(v1Dir, n), Path.Combine(v2Dir, n), true);
        }
        // relecture par la V2 (chemins a "\" convertis en "/") : on verifie que tout est lisible et on reecrit proprement
        foreach (string b in bases)
        {
            string set = Path.GetFileNameWithoutExtension(b).Substring("base-".Length);
            int raw = File.ReadAllLines(b, Encoding.UTF8).Count(l => l.Trim().Length > 0);
            var read = new StateStore(apply ? v2Dir : v1Dir).LoadBase(set);
            rep.BaseLines[set] = read.Count;
            rep.Malformed += Math.Max(0, raw - read.Count);
            if (apply) store.SaveBase(set, read);
        }
        store.SetHubIdIfApply(apply, v1Hub!);
        rep.Message = (apply ? "Memoire de la V1 reprise : " : "SIMULATION (rien n'est ecrit) : ") + rep.Files.Count + " fichier(s), " + rep.BaseLines.Values.Sum() + " fichiers connus"
            + (rep.Malformed > 0 ? ", " + rep.Malformed + " ligne(s) illisible(s) ignoree(s)" : "") + (rep.BackupDir != null ? ". Memoire V2 precedente sauvegardee dans " + rep.BackupDir : "") + ".";
        return rep;
    }

    static void SetHubIdIfApply(this StateStore s, bool apply, string id) { if (apply) s.SetHubId(id); }
}
