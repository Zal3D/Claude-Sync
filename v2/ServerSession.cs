// Claude-Sync V2 : session avec le programme serveur (processus enfant : ssh.exe, ou python en local pour les tests).
// Protocole : une requete JSON par ligne, une reponse JSON par ligne suivie de "len" octets bruts. C# 5.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClaudeSync.V2
{
    public static class Json
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static string Serialize(object o) { return Ser.Serialize(o); }

        public static Dictionary<string, object> ParseObject(string s)
        {
            object o = Ser.DeserializeObject(s);
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) throw new FormatException("objet JSON attendu");
            return d;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Lng(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            return v is bool ? (bool)v : false;
        }
    }

    // Comment joindre le serveur par SSH (ssh.exe de MSYS2 embarque avec rsync) : sert a construire la commande.
    public sealed class SshTarget
    {
        public string SshExe;
        public string KeyFile;
        public string KnownHosts;
        public string Host;
        public string User;
        public int Port = 22;
        public int ConnectTimeoutSeconds = 10;
        public string Extra = "";       // options ssh supplementaires (essais : "-o HostKeyAlias=...")
        public string RemoteCommand = "claudesync-serveur";

        static string Q(string s) { return (s.Length == 0 || s.IndexOf(' ') >= 0) ? "\"" + s + "\"" : s; }
        static string Fwd(string p) { return p.Replace('\\', '/'); }

        public string BuildArguments()
        {
            StringBuilder a = new StringBuilder();
            a.Append("-i ").Append(Q(Fwd(KeyFile)));
            a.Append(" -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes");
            a.Append(" -o ConnectTimeout=").Append(ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            a.Append(" -o ServerAliveInterval=15 -o ServerAliveCountMax=3");
            a.Append(" -o UserKnownHostsFile=").Append(Q(Fwd(KnownHosts)));
            a.Append(" -p ").Append(Port.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(Extra)) a.Append(' ').Append(Extra);
            a.Append(' ').Append(User).Append('@').Append(Host);
            a.Append(' ').Append(RemoteCommand);
            return a.ToString();
        }

        public ProcessStartInfo ToStartInfo()
        {
            return new ProcessStartInfo(SshExe, BuildArguments());
        }
    }

    public sealed class ServerSession : IDisposable
    {
        readonly Process p;
        readonly Stream stdin;
        readonly Stream stdout;
        readonly object gate = new object();
        readonly StringBuilder err = new StringBuilder();
        volatile bool timedOut;
        bool disposed;

        public int TimeoutMs = 60000;
        public byte[] Payload = new byte[0];    // octets bruts de la derniere reponse

        public ServerSession(ProcessStartInfo psi)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // .NET Framework ecrit un BOM UTF-8 au tout debut du tube d'entree : on demande un encodage sans BOM quand la propriete existe
            // (le serveur tolere de toute facon un BOM de debut de flux).
            System.Reflection.PropertyInfo enc = typeof(ProcessStartInfo).GetProperty("StandardInputEncoding");
            if (enc != null && enc.CanWrite) enc.SetValue(psi, new UTF8Encoding(false), null);
            try { p = Process.Start(psi); }
            catch (Exception e) { throw new HubException(HubErrKind.Network, "lancement impossible : " + e.Message, e); }
            stdin = p.StandardInput.BaseStream;
            stdout = new BufferedStream(p.StandardOutput.BaseStream, 64 * 1024);
            p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (err) { if (err.Length < 4000) err.AppendLine(e.Data); }
            };
            p.BeginErrorReadLine();
        }

        public string StderrText { get { lock (err) { return err.ToString().Trim(); } } }
        public bool HasExited { get { try { return p.HasExited; } catch (Exception) { return true; } } }

        public void Kill()
        {
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
        }

        public Dictionary<string, object> Call(Dictionary<string, object> req)
        {
            return CallLine(Json.Serialize(req));
        }

        // Envoie une ligne telle quelle (les essais s'en servent pour envoyer du JSON invalide).
        public Dictionary<string, object> CallLine(string jsonLine)
        {
            lock (gate)
            {
                if (disposed) throw new HubException(HubErrKind.Network, "session fermee");
                timedOut = false;
                Timer watchdog = new Timer(delegate { timedOut = true; Kill(); }, null, TimeoutMs, Timeout.Infinite);
                try
                {
                    byte[] data = Encoding.UTF8.GetBytes(jsonLine + "\n");
                    stdin.Write(data, 0, data.Length);
                    stdin.Flush();
                    string line = ReadLine();
                    if (line == null) throw Closed();
                    Dictionary<string, object> resp;
                    try { resp = Json.ParseObject(line); }
                    catch (Exception e) { throw new HubException(HubErrKind.Protocol, "reponse illisible du serveur", e); }
                    long n = Json.Lng(resp, "len");
                    if (n < 0 || n > 512L * 1024 * 1024) throw new HubException(HubErrKind.Protocol, "taille de reponse invalide : " + n);
                    Payload = n > 0 ? ReadExact((int)n) : new byte[0];
                    return resp;
                }
                catch (HubException) { throw; }
                catch (IOException) { throw Closed(); }
                catch (ObjectDisposedException) { throw Closed(); }
                catch (InvalidOperationException) { throw Closed(); }
                finally { watchdog.Dispose(); }
            }
        }

        HubException Closed()
        {
            if (timedOut) return new HubException(HubErrKind.Network, "delai depasse (" + (TimeoutMs / 1000) + " s) : liaison trop lente ou serveur muet");
            string e = StderrText;
            return new HubException(HubErrKind.Network, "connexion fermee par le serveur" + (e.Length > 0 ? " : " + e : ""));
        }

        string ReadLine()
        {
            MemoryStream ms = new MemoryStream();
            while (true)
            {
                int b = stdout.ReadByte();
                if (b < 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
                if (b == '\n') return Encoding.UTF8.GetString(ms.ToArray());
                ms.WriteByte((byte)b);
                if (ms.Length > 4 * 1024 * 1024) throw new HubException(HubErrKind.Protocol, "ligne de reponse trop longue");
            }
        }

        byte[] ReadExact(int n)
        {
            byte[] buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = stdout.Read(buf, got, n - got);
                if (r <= 0) throw Closed();
                got += r;
            }
            return buf;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                try
                {
                    byte[] bye = Encoding.UTF8.GetBytes("{\"cmd\":\"bye\"}\n");
                    stdin.Write(bye, 0, bye.Length); stdin.Flush();
                }
                catch (Exception) { }
                try { stdin.Close(); } catch (Exception) { }
                try { if (!p.WaitForExit(3000)) p.Kill(); } catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
            }
        }
    }
}
