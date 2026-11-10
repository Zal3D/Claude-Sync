// Claude-Sync V2 : installation / mise a jour / desinstallation (sans droits administrateur, dans le profil de l'utilisateur).
// Livraison : un dossier (zip) contenant ClaudeSync2.exe, outils\ (rsync, ssh) et serveur\ (programme serveur). Aucun fichier n'est cache dans l'exe
// (un exe qui en embarque d'autres ressemble a un "dropper" : Windows Defender l'a classe Trojan:Win32/Bearfoos.A!ml, comme avec la V1).
// Installer2 ne touche que : le dossier d'installation, le raccourci du menu, une entree "Applications installees" et, SEULEMENT si l'utilisateur le
// demande (case des Options, ou --startup), un raccourci de demarrage de Windows. Jamais la configuration, la cle ni les donnees synchronisees
// (dossier d'etat %LOCALAPPDATA%\ClaudeSync2), sauf demande explicite a la desinstallation.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ClaudeSync.V2;

// Ou installer : tout est regroupe ici pour que les tests puissent travailler dans un dossier d'essai sans toucher au vrai profil.
public sealed class InstallTarget
{
    public string Dir = "", StartupLink = "", MenuLink = "", RegKey = "";
    public string ExePath => Path.Combine(Dir, Installer2.ExeName);

    public static InstallTarget Default()
    {
        string la = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        return new InstallTarget
        {
            Dir = Path.Combine(la, "Programs", Installer2.ProductDir),
            StartupLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Installer2.LinkName),
            MenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Installer2.LinkName),
            RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Installer2.RegName,
        };
    }

    // Bac a sable : programme et raccourcis dans le dossier donne, entree de registre dans une branche d'essai.
    public static InstallTarget Sandbox(string root)
    {
        string full = Path.GetFullPath(root);
        string tag = BitConverter.ToString(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant())), 0, 6).Replace("-", "");
        return new InstallTarget
        {
            Dir = Path.Combine(full, "programme"), StartupLink = Path.Combine(full, "demarrage", Installer2.LinkName), MenuLink = Path.Combine(full, "menu", Installer2.LinkName),
            RegKey = @"Software\ClaudeSync2Test\" + tag,
        };
    }
}

public sealed class InstallOptions
{
    public bool Startup;                         // raccourci de demarrage de Windows : JAMAIS par defaut (choix explicite de l'utilisateur)
    public bool Launch;                          // lancer Claude-Sync a la fin
    public string? SourceExe;                    // exe a copier (defaut : le programme en cours)
    public Func<Stream>? Payload;                // paquet zip des outils (essais ; sinon : dossiers a cote du programme)
    public string? PayloadSha256;                // empreinte attendue du paquet fourni
    public string? PayloadDir;                   // dossier contenant outils\ et serveur\ (defaut : le dossier du programme)
}

public sealed class InstallResult
{
    public bool Ok; public string Message = "";
    public int Written, Unchanged, Removed;
}

public static class Installer2
{
    // Noms propres a la V2 : distincts de ceux de la V1 pour que les deux puissent coexister pendant la bascule (a unifier a la migration).
    public const string ProductDir = "Claude-Sync2", ExeName = "ClaudeSync2.exe", LinkName = "Claude-Sync 2.lnk", RegName = "ClaudeSync2";
    const string PayloadName = "payload.zip", PayloadHashName = "payload.sha256";
    static readonly string[] PayloadTops = { "outils", "serveur" };

    public static string SelfPath => Assembly.GetExecutingAssembly().Location;

    // ---------- paquet : dossiers a cote du programme (livraison normale) ou ressource integree (anciennes versions / essais)
    public static string? SiblingPayloadDir(string exe)
    {
        string? d = Path.GetDirectoryName(exe);
        if (d == null) return null;
        return File.Exists(Path.Combine(d, "outils", "win-x64", "rsync.exe")) && File.Exists(Path.Combine(d, "serveur", "claudesync-serveur")) ? d : null;
    }
    public static bool HasEmbeddedPayload => Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName) != null;
    public static bool HasPayload => HasEmbeddedPayload || SiblingPayloadDir(SelfPath) != null;
    static Stream EmbeddedPayload() => Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName)!;
    static string? EmbeddedHash()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadHashName);
        if (s == null) return null;
        using var r = new StreamReader(s); return r.ReadToEnd().Trim();
    }
    public static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

    public static bool IsInstalledCopy(InstallTarget t)
    {
        string? d = Path.GetDirectoryName(SelfPath);
        return d != null && string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(t.Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    public static string InstalledVersion(InstallTarget t)
    {
        try { using var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(t.RegKey); return rk == null ? "" : (rk.GetValue("DisplayVersion") ?? "").ToString() ?? ""; }
        catch (Exception) { return ""; }
    }

    // ---------- arret de l'ancienne version : d'abord DOUCEMENT (le programme se ferme seul), de force seulement s'il ne repond pas
    // Evenement nomme propre a chaque dossier d'installation : l'application residente l'ecoute (voir ListenForQuit).
    public static string QuitEventName(string dir) =>
        "Local\\ClaudeSync2-quit-" + BitConverter.ToString(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant())), 0, 6).Replace("-", "");

    // L'application demande a etre prevenue : renvoie un thread qui appelle onQuit quand l'installeur le demande.
    public static Thread ListenForQuit(string dir, Action onQuit)
    {
        var th = new Thread(() =>
        {
            try { using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName(dir)); if (ev.WaitOne()) onQuit(); }
            catch (Exception) { }
        }) { IsBackground = true, Name = "claude-sync-quit" };
        th.Start();
        return th;
    }

    static readonly string[] OwnProcesses = { "ClaudeSync2", "rsync", "ssh", "ssh-keygen" };
    static List<Process> RunningIn(string dir)
    {
        string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        int me = Process.GetCurrentProcess().Id; var l = new List<Process>();
        foreach (string name in OwnProcesses)
            foreach (Process p in Process.GetProcessesByName(name))
            {
                string? f = null; try { f = p.MainModule?.FileName; } catch (Exception) { }
                if (p.Id != me && f != null && f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) l.Add(p); else p.Dispose();
            }
        return l;
    }

    static void StopRunning(string dir)
    {
        var running = RunningIn(dir);
        if (running.Count == 0) return;
        try { using var ev = EventWaitHandle.OpenExisting(QuitEventName(dir)); ev.Set(); } catch (Exception) { }     // demande de fermeture : l'application se ferme seule
        var until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until && running.Any(p => { try { return !p.HasExited; } catch (Exception) { return false; } })) Thread.Sleep(200);
        foreach (var p in running)
        {
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(4000); } } catch (Exception) { }
            p.Dispose();
        }
    }

    static void MakeLink(string lnk, string target, string args, int windowStyle, string desc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        IShellLinkW l = (IShellLinkW)new ShellLinkCoClass();
        l.SetPath(target); l.SetArguments(args); l.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        l.SetShowCmd(windowStyle); l.SetIconLocation(target, 0); l.SetDescription(desc);
        ((IPersistFileW)l).Save(lnk, true);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(l);
    }

    // Demarrage avec Windows : choix de l'utilisateur (case des Options). Ne fait rien si le programme n'est pas installe a l'endroit prevu.
    public static bool StartupEnabled(InstallTarget t) => File.Exists(t.StartupLink);
    public static void SetStartup(InstallTarget t, bool on)
    {
        if (on) MakeLink(t.StartupLink, t.ExePath, "run --hidden", 7, "Claude-Sync");
        else { try { if (File.Exists(t.StartupLink)) File.Delete(t.StartupLink); } catch (Exception) { } }
    }

    // ---------- fichiers a installer (nom relatif avec "/", contenu)
    sealed class PayloadFile { public string Name = ""; public byte[] Data = new byte[0]; }

    static List<PayloadFile> LoadPayload(InstallOptions o, string srcExe, out string? error)
    {
        error = null; var list = new List<PayloadFile>();
        Func<Stream>? pf = o.Payload;
        string? expected = o.PayloadSha256;
        string? dir = o.PayloadDir;
        if (pf == null && dir == null) dir = SiblingPayloadDir(srcExe);
        if (pf == null && dir == null && HasEmbeddedPayload) { pf = EmbeddedPayload; expected = EmbeddedHash(); }
        if (pf != null)
        {
            byte[] zip; using (var s = pf()) using (var ms = new MemoryStream()) { s.CopyTo(ms); zip = ms.ToArray(); }
            if (expected != null && !Hex(SHA256.Create().ComputeHash(zip)).Equals(expected, StringComparison.OrdinalIgnoreCase)) { error = Loc.S("setup.corrupt"); return list; }
            using var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (var e in za.Entries)
            {
                if (e.FullName.EndsWith("/")) continue;
                using var es = e.Open(); using var ms = new MemoryStream(); es.CopyTo(ms);
                list.Add(new PayloadFile { Name = e.FullName, Data = ms.ToArray() });
            }
            return list;
        }
        if (dir != null)
        {
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            foreach (string top in PayloadTops)
            {
                string td = Path.Combine(root, top);
                if (!Directory.Exists(td)) continue;
                foreach (string f in Directory.GetFiles(td, "*", SearchOption.AllDirectories))
                    list.Add(new PayloadFile { Name = Path.GetFullPath(f).Substring(root.Length).Replace('\\', '/'), Data = File.ReadAllBytes(f) });
            }
            if (list.Count > 0) return list;
        }
        error = Loc.S("setup.nopayload");
        return list;
    }

    // ---------- installation
    public static InstallResult Install(InstallTarget t, InstallOptions o, Action<string, int>? progress = null)
    {
        Action<string, int> P = progress ?? delegate { };
        var res = new InstallResult();
        try
        {
            string src = o.SourceExe ?? SelfPath;
            var files = LoadPayload(o, src, out string? perr);
            if (perr != null) { res.Message = perr; return res; }
            if (!File.Exists(src)) { res.Message = Loc.S("setup.error", src); return res; }

            P(Loc.S("setup.p.stop"), 8); StopRunning(t.Dir); Thread.Sleep(200);
            P(Loc.S("setup.p.copy"), 22); Directory.CreateDirectory(t.Dir);
            string dest = t.ExePath;
            if (!Path.GetFullPath(src).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) { ReplaceFile(dest, File.ReadAllBytes(src)); res.Written++; }

            P(Loc.S("setup.p.tools"), 40);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string rootDir = Path.GetFullPath(t.Dir).TrimEnd('\\') + "\\";
            int n = 0, total = files.Count;
            foreach (var e in files)
            {
                string full = Path.GetFullPath(Path.Combine(t.Dir, e.Name.Replace('/', '\\')));
                if (!full.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase)) throw new IOException("Chemin refuse dans le paquet : " + e.Name);
                keep.Add(full); tops.Add(full.Substring(rootDir.Length).Split('\\')[0]);
                if (File.Exists(full) && new FileInfo(full).Length == e.Data.Length && SameBytes(File.ReadAllBytes(full), e.Data)) res.Unchanged++;
                else { Directory.CreateDirectory(Path.GetDirectoryName(full)!); ReplaceFile(full, e.Data); res.Written++; }
                if (++n % 4 == 0) P(Loc.S("setup.p.tools"), 40 + 30 * n / Math.Max(1, total));
            }
            // fichiers d'une ancienne version qui ne sont plus dans le paquet
            foreach (string top in tops)
            {
                string td = Path.Combine(t.Dir, top);
                if (!Directory.Exists(td)) continue;
                foreach (string f in Directory.GetFiles(td, "*", SearchOption.AllDirectories))
                    if (!keep.Contains(Path.GetFullPath(f))) { try { File.Delete(f); res.Removed++; } catch (Exception) { } }
            }

            P(Loc.S("setup.p.links"), 75);
            if (o.Startup) SetStartup(t, true);              // jamais sans demande explicite ; une reinstallation ne retire pas le choix deja fait
            MakeLink(t.MenuLink, dest, "run", 1, "Claude-Sync");

            P(Loc.S("setup.p.reg"), 88);
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(t.RegKey)!)
            {
                k.SetValue("DisplayName", "Claude-Sync 2"); k.SetValue("DisplayVersion", V2Info.Version); k.SetValue("Publisher", "Romain NOZAL");
                k.SetValue("InstallLocation", t.Dir); k.SetValue("DisplayIcon", dest + ",0");
                k.SetValue("UninstallString", "\"" + dest + "\" uninstall");
                k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord); k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 30000, Microsoft.Win32.RegistryValueKind.DWord);
            }

            if (o.Launch) { P(Loc.S("setup.p.launch"), 95); Launch(t); }
            P(Loc.S("setup.p.done"), 100);
            res.Ok = true; res.Message = Loc.S("setup.done");
        }
        catch (Exception e) { res.Ok = false; res.Message = Loc.S("setup.error", e.Message); P(res.Message, -1); }
        return res;
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // premier lancement (pas encore connecte a un serveur) : la fenetre s'ouvre pour guider la connexion ; ensuite, discret
    public static void Launch(InstallTarget t)
    {
        bool configured = AppConfig.Load(AppConfig.DefaultStateDir) != null;
        Process.Start(new ProcessStartInfo(t.ExePath, configured ? "run --hidden" : "run") { UseShellExecute = false, WorkingDirectory = t.Dir });
    }

    // ecrit a cote puis remplace : jamais de fichier a moitie ecrit
    static void ReplaceFile(string dest, byte[] data)
    {
        string tmp = dest + ".cs-new";
        File.WriteAllBytes(tmp, data);
        try { if (File.Exists(dest)) File.Delete(dest); File.Move(tmp, dest); }
        catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } throw; }
    }

    // ---------- desinstallation
    // Retire raccourcis, entree Windows et dossier du programme. La configuration/cle/etat (dossier d'etat) ne sont supprimes que sur demande.
    public static InstallResult Uninstall(InstallTarget t, bool purgeState, string? stateDir = null)
    {
        var res = new InstallResult { Ok = true };
        try
        {
            StopRunning(t.Dir);
            foreach (string l in new[] { t.StartupLink, t.MenuLink }) { try { if (File.Exists(l)) File.Delete(l); } catch (Exception) { } }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(t.RegKey, false); } catch (Exception) { }
            if (purgeState) { try { string sd = stateDir ?? AppConfig.DefaultStateDir; if (Directory.Exists(sd)) Directory.Delete(sd, true); } catch (Exception) { } }
            if (Directory.Exists(t.Dir))
            {
                if (IsInstalledCopy(t))
                {
                    // on ne peut pas effacer le programme en cours : une copie temporaire s'en charge (comme la V1 : pas de commande systeme externe)
                    string tmp = Path.Combine(Path.GetTempPath(), "ClaudeSync2-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");
                    File.Copy(SelfPath, tmp, true);
                    Process.Start(new ProcessStartInfo(tmp, "uninstall-cleanup \"" + t.Dir + "\"") { UseShellExecute = false, CreateNoWindow = true });
                }
                else Directory.Delete(t.Dir, true);
            }
            res.Message = Loc.S("setup.uninstalled");
        }
        catch (Exception e) { res.Ok = false; res.Message = Loc.S("setup.error", e.Message); }
        return res;
    }

    // Lance par la copie temporaire : attend la fin du programme puis efface le dossier. (La copie temporaire reste dans %TEMP%, nettoyee par Windows.)
    public static int Cleanup(string dir)
    {
        bool done = false;
        for (int i = 0; i < 12 && !done; i++) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); done = true; } catch (Exception) { Thread.Sleep(700); } }
        return done ? 0 : 1;
    }
}
