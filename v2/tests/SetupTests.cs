// Claude-Sync V2 : tests de l'installation (J4d). Tout se passe dans un dossier d'essai (InstallTarget.Sandbox) : jamais le vrai profil,
// jamais les vrais raccourcis ni la vraie entree "Applications installees". Un test final installe puis desinstalle le VRAI ClaudeSync2.exe construit.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class SetupTests
{
    static byte[] Zip(params (string name, string content)[] files)
    {
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, content) in files)
            {
                var e = za.CreateEntry(name);
                using var s = e.Open(); byte[] b = Encoding.UTF8.GetBytes(content); s.Write(b, 0, b.Length);
            }
        return ms.ToArray();
    }

    static InstallOptions Opt(byte[] zip, string exe, bool startup = true, bool badHash = false) => new InstallOptions
    {
        SourceExe = exe, Payload = () => new MemoryStream(zip), PayloadSha256 = badHash ? new string('0', 64) : Installer2.Hex(SHA256.Create().ComputeHash(zip)), Startup = startup,
    };

    static string RegValue(InstallTarget t, string name)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(t.RegKey);
        return (k?.GetValue(name) ?? "").ToString() ?? "";
    }

    public static void Run()
    {
        Loc.Set("fr");
        string root = Path.Combine(Env.Tmp, "setup"); Directory.CreateDirectory(root);
        string exe = Path.Combine(root, "source.exe"); File.WriteAllText(exe, "faux programme v1");
        byte[] zip1 = Zip(("serveur/claudesync-serveur", "srv1"), ("serveur/claudesync-entree", "ent1"), ("outils/win-x64/rsync.exe", "rsync1"), ("outils/win-x64/ancien.dll", "vieux"), ("outils/paquets.json", "{}"));

        T.Run("T150 installation : programme, serveur, outils, raccourcis, entree Windows", delegate
        {
            var t = InstallTarget.Sandbox(Path.Combine(root, "a"));
            var progress = new List<int>();
            var r = Installer2.Install(t, Opt(zip1, exe), (m, p) => progress.Add(p));
            T.Check("installation reussie", r.Ok, r.Message);
            T.Check("programme copie", File.Exists(t.ExePath) && File.ReadAllText(t.ExePath) == "faux programme v1");
            T.Check("serveur et outils extraits aux bons endroits", File.ReadAllText(Path.Combine(t.Dir, "serveur", "claudesync-serveur")) == "srv1" && File.ReadAllText(Path.Combine(t.Dir, "outils", "win-x64", "rsync.exe")) == "rsync1");
            T.Check("raccourcis menu et demarrage crees", File.Exists(t.MenuLink) && File.Exists(t.StartupLink));
            T.Check("entree Windows : version et desinstallation", RegValue(t, "DisplayVersion") == V2Info.Version && RegValue(t, "UninstallString").Contains("uninstall") && Installer2.InstalledVersion(t) == V2Info.Version);
            T.Check("progression croissante jusqu'a 100", progress.Count > 3 && progress.Last() == 100 && progress.SequenceEqual(progress.OrderBy(x => x)));
            T.Check("aucun fichier temporaire laisse", !Directory.GetFiles(t.Dir, "*.cs-new", SearchOption.AllDirectories).Any());
            Installer2.Uninstall(t, false);
        });

        T.Run("T151 reinstallation identique : rien n'est reecrit sauf le programme", delegate
        {
            var t = InstallTarget.Sandbox(Path.Combine(root, "b"));
            Installer2.Install(t, Opt(zip1, exe));
            var r = Installer2.Install(t, Opt(zip1, exe));
            T.Check("reussie", r.Ok, r.Message);
            T.Check("outils inchanges (non reecrits)", r.Unchanged == 5 && r.Written == 1 && r.Removed == 0, "ecrits " + r.Written + ", inchanges " + r.Unchanged);
            Installer2.Uninstall(t, false);
        });

        T.Run("T152 mise a jour : fichier modifie remplace, fichier disparu du paquet retire", delegate
        {
            var t = InstallTarget.Sandbox(Path.Combine(root, "c"));
            Installer2.Install(t, Opt(zip1, exe));
            byte[] zip2 = Zip(("serveur/claudesync-serveur", "srv2"), ("serveur/claudesync-entree", "ent1"), ("outils/win-x64/rsync.exe", "rsync1"), ("outils/paquets.json", "{}"), ("outils/win-x64/nouveau.dll", "neuf"));
            File.WriteAllText(exe, "faux programme v2");
            var r = Installer2.Install(t, Opt(zip2, exe));
            T.Check("reussie", r.Ok, r.Message);
            T.Check("serveur mis a jour", File.ReadAllText(Path.Combine(t.Dir, "serveur", "claudesync-serveur")) == "srv2");
            T.Check("programme mis a jour", File.ReadAllText(t.ExePath) == "faux programme v2");
            T.Check("ancien fichier retire, nouveau present", !File.Exists(Path.Combine(t.Dir, "outils", "win-x64", "ancien.dll")) && File.Exists(Path.Combine(t.Dir, "outils", "win-x64", "nouveau.dll")) && r.Removed == 1, "retires " + r.Removed);
            Installer2.Uninstall(t, false);
        });

        T.Run("T153 paquet abime ou dangereux : refuse, rien n'est installe", delegate
        {
            var t = InstallTarget.Sandbox(Path.Combine(root, "d"));
            var r = Installer2.Install(t, Opt(zip1, exe, badHash: true));
            T.Check("empreinte fausse : refuse", !r.Ok && r.Message.Length > 0, r.Message);
            T.Check("dossier non cree, pas d'entree Windows", !Directory.Exists(t.Dir) && Installer2.InstalledVersion(t) == "");
            byte[] evil = Zip(("serveur/ok", "x"), ("../evil.txt", "pirate"));
            var r2 = Installer2.Install(t, Opt(evil, exe));
            T.Check("chemin qui sort du dossier : refuse", !r2.Ok, r2.Message);
            T.Check("rien n'a ete ecrit hors du dossier", !File.Exists(Path.Combine(t.Dir, "..", "evil.txt")) && !File.Exists(Path.Combine(root, "d", "evil.txt")));
            var r3 = Installer2.Install(t, new InstallOptions { SourceExe = exe, Payload = null });
            T.Check("sans paquet (version de developpement) : message clair", !r3.Ok && r3.Message.Length > 0, r3.Message);
            if (Directory.Exists(t.Dir)) Installer2.Uninstall(t, false);
        });

        T.Run("T154 demarrage avec Windows : JAMAIS par defaut, choix de l'utilisateur (case des Options), conserve par une reinstallation", delegate
        {
            var t = InstallTarget.Sandbox(Path.Combine(root, "e"));
            Installer2.Install(t, new InstallOptions { SourceExe = exe, Payload = () => new MemoryStream(zip1), PayloadSha256 = Installer2.Hex(SHA256.Create().ComputeHash(zip1)) });
            T.Check("installation par defaut : AUCUN raccourci de demarrage (seulement celui du menu)", !File.Exists(t.StartupLink) && File.Exists(t.MenuLink) && !Installer2.StartupEnabled(t));
            Installer2.SetStartup(t, true);
            T.Check("l'utilisateur l'active : raccourci cree", File.Exists(t.StartupLink) && Installer2.StartupEnabled(t));
            Installer2.Install(t, Opt(zip1, exe, startup: false));
            T.Check("une reinstallation ne retire pas son choix", File.Exists(t.StartupLink));
            Installer2.SetStartup(t, false);
            T.Check("il le desactive : retire, le raccourci du menu reste", !File.Exists(t.StartupLink) && File.Exists(t.MenuLink));
            Installer2.Uninstall(t, false);
        });

        T.Run("T158 livraison en dossier : outils et serveur a cote de l'exe (rien d'embarque dans l'exe)", delegate
        {
            string pkg = Path.Combine(root, "livraison"); Directory.CreateDirectory(Path.Combine(pkg, "outils", "win-x64")); Directory.CreateDirectory(Path.Combine(pkg, "serveur"));
            File.WriteAllText(Path.Combine(pkg, "ClaudeSync2.exe"), "faux exe dossier");
            File.WriteAllText(Path.Combine(pkg, "outils", "win-x64", "rsync.exe"), "rsyncD"); File.WriteAllText(Path.Combine(pkg, "outils", "win-x64", "ssh.exe"), "sshD");
            File.WriteAllText(Path.Combine(pkg, "serveur", "claudesync-serveur"), "srvD"); File.WriteAllText(Path.Combine(pkg, "serveur", "claudesync-entree"), "entD");
            var t = InstallTarget.Sandbox(Path.Combine(root, "h"));
            T.Check("le dossier est reconnu comme paquet", Installer2.SiblingPayloadDir(Path.Combine(pkg, "ClaudeSync2.exe")) == pkg && Installer2.SiblingPayloadDir(exe) == null);
            var r = Installer2.Install(t, new InstallOptions { SourceExe = Path.Combine(pkg, "ClaudeSync2.exe") });
            T.Check("installation depuis le dossier reussie", r.Ok, r.Message);
            T.Check("programme, outils et serveur copies", File.ReadAllText(t.ExePath) == "faux exe dossier" && File.ReadAllText(Path.Combine(t.Dir, "outils", "win-x64", "rsync.exe")) == "rsyncD" && File.ReadAllText(Path.Combine(t.Dir, "serveur", "claudesync-entree")) == "entD");
            var r2 = Installer2.Install(t, new InstallOptions { SourceExe = Path.Combine(pkg, "ClaudeSync2.exe") });
            T.Check("reinstallation : rien de reecrit sauf l'exe", r2.Ok && r2.Written == 1 && r2.Unchanged == 4, r2.Written + " ecrits, " + r2.Unchanged + " inchanges");
            File.Delete(Path.Combine(t.Dir, "outils", "win-x64", "rsync.exe")); File.WriteAllText(Path.Combine(t.Dir, "outils", "ancien.txt"), "x");
            var r3 = Installer2.Install(t, new InstallOptions { SourceExe = Path.Combine(pkg, "ClaudeSync2.exe") });
            T.Check("fichier manquant retabli, fichier orphelin retire", r3.Ok && File.Exists(Path.Combine(t.Dir, "outils", "win-x64", "rsync.exe")) && !File.Exists(Path.Combine(t.Dir, "outils", "ancien.txt")));
            Installer2.Uninstall(t, false);
        });

        T.Run("T159 arret DOUX de l'ancienne version : l'application est prevenue et se ferme seule (pas d'arret force)", delegate
        {
            string dir = Path.Combine(root, "doux"); Directory.CreateDirectory(dir);
            var got = new ManualResetEventSlim();
            var th = Installer2.ListenForQuit(dir, () => got.Set());
            Thread.Sleep(300);
            T.Check("le nom de l'evenement depend du dossier d'installation", Installer2.QuitEventName(dir) != Installer2.QuitEventName(Path.Combine(root, "autre")));
            using (var ev = EventWaitHandle.OpenExisting(Installer2.QuitEventName(dir))) ev.Set();
            T.Check("l'application recoit la demande de fermeture", got.Wait(3000));
            T.Check("un autre dossier d'installation n'est pas concerne", !EventWaitHandle.TryOpenExisting(Installer2.QuitEventName(Path.Combine(root, "autre")), out _));
        });

        T.Run("T155 desinstallation : tout retire, etat conserve par defaut, supprime sur demande", delegate
        {
            var t = InstallTarget.Sandbox(Path.Combine(root, "f"));
            string state = Path.Combine(root, "f-etat"); Directory.CreateDirectory(state); File.WriteAllText(Path.Combine(state, "config.json"), "{}");
            Installer2.Install(t, Opt(zip1, exe));
            var r = Installer2.Uninstall(t, false, state);
            T.Check("reussie", r.Ok, r.Message);
            T.Check("dossier, raccourcis et entree Windows retires", !Directory.Exists(t.Dir) && !File.Exists(t.MenuLink) && !File.Exists(t.StartupLink) && Installer2.InstalledVersion(t) == "");
            T.Check("configuration conservee", File.Exists(Path.Combine(state, "config.json")));
            Installer2.Install(t, Opt(zip1, exe));
            Installer2.Uninstall(t, true, state);
            T.Check("avec --purge-state : configuration supprimee", !Directory.Exists(state));
            T.Check("desinstaller deux fois ne plante pas", Installer2.Uninstall(t, false).Ok);
        });

        T.Run("T156 textes d'installation : francais et anglais complets", delegate
        {
            var keys = Loc.Keys.Where(k => k.StartsWith("setup.")).ToList();
            T.Check("une vingtaine de textes", keys.Count >= 20, keys.Count.ToString());
            T.Check("chacun a ses deux langues, non vides", keys.All(k => Loc.Fr(k).Length > 0 && Loc.En(k).Length > 0));
        });

        string app = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "v2app", "ClaudeSync2.exe"));
        if (!File.Exists(app)) { Console.WriteLine("(test de l'installeur reel ignore : dist\\v2app\\ClaudeSync2.exe absent)"); return; }
        T.Run("T157 le VRAI ClaudeSync2.exe : installe en bac a sable, fonctionne depuis la, se desinstalle", delegate
        {
            string sb = Path.Combine(root, "reel");
            var t = InstallTarget.Sandbox(sb);
            var (c1, o1) = Exec(app, "install --silent --lang fr --sandbox \"" + sb + "\"");
            T.Check("install --silent : code 0", c1 == 0, o1);
            T.Check("programme installe", File.Exists(t.ExePath));
            string srcTools = Path.Combine(Path.GetDirectoryName(app)!, "outils", "win-x64");
            bool same = true;
            foreach (string f in Directory.GetFiles(srcTools))
            {
                string d = Path.Combine(t.Dir, "outils", "win-x64", Path.GetFileName(f));
                if (!File.Exists(d) || Sha(d) != Sha(f)) { same = false; break; }
            }
            T.Check("tous les outils (rsync, ssh...) installes, identiques aux originaux (SHA-256)", same);
            T.Check("serveur installe, identique", Sha(Path.Combine(t.Dir, "serveur", "claudesync-serveur")) == Sha(Path.Combine(Path.GetDirectoryName(app)!, "serveur", "claudesync-serveur")));
            T.Check("version enregistree", Installer2.InstalledVersion(t) == V2Info.Version);
            var (c2, o2) = Exec(t.ExePath, "install --silent --lang fr --sandbox \"" + sb + "\"");
            T.Check("reinstallation par-dessus (le programme installe se reinstalle lui-meme) : OK", c2 == 0, o2);
            var (c3, o3) = Exec(t.ExePath, "help");
            T.Check("le programme installe repond (aide)", c3 == 0 && o3.Contains("install"), o3);
            var (c4, o4) = Exec(t.ExePath, "uninstall --silent --lang fr --sandbox \"" + sb + "\"");
            T.Check("uninstall --silent depuis la copie installee : code 0", c4 == 0, o4);
            bool gone = false;
            for (int i = 0; i < 40 && !gone; i++) { if (!Directory.Exists(t.Dir)) gone = true; else Thread.Sleep(500); }
            T.Check("dossier efface par la copie temporaire", gone);
            T.Check("entree Windows et raccourcis retires", Installer2.InstalledVersion(t) == "" && !File.Exists(t.MenuLink));
        });
    }

    static string Sha(string f) => Installer2.Hex(SHA256.Create().ComputeHash(File.ReadAllBytes(f)));

    static (int code, string output) Exec(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(120000);
        return (p.ExitCode, o);
    }
}
