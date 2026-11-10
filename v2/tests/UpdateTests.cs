// Claude-Sync V2 : tests des mises a jour (J6), sans reseau : l'acces HTTP est remplace par un faux. Verifie surtout les REFUS :
// pas de HTTP en clair, pas de fichier dont l'empreinte ne correspond pas, pas plus d'une verification toutes les 6 h, pas de panne en cas d'erreur.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class UpdateTests
{
    static byte[] FakeExe() { var b = new byte[6000]; b[0] = (byte)'M'; b[1] = (byte)'Z'; for (int i = 2; i < b.Length; i++) b[i] = (byte)(i * 7); return b; }
    static string Sha(byte[] b) => Installer2.Hex(SHA256.Create().ComputeHash(b));

    static string Release(string tag, bool pre, bool draft, string assetName, string url, string digest) =>
        "{\"tag_name\":\"" + tag + "\",\"prerelease\":" + (pre ? "true" : "false") + ",\"draft\":" + (draft ? "true" : "false") + ",\"body\":\"notes " + tag + "\",\"assets\":[{\"name\":\"" + assetName + "\",\"size\":6000,\"browser_download_url\":\"" + url + "\"" + (digest.Length > 0 ? ",\"digest\":\"sha256:" + digest + "\"" : "") + "}]}";

    public static void Run()
    {
        var old = Updater2.Http;
        try { Tests(); } finally { Updater2.Http = old; }
    }

    static void Tests()
    {
        byte[] exe = FakeExe(); string h = Sha(exe);
        string root = Path.Combine(Env.Tmp, "maj"); Directory.CreateDirectory(root);

        T.Run("T180 comparaison de versions", delegate
        {
            T.Check("2.0.1 > 2.0.0", Updater2.IsNewer("2.0.1", "2.0.0"));
            T.Check("2.1.0 > 2.0.9 et 3.0.0 > 2.9.9", Updater2.IsNewer("2.1.0", "2.0.9") && Updater2.IsNewer("v3.0.0", "2.9.9"));
            T.Check("10 > 9 (comparaison numerique, pas alphabetique)", Updater2.IsNewer("2.0.10", "2.0.9"));
            T.Check("la version finale est plus recente que sa beta, qui l'est plus que dev", Updater2.IsNewer("2.0.0", "2.0.0-beta.1") && Updater2.IsNewer("2.0.0-beta.1", "2.0.0-dev"));
            T.Check("pas de mise a jour vers la meme version ou une plus ancienne", !Updater2.IsNewer("2.0.0", "2.0.0") && !Updater2.IsNewer("2.0.0-beta.1", "2.0.0") && !Updater2.IsNewer("1.9.9", "2.0.0"));
            T.Check("version illisible : jamais proposee", !Updater2.IsNewer("abc", "2.0.0") && !Updater2.IsNewer("", "2.0.0") && !Updater2.IsNewer("2.x.0", "2.0.0"));
        });

        T.Run("T181 source : depot GitHub ou adresse HTTPS, rien d'autre", delegate
        {
            T.Check("proprietaire/depot -> API GitHub des versions", Updater2.SourceUrl("alice/Claude-Sync") == "https://api.github.com/repos/alice/Claude-Sync/releases?per_page=10");
            T.Check("adresse HTTPS acceptee telle quelle", Updater2.SourceUrl("https://exemple.org/latest.json") == "https://exemple.org/latest.json");
            T.Check("HTTP en clair refuse", Updater2.SourceUrl("http://exemple.org/latest.json") == null);
            T.Check("autres protocoles refuses", Updater2.SourceUrl("ftp://x/y") == null && Updater2.SourceUrl("file:///c:/x") == null);
            T.Check("formes invalides refusees", Updater2.SourceUrl("") == null && Updater2.SourceUrl("a/b/c") == null && Updater2.SourceUrl("a/../b") == null && Updater2.SourceUrl("/b") == null && Updater2.SourceUrl("a b/c") == null);
        });

        T.Run("T182 lecture des versions publiees (GitHub)", delegate
        {
            string url = "https://github.com/alice/Claude-Sync/releases/download/v2.0.1/Claude-Sync-Setup.exe";
            string one = Release("v2.0.1", false, false, Updater2.AssetName, url, h);
            var u = Updater2.Parse(one);
            T.Check("version, adresse et empreinte lues", u != null && u.Version == "2.0.1" && u.Url == url && u.Sha256 == h && u.Notes.Contains("2.0.1"));
            string list = "[" + Release("v2.0.3", true, true, Updater2.AssetName, url, h) + "," + Release("v2.0.2-beta.1", true, false, Updater2.AssetName, url, h) + "," + Release("v2.0.1", false, false, Updater2.AssetName, url, h) + "," + Release("v2.0.9", false, false, "autre.zip", url, h) + "]";
            var best = Updater2.Parse(list);
            T.Check("liste : brouillon ignore, version preliminaire (beta) acceptee, la plus recente avec le bon fichier", best != null && best.Version == "2.0.2-beta.1", best?.Version);
            T.Check("sans le fichier attendu : rien", Updater2.Parse(Release("v2.1.0", false, false, "autre.zip", url, h)) == null);
            T.Check("adresse non HTTPS : rien", Updater2.Parse(Release("v2.1.0", false, false, Updater2.AssetName, "http://x/y.exe", h)) == null);
            T.Check("empreinte de mauvaise longueur ou non hexadecimale : rien", Updater2.Parse(Release("v2.1.0", false, false, Updater2.AssetName, url, "abc")) == null && Updater2.Parse(Release("v2.1.0", false, false, Updater2.AssetName, url, new string('z', 64))) == null);
            T.Check("sans empreinte du tout : rien (on n'installe jamais sans verification)", Updater2.Parse(Release("v2.1.0", false, false, Updater2.AssetName, url, "")) == null);
            // empreinte publiee dans un fichier .sha256 a cote (format "empreinte  nom")
            string withSum = "{\"tag_name\":\"v2.2.0\",\"assets\":[{\"name\":\"" + Updater2.AssetName + "\",\"browser_download_url\":\"" + url + "\"},{\"name\":\"" + Updater2.AssetName + ".sha256\",\"browser_download_url\":\"https://github.com/x/sum.txt\"}]}";
            var u2 = Updater2.Parse(withSum, a => Encoding.UTF8.GetBytes(h.ToUpperInvariant() + " *" + Updater2.AssetName + "\n"));
            T.Check("empreinte lue dans le fichier .sha256", u2 != null && u2.Sha256 == h && u2.Version == "2.2.0");
            var g = Updater2.Parse("{\"version\":\"3.0.0\",\"url\":\"https://exemple.org/s.exe\",\"sha256\":\"" + h + "\",\"notes\":\"n\"}");
            T.Check("format generique {version, url, sha256}", g != null && g.Version == "3.0.0" && g.Notes == "n");
        });

        T.Run("T183 verification : seulement si plus recent, au plus toutes les 6 h, jamais d'exception", delegate
        {
            string st = Path.Combine(root, "etat1"); int calls = 0;
            string url = "https://github.com/a/b/releases/download/v2.0.1/" + Updater2.AssetName;
            string body = "[" + Release("v2.0.1", false, false, Updater2.AssetName, url, h) + "]";
            Updater2.Http = u => { calls++; return Encoding.UTF8.GetBytes(body); };
            DateTime t0 = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
            var up = Updater2.Check("a/b", "2.0.0", st, false, t0, out string err);
            T.Check("plus recente : proposee", up != null && up.Version == "2.0.1" && err == "", err);
            T.Check("l'adresse interrogee est celle de l'API GitHub", calls == 1);
            var up2 = Updater2.Check("a/b", "2.0.0", st, false, t0.AddHours(5), out string err2);
            T.Check("5 h plus tard : pas de nouvelle requete", up2 == null && err2 == "recent" && calls == 1);
            var up3 = Updater2.Check("a/b", "2.0.0", st, false, t0.AddHours(6.1), out _);
            T.Check("apres 6 h : nouvelle requete", up3 != null && calls == 2);
            var up4 = Updater2.Check("a/b", "2.0.0", st, true, t0.AddHours(6.2), out _);
            T.Check("demande explicite (bouton) : toujours", up4 != null && calls == 3);
            var up5 = Updater2.Check("a/b", "2.0.1", st, true, t0.AddHours(7), out string err5);
            T.Check("deja a jour : rien, sans erreur", up5 == null && err5 == "");
            Updater2.Http = u => throw new System.Net.WebException("hors ligne");
            var up6 = Updater2.Check("a/b", "2.0.0", Path.Combine(root, "etat2"), true, t0, out string err6);
            T.Check("hors ligne : pas de plantage, raison donnee", up6 == null && err6.Contains("hors ligne"), err6);
            Updater2.Http = u => Encoding.UTF8.GetBytes("<html>pas du json</html>");
            T.Check("reponse illisible : pas de plantage", RunsClean(() => Updater2.Check("a/b", "2.0.0", Path.Combine(root, "etat3"), true, t0, out _)));
            T.Check("sans source : rien, sans requete", Updater2.Check("", "2.0.0", st, true, t0, out string e7) == null && e7 == "source");
        });

        T.Run("T184 telechargement : l'empreinte doit correspondre, sinon le fichier est refuse", delegate
        {
            string dest = Path.Combine(root, "dl");
            var good = new UpdateInfo { Version = "2.0.1", Url = "https://x.org/s.exe", Sha256 = h };
            Updater2.Http = u => exe;
            string path = Updater2.Download(good, dest);
            T.Check("fichier conforme : enregistre, octet pour octet", File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(exe) && path.Contains("2.0.1"));
            var other = new UpdateInfo { Version = "2.0.2", Url = "https://x.org/s.exe", Sha256 = Sha(new byte[] { 1, 2, 3 }) };
            T.Check("empreinte differente : refuse, rien d'ecrit", Throws(() => Updater2.Download(other, dest)) && !File.Exists(Path.Combine(dest, "Claude-Sync-Setup-2.0.2.exe")));
            var bad = (byte[])exe.Clone(); bad[0] = (byte)'X'; string hb = Sha(bad);
            Updater2.Http = u => bad;
            T.Check("pas un programme Windows (MZ) : refuse meme avec la bonne empreinte", Throws(() => Updater2.Download(new UpdateInfo { Version = "2.0.3", Url = "https://x.org/s.exe", Sha256 = hb }, dest)));
            T.Check("adresse HTTP refusee sans meme telecharger", Throws(() => Updater2.Download(new UpdateInfo { Version = "2.0.4", Url = "http://x.org/s.exe", Sha256 = h }, dest)));
            Updater2.Http = u => new byte[100];
            T.Check("fichier minuscule refuse", Throws(() => Updater2.Download(new UpdateInfo { Version = "2.0.5", Url = "https://x.org/s.exe", Sha256 = Sha(new byte[100]) }, dest)));
        });

        T.Run("T185 textes de mise a jour : francais et anglais", delegate
        {
            var keys = Loc.Keys.Where(k => k.StartsWith("update.") || k == "tray.update").ToList();
            T.Check("une dizaine de textes, deux langues chacun", keys.Count >= 10 && keys.All(k => Loc.Fr(k).Length > 0 && Loc.En(k).Length > 0), keys.Count.ToString());
        });
    }

    static bool Throws(Action a) { try { a(); return false; } catch (Exception) { return true; } }
    static bool RunsClean(Action a) { try { a(); return true; } catch (Exception) { return false; } }
}
