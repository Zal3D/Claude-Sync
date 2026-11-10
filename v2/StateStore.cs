// Claude-Sync V2 : etat local du moteur (dans %LOCALAPPDATA%\ClaudeSync). Memes fichiers et memes formats que la V1 :
//   base-<jeu>.tsv    dernier etat synchronise (chemin, taille, date)        attente-<jeu>.tsv  suppressions deja vues (chemin, date)
//   conflits.tsv      conflits en attente                                       hub.id             identite du hub connu
// Les chemins des fichiers de la V1 contenaient des "\" : ils sont convertis en "/" a la lecture (la base de la V1 reste utilisable).
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeSync.V2;

public sealed class StateStore
{
    public readonly string Dir;
    public StateStore(string dir) { Dir = dir; Directory.CreateDirectory(dir); }

    public string TrashDir => Path.Combine(Dir, "corbeille");       // suppressions venues de l'autre PC
    public string ArchiveDir => Path.Combine(Dir, "anciennes");     // versions locales remplacees par une recuperation
    string P(string name) => Path.Combine(Dir, name);

    static string Norm(string rel) => rel.Replace('\\', '/');
    static void WriteAtomic(string path, IEnumerable<string> lines)
    {
        string tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    public Dictionary<string, FileEntry> LoadBase(string set)
    {
        var d = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        string p = P("base-" + set + ".tsv");
        if (!File.Exists(p)) return d;
        foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
        {
            string[] f = line.Split('\t');
            if (f.Length != 3 || !long.TryParse(f[1], out long size) || !long.TryParse(f[2], out long ticks)) continue;
            string rel = Norm(f[0]);
            d[rel] = new FileEntry { Rel = rel, Size = size, Ticks = ticks };
        }
        return d;
    }

    public void SaveBase(string set, IDictionary<string, FileEntry> d) =>
        WriteAtomic(P("base-" + set + ".tsv"), d.Values.Select(e => e.Rel + "\t" + e.Size + "\t" + e.Ticks));

    public Dictionary<string, long> LoadPending(string set)
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string p = P("attente-" + set + ".tsv");
        if (!File.Exists(p)) return d;
        foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
        {
            string[] f = line.Split('\t');
            if (f.Length == 2 && long.TryParse(f[1], out long t)) d[Norm(f[0])] = t;
        }
        return d;
    }

    public void SavePending(string set, IDictionary<string, long> d) =>
        File.WriteAllLines(P("attente-" + set + ".tsv"), d.Select(kv => kv.Key + "\t" + kv.Value).ToArray(), new UTF8Encoding(false));

    public List<ConflictRec> LoadConflicts()
    {
        var l = new List<ConflictRec>();
        string p = P("conflits.tsv");
        if (!File.Exists(p)) return l;
        foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
        {
            string[] f = line.Split('\t');
            if (f.Length != 7) continue;
            if (!long.TryParse(f[2], out long ls) || !long.TryParse(f[3], out long lt) || !long.TryParse(f[4], out long hs) || !long.TryParse(f[5], out long ht) || !long.TryParse(f[6], out long det)) continue;
            l.Add(new ConflictRec { Set = f[0], Rel = Norm(f[1]), LSize = ls, LTicks = lt, HSize = hs, HTicks = ht, Detected = det });
        }
        return l;
    }

    public void SaveConflicts(IEnumerable<ConflictRec> l) =>
        File.WriteAllLines(P("conflits.tsv"), l.Select(c => string.Join("\t", c.Set, c.Rel, c.LSize, c.LTicks, c.HSize, c.HTicks, c.Detected)).ToArray(), new UTF8Encoding(false));

    // ---- identite du hub : si elle change (nouveau serveur, nouveau dossier), la memoire des synchros precedentes ne vaut plus rien
    public string? KnownHubId { get { try { return File.Exists(P("hub.id")) ? File.ReadAllText(P("hub.id")).Trim() : null; } catch (IOException) { return null; } } }
    public void SetHubId(string id) => File.WriteAllText(P("hub.id"), id);

    public bool HasMemory => Directory.EnumerateFiles(Dir, "base-*.tsv").Any();

    // Oublie la memoire (rien n'est supprime nulle part : le premier passage compare simplement tout).
    public void ResetMemory()
    {
        foreach (string f in Directory.EnumerateFiles(Dir))
        {
            string n = Path.GetFileName(f);
            if (n.StartsWith("base-") || n.StartsWith("attente-") || n == "conflits.tsv") File.Delete(f);
        }
    }
}
