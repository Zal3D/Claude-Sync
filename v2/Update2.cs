// Claude-Sync V2 : mises a jour du programme (J6). Source = les "Releases" d'un depot GitHub (reglage "mises_a_jour" = "proprietaire/depot"),
// ou une adresse HTTPS d'un petit fichier JSON {"version","url","sha256"} (autre hebergement).
// Regles : HTTPS seulement ; l'empreinte SHA-256 publiee doit correspondre au fichier telecharge ; au plus une verification toutes les 6 h ;
// RIEN ne s'installe sans le clic de l'utilisateur (une synchro en cours ou une fermeture de Claude ne doivent jamais etre interrompues par surprise).
// Limite assumee : l'empreinte garantit que le fichier n'est pas abime, pas qui l'a publie (pas de signature de code payante) : la confiance
// repose sur le controle du compte du depot. Voir README.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeSync.V2;

public sealed class UpdateInfo
{
    public string Version = "", Url = "", Sha256 = "", Notes = "";
    public long Size;
}

public static class Updater2
{
    public const string AssetName = "Claude-Sync-Setup.exe";
    public const int CheckEveryHours = 6;
    public const string DefaultSource = "Zal3D/Claude-Sync";      // depot public des versions ; reglable par "mises_a_jour" dans config.json
    const long MaxBytes = 120L << 20;

    // Acces reseau remplacable par les tests
    public static Func<string, byte[]> Http = DefaultHttp;

    static byte[] DefaultHttp(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var rq = (HttpWebRequest)WebRequest.Create(url);
        rq.UserAgent = "Claude-Sync/" + V2Info.Version; rq.Timeout = 25000; rq.ReadWriteTimeout = 25000; rq.AllowAutoRedirect = true; rq.Accept = "application/vnd.github+json, application/json, application/octet-stream";
        using var rs = (HttpWebResponse)rq.GetResponse();
        if (rs.ContentLength > MaxBytes) throw new IOException("fichier trop gros");
        using var s = rs.GetResponseStream(); using var ms = new MemoryStream();
        var buf = new byte[81920]; int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > MaxBytes) throw new IOException("fichier trop gros"); }
        return ms.ToArray();
    }

    // ---------- versions : 2.0.1 > 2.0.0 > 2.0.0-beta.2 > 2.0.0-dev
    public static bool IsNewer(string candidate, string current)
    {
        var a = Split(candidate); var b = Split(current);
        if (a == null || b == null) return false;
        for (int i = 0; i < 3; i++) if (a.Value.nums[i] != b.Value.nums[i]) return a.Value.nums[i] > b.Value.nums[i];
        if (a.Value.pre.Length == 0 && b.Value.pre.Length > 0) return true;       // 2.0.0 est plus recent que 2.0.0-beta
        if (a.Value.pre.Length > 0 && b.Value.pre.Length == 0) return false;
        int ra = Rank(a.Value.pre), rb = Rank(b.Value.pre);
        if (ra != rb) return ra > rb;
        return string.Compare(a.Value.pre, b.Value.pre, StringComparison.Ordinal) > 0;
    }

    // dev < alpha < beta < rc (puis ordre alphabetique du reste : beta.2 > beta.1)
    static int Rank(string pre) => pre.StartsWith("dev", StringComparison.OrdinalIgnoreCase) ? 0 : pre.StartsWith("alpha", StringComparison.OrdinalIgnoreCase) ? 1 : pre.StartsWith("beta", StringComparison.OrdinalIgnoreCase) ? 2 : pre.StartsWith("rc", StringComparison.OrdinalIgnoreCase) ? 3 : 1;

    static (int[] nums, string pre)? Split(string v)
    {
        v = (v ?? "").Trim().TrimStart('v', 'V');
        string pre = ""; int dash = v.IndexOf('-'); if (dash >= 0) { pre = v.Substring(dash + 1); v = v.Substring(0, dash); }
        var parts = v.Split('.'); var nums = new int[3];
        if (parts.Length < 1 || parts.Length > 3) return null;
        for (int i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return null;
        return (nums, pre);
    }

    // ---------- source -> adresse a interroger
    public static string? SourceUrl(string source)
    {
        source = (source ?? "").Trim();
        if (source.Length == 0) return null;
        if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return source;
        if (source.Contains("://")) return null;                                   // http:// et autres : refuses
        var p = source.Split('/');
        if (p.Length != 2 || p.Any(x => x.Length == 0 || x.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')))) return null;
        return "https://api.github.com/repos/" + p[0] + "/" + p[1] + "/releases?per_page=10";     // la liste (et non /latest) : pendant la beta, les versions sont "preliminaires"
    }

    // ---------- lecture de la reponse (GitHub : tag_name + assets ; generique : version/url/sha256)
    public static UpdateInfo? Parse(string json, Func<string, byte[]>? http = null)
    {
        object root = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
        if (root is Dictionary<string, object> one) return ParseOne(one, http);
        // liste de versions (GitHub /releases : y compris les versions "preliminaires" de la beta) : la plus recente qui a un fichier valide
        UpdateInfo? best = null;
        if (root is System.Collections.IEnumerable all)
            foreach (object it in all)
                if (it is Dictionary<string, object> d && !Json.Bool(d, "draft"))
                {
                    var u = ParseOne(d, http);
                    if (u != null && (best == null || IsNewer(u.Version, best.Version))) best = u;
                }
        return best;
    }

    static UpdateInfo? ParseOne(Dictionary<string, object> d, Func<string, byte[]>? http)
    {
        var u = new UpdateInfo();
        if (d.ContainsKey("tag_name"))
        {
            u.Version = (Json.Str(d, "tag_name") ?? "").TrimStart('v', 'V'); u.Notes = Json.Str(d, "body") ?? "";
            string sumUrl = "";
            if (d.TryGetValue("assets", out var ao) && ao is System.Collections.IEnumerable list)
                foreach (object it in list)
                {
                    if (it is not Dictionary<string, object> a) continue;
                    string name = Json.Str(a, "name") ?? "", url = Json.Str(a, "browser_download_url") ?? "";
                    if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        u.Url = url; u.Size = Json.Lng(a, "size");
                        string dg = Json.Str(a, "digest") ?? ""; if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) u.Sha256 = dg.Substring(7).ToLowerInvariant();
                    }
                    else if (name.Equals(AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) sumUrl = url;
                }
            if (u.Sha256.Length == 0 && sumUrl.Length > 0 && IsHttps(sumUrl))
            {
                string txt = Encoding.UTF8.GetString((http ?? Http)(sumUrl));
                u.Sha256 = FirstHex(txt);
            }
        }
        else
        {
            u.Version = (Json.Str(d, "version") ?? "").TrimStart('v', 'V'); u.Url = Json.Str(d, "url") ?? ""; u.Sha256 = (Json.Str(d, "sha256") ?? "").ToLowerInvariant(); u.Notes = Json.Str(d, "notes") ?? "";
        }
        if (Split(u.Version) == null || u.Url.Length == 0 || !IsHttps(u.Url) || u.Sha256.Length != 64 || !u.Sha256.All(Uri.IsHexDigit)) return null;
        return u;
    }

    static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static string FirstHex(string t)
    {
        foreach (string tok in t.Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries))
            if (tok.Length == 64 && tok.All(Uri.IsHexDigit)) return tok.ToLowerInvariant();
        return "";
    }

    // ---------- verification periodique (au plus une fois toutes les 6 h sauf demande explicite)
    static string StateFile(string stateDir) => Path.Combine(stateDir, "update.json");

    public static bool Due(string stateDir, DateTime nowUtc)
    {
        try
        {
            var d = Json.ParseObject(File.ReadAllText(StateFile(stateDir)));
            return nowUtc - new DateTime(Json.Lng(d, "derniere"), DateTimeKind.Utc) >= TimeSpan.FromHours(CheckEveryHours);
        }
        catch (Exception) { return true; }
    }

    // Renvoie la mise a jour disponible, ou null (a jour, pas de source, hors ligne...). error : raison lisible si la verification a echoue.
    public static UpdateInfo? Check(string source, string currentVersion, string stateDir, bool force, DateTime nowUtc, out string error)
    {
        error = "";
        string? url = SourceUrl(source);
        if (url == null) { error = "source"; return null; }
        if (!force && !Due(stateDir, nowUtc)) { error = "recent"; return null; }
        try
        {
            string json = Encoding.UTF8.GetString(Http(url));
            try { Directory.CreateDirectory(stateDir); File.WriteAllText(StateFile(stateDir), Json.Serialize(new Dictionary<string, object> { ["derniere"] = nowUtc.Ticks }), new UTF8Encoding(false)); } catch (Exception) { }
            var u = Parse(json);
            if (u == null) { error = "reponse"; return null; }
            return IsNewer(u.Version, currentVersion) ? u : null;
        }
        catch (Exception e) { error = e.Message; return null; }
    }

    // ---------- telechargement verifie
    public static string Download(UpdateInfo u, string destDir)
    {
        if (!IsHttps(u.Url)) throw new IOException("adresse non HTTPS refusee");
        byte[] data = Http(u.Url);
        string got = Installer2.Hex(SHA256.Create().ComputeHash(data));
        if (!got.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("empreinte SHA-256 incorrecte : fichier refuse");
        if (data.Length < 4096 || data[0] != (byte)'M' || data[1] != (byte)'Z') throw new IOException("ce n'est pas un programme Windows");
        Directory.CreateDirectory(destDir);
        string path = Path.Combine(destDir, "Claude-Sync-Setup-" + u.Version + ".exe");
        File.WriteAllBytes(path, data);
        return path;
    }

    // Lance l'installeur telecharge (il arrete l'ancienne version, remplace les fichiers et relance Claude-Sync) : l'appelant doit alors se fermer.
    public static void Apply(string setupExe)
    {
        Process.Start(new ProcessStartInfo(setupExe, "install --silent --launch") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setupExe) });
    }
}
