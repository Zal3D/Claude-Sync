// Claude-Sync V2 : inscription d'un PC aupres d'un serveur SSH (portage en C# du prototype v2-proto\enroler.py).
// Un acces d'amorcage (mot de passe via SSH_ASKPASS, jamais ecrit sur le disque) sert UNE FOIS a : controler le serveur, deposer le programme
// serveur dans le dossier de l'utilisateur, creer le dossier de donnees, fabriquer la cle PROPRE A CE PC et l'ajouter limitee a Claude-Sync.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeSync.V2;

public interface IRemoteShell
{
    // Execute un script sh sur le serveur (lu sur l'entree standard) ; retourne (code, sortie, erreurs).
    (int code, string output, string error) RunScript(string script);
    // Execute une commande en lui donnant des donnees sur l'entree standard (depot de fichiers).
    (int code, string output, string error) RunCommand(string command, byte[] stdin);
}

public sealed class EnrollRequest
{
    public string Host = "", User = "", Folder = "claude-sync", Pc = Environment.MachineName, ToolsDir = "", StateDir = "", ProjetsRoot = @"C:\Projets";
    public int Port = 22;
    public string? Password;                                   // amorcage par mot de passe (jamais ecrit sur le disque)
    public string AskpassExe = "";                             // programme que ssh lance pour obtenir le mot de passe (ClaudeSync2.exe --askpass)
    public IDictionary<string, byte[]> ServerFiles = new Dictionary<string, byte[]>();   // claudesync-serveur, claudesync-entree
    public Func<string, bool> ConfirmFingerprint = _ => false;   // l'utilisateur doit confirmer l'empreinte du serveur (refus par defaut)
    public Action<string>? Progress;
}

public sealed class EnrollResult
{
    public bool Ok;
    public string Message = "";
    public AppConfig? Config;
}

public static class Enroller
{
    public const string ControlScript = @"
echo ""HOME=$HOME""
echo ""OS=$(uname -s)""
[ -f /etc/synoinfo.conf ] && echo ""NAS=Synology""
[ -f /etc/os-release ] && . /etc/os-release && echo ""DISTRO=$PRETTY_NAME""
for c in rsync python3; do p=$(command -v $c 2>/dev/null) && echo ""OK_$c=$p"" || echo ""MANQUE_$c=1""; done
python3 -c 'import sys;print(""PYVER=%d.%d""%sys.version_info[:2])' 2>/dev/null
rsync --version 2>/dev/null | head -1 | sed 's/^/RSYNCVER=/'
for p in /usr/bin/rrsync /usr/lib/rsync/rrsync /usr/share/doc/rsync/support/rrsync /opt/bin/rrsync /bin/rrsync /usr/local/bin/rrsync; do [ -x $p ] && { echo ""RRSYNC=$p""; break; }; done
";

    // ---------- fonctions pures (testees seules)
    public static Dictionary<string, string> ParseControl(string output)
    {
        var d = new Dictionary<string, string>();
        foreach (string l in output.Replace("\r", "").Split('\n')) { int i = l.IndexOf('='); if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1); }
        return d;
    }

    // Problemes bloquants, avec le conseil adapte au systeme.
    public static List<string> Problems(Dictionary<string, string> info)
    {
        var p = new List<string>();
        if (info.ContainsKey("MANQUE_rsync")) p.Add("rsync est absent du serveur (Debian/Ubuntu : apt install rsync ; Synology : activer le service rsync).");
        if (info.ContainsKey("MANQUE_python3")) p.Add("Python 3 est absent du serveur (Synology : installer le paquet « Python 3 » ; QNAP : via l'App Center ; Debian/Ubuntu : apt install python3).");
        else if (info.TryGetValue("PYVER", out var v) && Version.TryParse(v, out var pv) && pv < new Version(3, 6)) p.Add("Python " + v + " est trop ancien (3.6 minimum).");
        if (!info.ContainsKey("RRSYNC")) p.Add("rrsync est introuvable : la cle ne pourrait pas etre limitee au dossier choisi.");
        return p;
    }

    public static string ShQ(string s)
    {
        if (s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "/._-+:@%,".IndexOf(c) >= 0)) return s;
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    // Ligne d'authorized_keys : la cle ne peut lancer que l'aiguilleur Claude-Sync (rsync dans le dossier, ou le programme serveur).
    public static string AuthorizedLine(string hubDir, string home, string pubType, string pubKey, string pc)
    {
        if (hubDir.Contains('"') || home.Contains('"') || pc.Any(char.IsWhiteSpace)) throw new ArgumentException("caractere interdit dans le dossier ou le nom du PC");
        return "restrict,command=\"CS_ROOT=" + ShQ(hubDir) + " " + home + "/.claudesync/claudesync-entree\" " + pubType + " " + pubKey + " claude-sync:" + pc;
    }

    static string Fwd(string p) => p.Replace('\\', '/');

    // ---------- processus
    // Les donnees destinees au serveur passent par un FICHIER TEMPORAIRE redirige vers l'entree du programme (cmd.exe "< fichier") :
    // .NET Framework ecrirait sinon un BOM UTF-8 au debut du tube (selon la page de codes de la console), ce qui casserait un script
    // ou un fichier envoye. Le contenu n'est jamais interprete ni recode par .NET.
    static (int, string, string) Exec(ProcessStartInfo psi, byte[]? stdin, int timeoutMs = 60000)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "cs-in-" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllBytes(tmp, stdin ?? Array.Empty<byte>());
        try
        {
            var real = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false };
            real.StandardOutputEncoding = new UTF8Encoding(false); real.StandardErrorEncoding = new UTF8Encoding(false);
            foreach (System.Collections.DictionaryEntry de in psi.EnvironmentVariables) real.EnvironmentVariables[(string)de.Key] = de.Value as string ?? "";
            real.Arguments = "/s /c \"\"" + psi.FileName + "\" " + psi.Arguments + " < \"" + tmp + "\"\"";
            var o = new StringBuilder(); var e = new StringBuilder();
            using var p = Process.Start(real)!;
            p.OutputDataReceived += (_, a) => { if (a.Data != null) lock (o) o.AppendLine(a.Data); };
            p.ErrorDataReceived += (_, a) => { if (a.Data != null) lock (e) e.AppendLine(a.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs)) { RsyncUtil.KillTree(p.Id); return (-1, o.ToString(), "delai depasse"); }
            p.WaitForExit();
            return (p.ExitCode, o.ToString(), e.ToString().Trim());
        }
        finally { try { File.Delete(tmp); } catch (Exception) { } }
    }

    // Acces SSH d'amorcage au compte de l'utilisateur (mot de passe par SSH_ASKPASS, ou cle d'essai).
    public sealed class SshShell : IRemoteShell
    {
        readonly EnrollRequest r; readonly string known; readonly string? bootKey; readonly string? loginOverride; readonly string? suUser;
        public SshShell(EnrollRequest r, string knownHosts, string? bootstrapKey = null, string? login = null, string? suUser = null)
        { this.r = r; known = knownHosts; bootKey = bootstrapKey; loginOverride = login; this.suUser = suUser; }

        ProcessStartInfo Build(string remote)
        {
            var a = new List<string> { "-F", "none", "-o", "StrictHostKeyChecking=yes", "-o", "GlobalKnownHostsFile=/dev/null", "-o", "UserKnownHostsFile=" + Fwd(known), "-o", "ConnectTimeout=10", "-p", r.Port.ToString(CultureInfo.InvariantCulture) };
            var psi = new ProcessStartInfo(Path.Combine(r.ToolsDir, "ssh.exe"));
            if (bootKey != null) { a.AddRange(new[] { "-i", Fwd(bootKey), "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes" }); }
            else
            {
                a.AddRange(new[] { "-o", "PreferredAuthentications=password,keyboard-interactive", "-o", "PubkeyAuthentication=no", "-o", "NumberOfPasswordPrompts=1" });
                psi.EnvironmentVariables["SSH_ASKPASS"] = RsyncUtil.Cyg(r.AskpassExe);
                psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                psi.EnvironmentVariables["DISPLAY"] = "none";
                psi.EnvironmentVariables["CS_ASKPASS_VALUE"] = r.Password ?? "";
            }
            a.Add(loginOverride ?? r.User + "@" + r.Host);
            a.Add(suUser != null ? "su -l -s /bin/sh " + suUser + " -c " + ShQ(remote) : remote);
            psi.Arguments = string.Join(" ", a.Select(RsyncUtil.Arg));
            psi.EnvironmentVariables["MSYS2_ARG_CONV_EXCL"] = "*";
            return psi;
        }

        public (int code, string output, string error) RunScript(string script) => Exec(Build("sh -s"), Encoding.UTF8.GetBytes(script.Replace("\r", "")));
        public (int code, string output, string error) RunCommand(string command, byte[] stdin) => Exec(Build(command), stdin);
    }

    // Memorise la cle du serveur SANS envoyer de mot de passe (aucune authentification n'est tentee), pour la faire confirmer avant d'aller plus loin.
    public static string? PinHostKey(EnrollRequest r, string knownHosts)
    {
        var psi = new ProcessStartInfo(Path.Combine(r.ToolsDir, "ssh.exe"));
        psi.EnvironmentVariables["MSYS2_ARG_CONV_EXCL"] = "*";
        psi.Arguments = string.Join(" ", new[] { "-F", "none", "-o", "StrictHostKeyChecking=accept-new", "-o", "GlobalKnownHostsFile=/dev/null", "-o", "UserKnownHostsFile=" + Fwd(knownHosts),
            "-o", "PreferredAuthentications=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-p", r.Port.ToString(CultureInfo.InvariantCulture), r.User + "@" + r.Host, "exit" }.Select(RsyncUtil.Arg));
        Exec(psi, null, 30000);                                   // l'echec d'authentification est attendu : seule la cle du serveur nous interesse
        if (!File.Exists(knownHosts)) return null;
        var kg = new ProcessStartInfo(Path.Combine(r.ToolsDir, "ssh-keygen.exe"), "-lf " + RsyncUtil.Arg(Fwd(knownHosts)));
        kg.EnvironmentVariables["MSYS2_ARG_CONV_EXCL"] = "*";
        var (_, o, _) = Exec(kg, null, 15000);
        return o.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
    }

    // Cle ed25519 sans phrase de passe, propre a ce PC.
    public static (bool ok, string pubType, string pubKey, string message) GenerateKey(string toolsDir, string keyFile, string pc)
    {
        if (!File.Exists(keyFile))
        {
            var psi = new ProcessStartInfo(Path.Combine(toolsDir, "ssh-keygen.exe"));
            psi.EnvironmentVariables["MSYS2_ARG_CONV_EXCL"] = "*";
            psi.Arguments = string.Join(" ", new[] { "-q", "-t", "ed25519", "-N", "", "-C", "claude-sync:" + pc, "-f", Fwd(keyFile) }.Select(RsyncUtil.Arg));
            var (code, _, err) = Exec(psi, null, 30000);
            if (code != 0 || !File.Exists(keyFile + ".pub")) return (false, "", "", "creation de la cle impossible : " + err);
        }
        string[] pub = File.ReadAllText(keyFile + ".pub", Encoding.UTF8).Trim().Split(' ');
        return pub.Length >= 2 ? (true, pub[0], pub[1], "") : (false, "", "", "cle publique illisible");
    }

    // ---------- l'inscription
    public static EnrollResult Run(EnrollRequest r, IRemoteShell? injected = null)
    {
        void Step(string s) { r.Progress?.Invoke(s); }
        EnrollResult Fail(string m) => new() { Ok = false, Message = m };
        Directory.CreateDirectory(r.StateDir);
        string sshDir = Path.Combine(r.StateDir, "ssh"); Directory.CreateDirectory(sshDir);
        string known = Path.Combine(sshDir, "known_hosts"), keyFile = Path.Combine(sshDir, "id_ed25519");

        Step("Empreinte du serveur");
        string? fp = PinHostKey(r, known);
        if (fp == null) return Fail("Serveur injoignable (" + r.Host + ":" + r.Port + ").");
        if (!r.ConfirmFingerprint(fp)) { try { File.Delete(known); } catch (Exception) { } return Fail("Empreinte du serveur refusee : inscription annulee, rien n'a ete envoye.");  }

        IRemoteShell shell = injected ?? new SshShell(r, known);
        Step("Controle du serveur");
        var (c0, out0, err0) = shell.RunScript(ControlScript);
        if (c0 != 0 && out0.Trim().Length == 0) return Fail(err0.Contains("Permission denied") ? "Utilisateur ou mot de passe refuse." : "Connexion impossible : " + err0);
        var info = ParseControl(out0);
        var problems = Problems(info);
        if (problems.Count > 0) return Fail("Le serveur n'est pas pret : " + string.Join(" ", problems));
        string home = info["HOME"];
        string dir = r.Folder.StartsWith("/") ? r.Folder : home + "/" + r.Folder;

        Step("Depot du programme serveur");
        shell.RunScript("mkdir -p ~/.claudesync && chmod 700 ~/.claudesync");
        foreach (var kv in r.ServerFiles)
        {
            var (c, _, e) = shell.RunCommand("cat > ~/.claudesync/" + kv.Key + ".new && chmod 700 ~/.claudesync/" + kv.Key + ".new && mv ~/.claudesync/" + kv.Key + ".new ~/.claudesync/" + kv.Key, kv.Value.Where(b => b != 13).ToArray());
            if (c != 0) return Fail("Depot de " + kv.Key + " impossible : " + e);
        }

        Step("Dossier de donnees");
        string hid = Guid.NewGuid().ToString("N");
        var (c1, out1, err1) = shell.RunScript("mkdir -p " + ShQ(dir) + "/_etat && cd " + ShQ(dir) + " && pwd -P && { [ -f _etat/hub.id ] && echo EXISTANT || { printf '%s' " + hid + " > _etat/hub.id; echo CREE; }; }");
        string[] l1 = out1.Replace("\r", "").Split('\n').Where(x => x.Length > 0).ToArray();
        if (c1 != 0 || l1.Length < 2) return Fail("Dossier de donnees impossible : " + err1);
        string real = l1[l1.Length - 2];

        Step("Cle de ce PC");
        var key = GenerateKey(r.ToolsDir, keyFile, r.Pc);
        if (!key.ok) return Fail(key.message);
        string line = AuthorizedLine(real, home, key.pubType, key.pubKey, r.Pc);
        var (c2, out2, err2) = shell.RunCommand("mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys && grep -v ' claude-sync:" + r.Pc + "$' ~/.ssh/authorized_keys > ~/.ssh/ak.tmp; cat >> ~/.ssh/ak.tmp && cat ~/.ssh/ak.tmp > ~/.ssh/authorized_keys && rm -f ~/.ssh/ak.tmp && echo OK", Encoding.UTF8.GetBytes(line + "\n"));
        if (!out2.Contains("OK")) return Fail("Ajout de la cle impossible : " + err2);

        var cfg = new AppConfig { Host = r.Host, Port = r.Port, User = r.User, Folder = real, Pc = r.Pc, KeyFile = keyFile, KnownHosts = known, ToolsDir = r.ToolsDir, ProjetsRoot = r.ProjetsRoot };

        Step("Verification");
        try
        {
            using var hub = HubServeur.OpenSsh(cfg.ToTarget());
            var h = hub.Hello();
            if (h.HubId.Length == 0) return Fail("Le serveur ne repond pas correctement.");
        }
        catch (HubException e) { return Fail("Verification impossible avec la nouvelle cle : " + e.Message); }
        var refused = ProbeRefused(cfg);
        if (!refused) return Fail("ATTENTION : la cle accepte une commande interdite ; inscription annulee.");

        cfg.Save(r.StateDir);
        return new EnrollResult { Ok = true, Message = "Inscription terminee.", Config = cfg };
    }

    // La cle doit refuser toute commande autre que rsync / programme serveur.
    static bool ProbeRefused(AppConfig c)
    {
        var t = c.ToTarget(); t.RemoteCommand = "id";
        var (code, _, err) = Exec(t.ToStartInfo(), Array.Empty<byte>(), 30000);
        return code != 0 && err.Contains("refus");
    }
}
