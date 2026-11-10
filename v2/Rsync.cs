// Claude-Sync V2 : transport par rsync (programme embarque avec son ssh.exe). C# 5.
// Le moteur decide QUOI copier ; ce module execute des lots (--files-from) dans un sens ou dans l'autre,
// avec archive cote serveur (--backup-dir), reprise (--partial-dir), compression, et dit exactement quels fichiers ont reussi.
// Jamais d'option de suppression (--delete*, --remove-source-files) : les suppressions passent par la corbeille du serveur.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClaudeSync.V2
{
    public sealed class RsyncOptions
    {
        public string ToolsDir;                       // dossier contenant rsync.exe, ssh.exe et leurs bibliotheques
        public int TimeoutSeconds = 60;               // --timeout : delai sans aucun echange avant abandon
        public bool Compress = true;                  // -z
        public int BwLimitKBps = 0;                   // --bwlimit (0 = illimite)
        public string Chmod = "Fug=rw,Fo=r,Dug=rwx,Do=rx";   // droits des fichiers crees sur le serveur
        public string PartialDirName = ".rsync-partial";   // morceaux recus d'un transfert interrompu (le serveur et le moteur l'ignorent)
        public string TempDirName = ".rsync-tmp";          // fichiers temporaires de rsync, a la racine du hub (hors des jeux de fichiers)
    }

    // L'autre bout du transfert : un serveur joint par SSH, ou (essais) un dossier local qui joue le role du hub.
    public sealed class RsyncRemote
    {
        public SshTarget Ssh;                         // null = mode local
        public string LocalHubRoot;                   // mode local : dossier qui tient lieu de racine du hub
        public bool IsLocal { get { return Ssh == null; } }
    }

    public sealed class TransferResult
    {
        public int ExitCode;
        public List<string> Done = new List<string>();     // fichiers dont le transfert est confirme par rsync
        public List<string> Failed = new List<string>();   // demandes non transferees (absentes, refusees, coupees...)
        public long BytesSent, BytesReceived, Literal, Matched;
        public bool Cancelled, Vanished;
        public HubErrKind? Fatal;                     // erreur d'ensemble (liaison, serveur, protocole) ; null = ok ou transfert partiel
        public string ErrorText = "";
        public bool Success { get { return ExitCode == 0 && !Cancelled; } }
    }

    public static class RsyncUtil
    {
        // "%i|%b|%n" : %b (octets transferes) force rsync a ecrire la ligne APRES le transfert du fichier (avec %i seul, il l'ecrit au debut).
        static readonly Regex Item = new Regex(@"^([<>ch\.\*])([fdLDS])(.{9})\|(\d+)\|(.*)$", RegexOptions.Compiled);
        static readonly Regex Progress = new Regex(@"^\s*([\d,\.]+)\s+(\d+)%", RegexOptions.Compiled);

        // Chemin Windows -> chemin vu par le rsync de MSYS2.
        public static string Cyg(string p)
        {
            string s = p.Replace('\\', '/');
            if (s.StartsWith("//")) return s;                                   // partage reseau
            if (s.Length >= 2 && s[1] == ':') return "/cygdrive/" + char.ToLowerInvariant(s[0]) + s.Substring(2);
            return s;
        }

        // Quote un argument pour la ligne de commande Windows (regles de CommandLineToArgvW).
        public static string Arg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return a;
            StringBuilder sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // Quote un mot pour la valeur de -e (rsync la decoupe sur les espaces ; les guillemets simples ou doubles regroupent).
        public static string Shq(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new char[] { ' ', '\t', '\'', '"' }) < 0) return s;
            if (s.IndexOf('\'') < 0) return "'" + s + "'";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // Liste de fichiers pour --files-from : UTF-8 sans BOM, fins de ligne LF.
        public static byte[] ListBytes(IList<string> rels)
        {
            return new UTF8Encoding(false).GetBytes(string.Join("\n", rels) + "\n");
        }

        public static long Num(string s)
        {
            long v; string d = Regex.Replace(s, @"[^\d]", "");
            return long.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // Analyse une ligne de sortie de rsync (liste des fichiers transferes, statistiques, progression).
        public static void ParseLine(string line, TransferResult r, Action<long> onBytes)
        {
            Match m = Item.Match(line);
            if (m.Success)
            {
                string act = m.Groups[1].Value, typ = m.Groups[2].Value;
                if (typ == "f" && (act == "<" || act == ">")) lock (r) { r.Done.Add(m.Groups[5].Value); }
                return;
            }
            m = Progress.Match(line);
            if (m.Success) { if (onBytes != null) onBytes(Num(m.Groups[1].Value)); return; }
            int i = line.IndexOf(": ", StringComparison.Ordinal);
            if (i < 0) return;
            string k = line.Substring(0, i), v = line.Substring(i + 2);
            lock (r)
            {
                if (k == "Total bytes sent") r.BytesSent = Num(v);
                else if (k == "Total bytes received") r.BytesReceived = Num(v);
                else if (k == "Literal data") r.Literal = Num(v);
                else if (k == "Matched data") r.Matched = Num(v);
            }
        }

        // Code de sortie de rsync -> nature de l'erreur d'ensemble (null = pas d'erreur d'ensemble : succes ou transfert partiel).
        public static HubErrKind? FatalKind(int exit, string stderr)
        {
            if (exit == 0 || exit == 23 || exit == 24 || exit == 20) return null;
            if (stderr.IndexOf("Host key verification failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stderr.IndexOf("Permission denied (publickey", StringComparison.OrdinalIgnoreCase) >= 0) return HubErrKind.Refused;
            if (exit == 10 || exit == 12 || exit == 30 || exit == 35 || exit == 255) return HubErrKind.Network;
            if (exit == 11 || exit == 13 || exit == 14) return HubErrKind.Server;
            return HubErrKind.Protocol;
        }

        // Arrete le ssh.exe de CE transfert : sous MSYS il n'apparait pas comme enfant de rsync dans Windows, on le reconnait donc a
        // l'etiquette unique (-o Tag=...) placee dans sa ligne de commande. Retourne le nombre de processus arretes.
        public static int StopSshByMarker(string marker)
        {
            int n = 0;
            if (string.IsNullOrEmpty(marker)) return 0;
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher("SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name = 'ssh.exe'"))
                    foreach (System.Management.ManagementBaseObject mo in s.Get())
                    {
                        string cl = Convert.ToString(mo["CommandLine"]) ?? "";
                        if (cl.IndexOf("Tag=" + marker, StringComparison.Ordinal) < 0) continue;
                        try { Process.GetProcessById(Convert.ToInt32(mo["ProcessId"])).Kill(); n++; } catch (Exception) { }
                    }
            }
            catch (Exception) { }
            return n;
        }

        public static void KillTree(int pid)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/T /F /PID " + pid);
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi)) { p.WaitForExit(5000); }
            }
            catch (Exception) { }
        }
    }

    // Ce que le moteur demande au transport (les essais du moteur peuvent le remplacer par un faux).
    public interface ITransfer
    {
        Action<long> OnBytes { get; set; }           // progression : octets du lot en cours
        Func<bool> Cancel { get; set; }              // retourne vrai pour interrompre (la reprise repartira de la partie deja recue)
        TransferResult Push(string localRoot, IList<string> rels, string hubDir, string hubBackupDir);
        TransferResult Pull(string hubDir, IList<string> rels, string localRoot, string localBackupDir);
    }

    public sealed class RsyncTransport : ITransfer
    {
        readonly RsyncOptions o;
        readonly RsyncRemote remote;
        string marker = "cs0";                          // etiquette du ssh du transfert en cours
        public Action<long> OnBytes { get; set; }       // progression : octets du fichier ou du lot en cours
        public Func<bool> Cancel { get; set; }          // retourne vrai pour interrompre (la reprise repartira de la partie deja recue)

        public RsyncTransport(RsyncOptions options, RsyncRemote r) { o = options; remote = r; }

        string Exe { get { return Path.Combine(o.ToolsDir, "rsync.exe"); } }

        string ShellArgument()
        {
            SshTarget t = remote.Ssh;
            List<string> w = new List<string>();
            w.Add(RsyncUtil.Cyg(Path.Combine(o.ToolsDir, "ssh.exe")));
            w.Add("-F"); w.Add("none");
            w.Add("-i"); w.Add(RsyncUtil.Cyg(t.KeyFile));
            w.Add("-o"); w.Add("BatchMode=yes");
            w.Add("-o"); w.Add("IdentitiesOnly=yes");
            w.Add("-o"); w.Add("StrictHostKeyChecking=yes");
            w.Add("-o"); w.Add("GlobalKnownHostsFile=/dev/null");
            w.Add("-o"); w.Add("UserKnownHostsFile=" + RsyncUtil.Cyg(t.KnownHosts));
            w.Add("-o"); w.Add("ConnectTimeout=" + t.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            w.Add("-o"); w.Add("ServerAliveInterval=15");
            w.Add("-o"); w.Add("ServerAliveCountMax=3");
            w.Add("-p"); w.Add(t.Port.ToString(CultureInfo.InvariantCulture));
            w.Add("-o"); w.Add("Tag=" + marker);                 // etiquette unique : permet d'arreter ce ssh seul (annulation douce)
            StringBuilder sb = new StringBuilder();
            foreach (string s in w) { if (sb.Length > 0) sb.Append(' '); sb.Append(RsyncUtil.Shq(s)); }
            return sb.ToString();
        }

        static string Dir(string d) { return d.EndsWith("/") ? d : d + "/"; }

        // Adresse du dossier du hub vue par rsync.
        string HubSpec(string hubDir)
        {
            if (remote.IsLocal) return RsyncUtil.Cyg(remote.LocalHubRoot).TrimEnd('/') + Dir(hubDir);
            return remote.Ssh.User + "@" + remote.Ssh.Host + ":" + Dir(hubDir);
        }

        // Dossier d'archive du hub : chemin absolu DANS le hub (rrsync le ramene a la racine du hub) ; en mode local, vrai chemin.
        string HubBackupArg(string hubBackupDir)
        {
            if (remote.IsLocal) return RsyncUtil.Cyg(remote.LocalHubRoot).TrimEnd('/') + hubBackupDir;
            return hubBackupDir;
        }

        List<string> Common(string listFile)
        {
            List<string> a = new List<string>();
            a.Add("-tI"); a.Add("-8");
            a.Add("--timeout=" + o.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            a.Add("--stats"); a.Add("--info=progress2"); a.Add("--out-format=%i|%b|%n");
            if (o.Compress) a.Add("-z");
            if (o.BwLimitKBps > 0) a.Add("--bwlimit=" + o.BwLimitKBps.ToString(CultureInfo.InvariantCulture));
            a.Add("--files-from=" + RsyncUtil.Cyg(listFile));
            if (remote.IsLocal) a.Add("--no-whole-file"); else { a.Add("-e"); a.Add(ShellArgument()); }
            return a;
        }

        // Arguments d'un envoi PC -> serveur (public : les essais verifient la composition).
        public List<string> BuildPushArgs(string localRoot, string hubDir, string hubBackupDir, string listFile)
        {
            List<string> a = Common(listFile);
            a.Add("-p"); a.Add("--chmod=" + o.Chmod);
            a.Add("--partial-dir=" + o.PartialDirName);
            a.Add("--temp-dir=" + (remote.IsLocal ? RsyncUtil.Cyg(remote.LocalHubRoot).TrimEnd('/') : "") + "/" + o.TempDirName);
            if (hubBackupDir != null) { a.Add("--backup"); a.Add("--backup-dir=" + HubBackupArg(hubBackupDir)); }
            a.Add(RsyncUtil.Cyg(localRoot).TrimEnd('/') + "/");
            a.Add(HubSpec(hubDir));
            return a;
        }

        // Arguments d'une recuperation serveur -> PC.
        public List<string> BuildPullArgs(string hubDir, string localRoot, string localBackupDir, string listFile)
        {
            List<string> a = Common(listFile);
            a.Add("--partial-dir=" + RsyncUtil.Cyg(localRoot).TrimEnd('/') + "/" + o.PartialDirName);
            a.Add("--temp-dir=" + RsyncUtil.Cyg(localRoot).TrimEnd('/') + "/" + o.PartialDirName);
            if (localBackupDir != null) { a.Add("--backup"); a.Add("--backup-dir=" + RsyncUtil.Cyg(localBackupDir)); }
            a.Add(HubSpec(hubDir));
            a.Add(RsyncUtil.Cyg(localRoot).TrimEnd('/') + "/");
            return a;
        }

        public TransferResult Push(string localRoot, IList<string> rels, string hubDir, string hubBackupDir)
        {
            if (remote.IsLocal && rels.Count > 0) Directory.CreateDirectory(Path.Combine(remote.LocalHubRoot, o.TempDirName));
            return Run(rels,delegate (string list) { return BuildPushArgs(localRoot, hubDir, hubBackupDir, list); });
        }

        public TransferResult Pull(string hubDir, IList<string> rels, string localRoot, string localBackupDir)
        {
            Directory.CreateDirectory(localRoot);
            Directory.CreateDirectory(Path.Combine(localRoot, o.PartialDirName));
            return Run(rels, delegate (string list) { return BuildPullArgs(hubDir, localRoot, localBackupDir, list); });
        }

        TransferResult Run(IList<string> rels, Func<string, List<string>> build)
        {
            TransferResult r = new TransferResult();
            if (rels.Count == 0) return r;
            if (!File.Exists(Exe)) throw new HubException(HubErrKind.Server, "rsync.exe introuvable dans " + o.ToolsDir);
            string list = Path.Combine(Path.GetTempPath(), "cs-rsync-" + Guid.NewGuid().ToString("N") + ".lst");
            File.WriteAllBytes(list, RsyncUtil.ListBytes(rels));
            marker = "cs" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                List<string> args = build(list);
                StringBuilder cl = new StringBuilder();
                foreach (string a in args) { if (cl.Length > 0) cl.Append(' '); cl.Append(RsyncUtil.Arg(a)); }
                ProcessStartInfo psi = new ProcessStartInfo(Exe, cl.ToString());
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false); psi.StandardErrorEncoding = new UTF8Encoding(false);
                psi.WorkingDirectory = Path.GetTempPath();
                psi.EnvironmentVariables["MSYS2_ARG_CONV_EXCL"] = "*";
                StringBuilder err = new StringBuilder();
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) RsyncUtil.ParseLine(e.Data, r, OnBytes); };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (err) { if (err.Length < 8000) err.AppendLine(e.Data); } };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    while (!p.WaitForExit(200))
                    {
                        if (Cancel != null && Cancel())
                        {
                            r.Cancelled = true;
                            // On coupe d'abord la connexion SSH : rsync voit la liaison tomber, garde la partie deja recue (reprise possible) et sort seul.
                            if (RsyncUtil.StopSshByMarker(marker) > 0 && p.WaitForExit(8000)) break;
                            RsyncUtil.KillTree(p.Id);
                            break;
                        }
                    }
                    p.WaitForExit();
                    r.ExitCode = p.ExitCode;
                }
                r.ErrorText = err.ToString().Trim();
                if (r.ExitCode == 24) r.Vanished = true;
                r.Fatal = r.Cancelled ? null : RsyncUtil.FatalKind(r.ExitCode, r.ErrorText);
                // Annulation ou echec d'ensemble : aucune ligne n'est fiable, on ne confirme rien (le moteur reverifiera de toute facon).
                if (r.Cancelled || (r.ExitCode != 0 && r.ExitCode != 23 && r.ExitCode != 24)) r.Done.Clear();
                HashSet<string> done = new HashSet<string>(r.Done, StringComparer.Ordinal);
                foreach (string rel in rels) if (!done.Contains(rel)) r.Failed.Add(rel);
                return r;
            }
            finally { try { File.Delete(list); } catch (Exception) { } }
        }
    }
}
