// Claude-Sync V2 : HubServeur = IHub realise avec le programme serveur (claudesync-serveur). C# 5.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ClaudeSync.V2
{
    public sealed class HubServeur : IHub
    {
        public const int ProtoAttendu = 1;
        readonly ServerSession s;

        public HubServeur(ServerSession session) { s = session; }
        public ServerSession Session { get { return s; } }

        // Production : le serveur est joint par SSH.
        public static HubServeur OpenSsh(SshTarget t) { return new HubServeur(new ServerSession(t.ToStartInfo())); }

        // Essais : le programme serveur tourne en local (python) sur un dossier.
        public static HubServeur OpenLocal(string python, string script, string root, IDictionary<string, string> env)
        {
            ProcessStartInfo psi = new ProcessStartInfo(python, "\"" + script + "\"");
            psi.EnvironmentVariables["CS_ROOT"] = root;
            if (env != null) foreach (KeyValuePair<string, string> kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            return new HubServeur(new ServerSession(psi));
        }

        static Dictionary<string, object> R(string cmd, params object[] kv)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["cmd"] = cmd;
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        // Appel dont l'echec ("ok": false) est une erreur.
        Dictionary<string, object> Do(Dictionary<string, object> req)
        {
            Dictionary<string, object> resp = s.Call(req);
            if (!Json.Bool(resp, "ok"))
            {
                string e = Json.Str(resp, "err") ?? "erreur inconnue";
                throw new HubException(e.StartsWith("ValueError") ? HubErrKind.Refused : HubErrKind.Server, e);
            }
            return resp;
        }

        public HelloInfo Hello()
        {
            Dictionary<string, object> r = Do(R("hello"));
            HelloInfo h = new HelloInfo();
            h.ServerVersion = Json.Str(r, "version") ?? "";
            h.Proto = (int)Json.Lng(r, "proto");
            h.HubId = Json.Str(r, "hub_id") ?? "";
            h.ServerTicks = Json.Lng(r, "temps_ticks");
            if (h.Proto != ProtoAttendu)
                throw new HubException(HubErrKind.Protocol, "protocole du serveur " + h.Proto + " (version " + h.ServerVersion + "), attendu " + ProtoAttendu + " : mettre a jour le serveur ou Claude-Sync");
            return h;
        }

        public int PingMs(int samples)
        {
            if (samples < 1) samples = 1;
            List<long> t = new List<long>();
            for (int i = 0; i < samples; i++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                Do(R("ping"));
                t.Add(sw.ElapsedMilliseconds);
            }
            t.Sort();
            return (int)t[t.Count / 2];
        }

        public ListResult List(string set, string ifChangedToken)
        {
            Dictionary<string, object> req = R("list", "set", set);
            if (!string.IsNullOrEmpty(ifChangedToken)) req["if_changed"] = ifChangedToken;
            Dictionary<string, object> r = Do(req);
            ListResult res = new ListResult();
            res.Token = Json.Str(r, "token");
            res.Count = (int)Json.Lng(r, "count");
            res.Ignored = (int)Json.Lng(r, "ignored");
            res.Errors = (int)Json.Lng(r, "errors");
            if (Json.Bool(r, "unchanged")) { res.Unchanged = true; return res; }
            string text;
            try
            {
                using (GZipStream gz = new GZipStream(new MemoryStream(s.Payload), CompressionMode.Decompress))
                using (MemoryStream outp = new MemoryStream())
                {
                    gz.CopyTo(outp);
                    text = Encoding.UTF8.GetString(outp.ToArray());
                }
            }
            catch (Exception e) { throw new HubException(HubErrKind.Protocol, "listage illisible (donnees compressees invalides)", e); }
            res.Files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                long size, ticks;
                if (f.Length != 3 || !long.TryParse(f[1], out size) || !long.TryParse(f[2], out ticks))
                    throw new HubException(HubErrKind.Protocol, "ligne de listage invalide : " + line);
                if (res.Files.ContainsKey(f[0])) { res.CaseCollisions.Add(f[0]); continue; }
                res.Files[f[0]] = new FileEntry { Rel = f[0], Size = size, Ticks = ticks };
            }
            if (res.Files.Count + res.CaseCollisions.Count != res.Count)
                throw new HubException(HubErrKind.Protocol, "listage incoherent : " + (res.Files.Count + res.CaseCollisions.Count) + " lignes pour " + res.Count + " annoncees");
            return res;
        }

        public Dictionary<string, FileEntry> Stat(string set, IList<string> rels)
        {
            List<object> l = new List<object>();
            foreach (string r in rels) l.Add(r);
            Dictionary<string, object> resp = Do(R("stat", "set", set, "rels", l));
            Dictionary<string, FileEntry> res = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> st = resp.ContainsKey("stat") ? resp["stat"] as Dictionary<string, object> : null;
            if (st == null) throw new HubException(HubErrKind.Protocol, "reponse stat invalide");
            foreach (KeyValuePair<string, object> kv in st)
            {
                System.Collections.IList a = kv.Value as System.Collections.IList;
                if (a == null || a.Count != 2) { res[kv.Key] = null; continue; }
                res[kv.Key] = new FileEntry { Rel = kv.Key, Size = Convert.ToInt64(a[0]), Ticks = Convert.ToInt64(a[1]) };
            }
            return res;
        }

        public LockResult AcquireLock(string pc)
        {
            Dictionary<string, object> r = s.Call(R("lock.acquire", "pc", pc));
            LockResult l = new LockResult();
            l.Ok = Json.Bool(r, "ok");
            l.Holder = Json.Str(r, "holder");
            l.AgeSeconds = Json.Lng(r, "age_s");
            l.TookOrphan = Json.Bool(r, "reprise_orphelin");
            return l;
        }

        public void ReleaseLock() { Do(R("lock.release")); }

        public void WritePresence(string pc, bool running) { Do(R("presence.write", "pc", pc, "running", running)); }

        public List<PresenceInfo> ReadPresence()
        {
            Dictionary<string, object> r = Do(R("presence.read"));
            List<PresenceInfo> res = new List<PresenceInfo>();
            System.Collections.IList arr = r.ContainsKey("pcs") ? r["pcs"] as System.Collections.IList : null;
            if (arr == null) return res;
            foreach (object o in arr)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                res.Add(new PresenceInfo { Pc = Json.Str(d, "pc"), Ticks = Json.Lng(d, "ticks"), Running = Json.Bool(d, "running") });
            }
            return res;
        }

        bool Move(string cmd, Dictionary<string, object> req)
        {
            Dictionary<string, object> r = s.Call(req);
            if (Json.Bool(r, "ok")) return true;
            string e = Json.Str(r, "err") ?? "erreur inconnue";
            if (e == "absent") return false;
            throw new HubException(e.StartsWith("ValueError") ? HubErrKind.Refused : HubErrKind.Server, e);
        }

        public bool Trash(string set, string rel, string stamp, string pc)
        {
            return Move("trash", R("trash", "set", set, "rel", rel, "stamp", stamp, "pc", pc));
        }

        public bool Archive(string set, string rel, string stamp)
        {
            return Move("archive", R("archive", "set", set, "rel", rel, "stamp", stamp));
        }

        public TrashListing TrashList()
        {
            Do(R("trash.list"));
            string text;
            try
            {
                using (GZipStream gz = new GZipStream(new MemoryStream(s.Payload), CompressionMode.Decompress))
                using (MemoryStream outp = new MemoryStream())
                {
                    gz.CopyTo(outp);
                    text = Encoding.UTF8.GetString(outp.ToArray());
                }
            }
            catch (Exception e) { throw new HubException(HubErrKind.Protocol, "liste de la corbeille illisible", e); }
            TrashListing res = new TrashListing();
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                long size;
                if (f[0] == "P" && f.Length == 3) res.PcByStamp[f[1]] = f[2];
                else if (f[0] == "F" && f.Length == 7 && long.TryParse(f[4], out size))
                    res.Files.Add(new TrashFile { Stamp = f[1], Set = f[2], Rel = f[3], Size = size, Cli = f[5], Title = f[6] });
                else throw new HubException(HubErrKind.Protocol, "ligne de corbeille invalide : " + line);
            }
            return res;
        }

        public bool RestoreFromTrash(string stamp, string set, string rel)
        {
            Dictionary<string, object> r = s.Call(R("trash.restore", "stamp", stamp, "set", set, "rel", rel));
            if (Json.Bool(r, "ok")) return Json.Bool(r, "restored");
            string e = Json.Str(r, "err") ?? "erreur inconnue";
            if (e == "absent") return false;                      // deja restaure ou purge : rien a faire
            throw new HubException(e.StartsWith("ValueError") ? HubErrKind.Refused : HubErrKind.Server, e);
        }

        public string Hash(string set, string rel)
        {
            Dictionary<string, object> r = s.Call(R("hash", "set", set, "rel", rel));
            if (Json.Bool(r, "ok")) return Json.Str(r, "sha256");
            string e = Json.Str(r, "err") ?? "erreur inconnue";
            if (e == "absent") return null;
            throw new HubException(e.StartsWith("ValueError") ? HubErrKind.Refused : HubErrKind.Server, e);
        }

        public bool Touch(string set, string rel, long ticks)
        {
            return Move("touch", R("touch", "set", set, "rel", rel, "ticks", ticks));
        }

        public List<string> Purge(int days, bool dryRun)
        {
            Dictionary<string, object> r = Do(R("purge", "days", days, "dry", dryRun));
            List<string> res = new List<string>();
            System.Collections.IList arr = r.ContainsKey("dossiers") ? r["dossiers"] as System.Collections.IList : null;
            if (arr != null) foreach (object o in arr) res.Add(Convert.ToString(o));
            return res;
        }

        public void Dispose() { s.Dispose(); }
    }
}
