// Claude-Sync V2 : definition des cinq jeux de fichiers, scan local (morceau 4 du moteur) et filtrage de la liste du serveur.
// Memes regles que la V1 des DEUX cotes, pour que le planificateur compare des listes comparables :
//   - chemins toujours en "/" (le scan local de la V1 donnait des "\") ;
//   - memes dossiers exclus, memes fichiers temporaires ignores, memes jeux restreints (settings.json, local_*.json) ;
//   - le dossier de reprise de rsync (.rsync-partial) n'est jamais liste.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeSync.V2;

public static class SetDefs
{
    public static readonly string[] Names = { "conversations", "reglages", "historique", "projets", "sessions" };
    public const string PartialDir = ".rsync-partial";

    static readonly string[] ProjectExclusions =
        { "node_modules", "__pycache__", ".venv", "venv", ".mypy_cache", ".pytest_cache", "$RECYCLE.BIN", "System Volume Information" };

    // Les 5 jeux. indexDir : dossier des sessions de Claude Desktop (peut etre null s'il est introuvable : le jeu sera ignore).
    public static List<SyncSet> Build(string profile, string projetsRoot, string? indexDir)
    {
        var conv = new SyncSet { Name = "conversations", LocalRoot = Path.Combine(profile, @".claude\projects") };
        var reg = new SyncSet
        {
            Name = "reglages", LocalRoot = Path.Combine(profile, ".claude"), NewestWins = true, NoSubdirs = true,
            FileOk = n => n.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
        };
        var hist = new SyncSet { Name = "historique", LocalRoot = Path.Combine(profile, @".claude\file-history") };
        var proj = new SyncSet { Name = "projets", LocalRoot = projetsRoot };
        foreach (var d in ProjectExclusions) proj.ExcludedDirs.Add(d);
        var sess = new SyncSet
        {
            Name = "sessions", LocalRoot = indexDir!, NewestWins = true,
            FileOk = n => n.StartsWith("local_", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        };
        var all = new List<SyncSet> { conv, reg, hist, proj, sess };
        foreach (var s in all) s.ExcludedDirs.Add(PartialDir);
        return all;
    }

    // Dossier du jeu DANS le hub (chemin absolu dans le hub, tel que le voit rrsync), avec "/" final.
    public static string HubDir(string set) => set switch
    {
        "conversations" => "/claude/projects/",
        "reglages" => "/claude/reglages/",
        "historique" => "/claude/file-history/",
        "projets" => "/projets/",
        "sessions" => "/desktop/sessions/",
        _ => throw new ArgumentException("jeu inconnu : " + set)
    };

    // Scan local, chemins en "/". links : liens symboliques ignores ; errors : erreurs de lecture (aucune suppression ne sera deduite).
    public static Dictionary<string, FileEntry> ScanLocal(SyncSet s, out int links, out int errors)
    {
        var raw = Fs.Scan(s.LocalRoot, s, out links, out errors);
        var res = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in raw)
        {
            string rel = kv.Key.Replace('\\', '/');
            res[rel] = new FileEntry { Rel = rel, Size = kv.Value.Size, Ticks = kv.Value.Ticks };
        }
        return res;
    }

    // Applique a la liste du serveur les memes filtres que le scan local.
    public static Dictionary<string, FileEntry> FilterHub(IReadOnlyDictionary<string, FileEntry> hub, SyncSet s)
    {
        var res = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in hub)
        {
            string[] parts = kv.Key.Split('/');
            if (s.NoSubdirs && parts.Length > 1) continue;
            bool excluded = false;
            for (int i = 0; i < parts.Length - 1; i++) if (s.ExcludedDirs.Contains(parts[i])) { excluded = true; break; }
            if (excluded) continue;
            if (!Fs.AcceptFile(parts[parts.Length - 1], s)) continue;
            res[kv.Key] = kv.Value;
        }
        return res;
    }
}
