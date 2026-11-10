// Claude-Sync V2 : reconnaissance et pilotage de Claude Desktop (et non d'un Claude Code lance dans un terminal). Reprise de la V1.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ClaudeSync.V2;

static class ClaudeDesktop
{
    // Identifiant de l'application Claude Desktop (paquet Windows) : sert a la relancer comme le fait le menu Demarrer.
    public const string Aumid = @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude";

    static bool IsDesktopProcess(Process p)
    {
        try
        {
            string path = p.MainModule!.FileName!;
            return path.IndexOf(@"WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf(@"Claude-Data\claude-code", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf(@"\Claude\claude-code\", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception) { return true; }     // chemin illisible : on considere que c'est Desktop
    }

    static List<Process> Processes()
    {
        var l = new List<Process>();
        foreach (var p in Process.GetProcessesByName("claude")) { if (IsDesktopProcess(p)) l.Add(p); else p.Dispose(); }
        return l;
    }

    public static bool Running() { var l = Processes(); bool r = l.Count > 0; foreach (var p in l) p.Dispose(); return r; }

    // La fenetre principale est-elle visible ? (fermee avec la croix = masquee, mais le programme reste actif)
    public static bool WindowVisible()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Processes()) { pids.Add((uint)p.Id); p.Dispose(); }
        if (pids.Count == 0) return false;
        bool found = false;
        Native.EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pids.Contains(pid) && Native.IsWindowVisible(h) && Native.GetWindowTextLength(h) > 0) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Fermeture normale d'abord (fenetre principale), arret force seulement si Claude ne repond pas.
    public static void Kill()
    {
        foreach (var p in Processes()) { try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch (Exception) { } p.Dispose(); }
        for (int i = 0; i < 12 && Running(); i++) Thread.Sleep(250);
        for (int pass = 0; pass < 3; pass++)
        {
            var l = Processes();
            if (l.Count == 0) return;
            foreach (var p in l) { try { p.Kill(); } catch (Exception) { } try { p.WaitForExit(3000); } catch (Exception) { } p.Dispose(); }
            Thread.Sleep(400);
        }
    }

    public static void Relaunch()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", Aumid) { UseShellExecute = false }); } catch (Exception) { }
    }
}
