// Claude-Sync V2 : types et petits outils communs (reprise des briques de la V1 dont la V2 a besoin, pour ne plus dependre du code V1).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeSync.V2
{
    // ---- FileEntry
    public sealed class FileEntry
    {
        public string Rel;
        public long Size;
        public long Ticks;
        public const long Tol = 20000000L; // 2 s
        public static bool Same(FileEntry a, FileEntry b)
        {
            if (a == null || b == null) return false;
            return a.Size == b.Size && Math.Abs(a.Ticks - b.Ticks) <= Tol;
        }
    }

    // ---- Jeux de dossiers, plan, conflits
    public sealed class SyncSet
    {
        public string Name;
        public string LocalRoot;
        public string HubRoot;
        public bool NewestWins; // metadonnees : le plus recent gagne (ancienne version archivee)
        public bool NoSubdirs;  // ne descend pas dans les sous-dossiers
        public Func<string, bool> FileOk;
        public HashSet<string> ExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
    public enum Act { Push, Pull, Conflict, Unstable, PullBlocked, Same, Ignored, TrashHub, TrashLocal, Forget, PendingDelete }
    public sealed class PlanItem
    {
        public SyncSet Set;
        public string Rel;
        public Act Kind;
        public FileEntry L, H, B;
        public string Note;
        public long Size { get { return Kind == Act.Push ? (L != null ? L.Size : 0) : (H != null ? H.Size : 0); } }
    }
    public sealed class ConflictRec
    {
        public string Set, Rel;
        public long LSize, LTicks, HSize, HTicks, Detected;
    }

    // ---- ProgressInfo
    public sealed class ProgressInfo
    {
        public string Phase = "";
        public string Current = "";
        public long TotalFiles, DoneFiles, TotalBytes, DoneBytes;
        public double BytesPerSec;
        public TimeSpan Eta;
        public DateTime Start = DateTime.UtcNow;
        public Dictionary<string, long[]> PerSet = new Dictionary<string, long[]>(); // set -> {fichiers faits, fichiers total}
    }

    // ---- Corbeille (entree)
    public class TrashEntry
    {
        public int Num; public string Kind, Title, Stamp, Pc, Sample; public DateTime Date; public int Files; public long Bytes;
        public List<string[]> Items = new List<string[]>(); // {chemin dans la corbeille, jeu, chemin relatif}
    }

    // ---- Acces fichiers locaux (chemins longs, analyse)
    public static class Fs
    {
        public static string Long(string p)
        {
            if (p.StartsWith(@"\\?\")) return p;
            p = p.Replace('/', '\\');             // le prefixe \\?\ desactive la normalisation de Windows : "C:/Projets" serait introuvable
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }
        public static string Show(string p)
        {
            if (p.StartsWith(@"\\?\UNC\")) return @"\\" + p.Substring(8);
            if (p.StartsWith(@"\\?\")) return p.Substring(4);
            return p;
        }
        public static bool FileExists(string p) { return File.Exists(Long(p)); }
        public static bool DirExists(string p) { return Directory.Exists(Long(p)); }
        public static void EnsureDir(string d) { Directory.CreateDirectory(Long(d)); }
        public static Dictionary<string, FileEntry> Scan(string root, SyncSet s, out int skippedLinks, out int errors)
        {
            Dictionary<string, FileEntry> res = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            int links = 0, errs = 0;
            if (DirExists(root))
            {
                string r = root.TrimEnd('\\');
                ScanDir(new DirectoryInfo(Long(r)), Long(r).Length + 1, s, res, ref links, ref errs);
            }
            skippedLinks = links; errors = errs;
            return res;
        }
        public static bool AcceptFile(string name, SyncSet s)
        {
            if (name.IndexOf(".syncpart-", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.StartsWith("~$") || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
            return s.FileOk == null || s.FileOk(name);
        }
        static void ScanDir(DirectoryInfo di, int rootLen, SyncSet s, Dictionary<string, FileEntry> res, ref int links, ref int errs)
        {
            try
            {
                foreach (FileInfo fi in di.EnumerateFiles())
                {
                    string name = fi.Name;
                    if (!AcceptFile(name, s)) continue;
                    string rel = fi.FullName.Substring(rootLen);
                    res[rel] = new FileEntry { Rel = rel, Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks };
                }
                if (s.NoSubdirs) return;
                foreach (DirectoryInfo sub in di.EnumerateDirectories())
                {
                    if (s.ExcludedDirs.Contains(sub.Name)) continue;
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) { links++; continue; }
                    ScanDir(sub, rootLen, s, res, ref links, ref errs);
                }
            }
            catch (Exception) { errs++; }
        }
        public static string Hash(string path)
        {
            using (FileStream fs = new FileStream(Long(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            }
        }
    }

    // ---- Journal
    public static class Log
    {
        static string dir; static readonly object lk = new object();
        public static Action<string> Sink;
        public static void Init(string d) { dir = d; try { Fs.EnsureDir(d); } catch { } }
        public static void W(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
            lock (lk)
            {
                Console.WriteLine(msg);
                if (Sink != null) Sink(msg);
                try { if (dir != null) File.AppendAllText(Path.Combine(dir, "log-" + DateTime.Now.ToString("yyyyMMdd") + ".txt"), line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }
    }

    // ---- Dossier des sessions de Claude Desktop
    public static class ClaudePaths
    {
        public static string FindIndexDir(string profile)
        {
            string[] cands = new string[] {
                Path.Combine(profile, @"AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\claude-code-sessions"),
                Path.Combine(profile, @"AppData\Roaming\Claude\claude-code-sessions") };
            foreach (string c in cands)
            {
                if (Directory.Exists(Fs.Long(c)))
                {
                    try { if (Directory.EnumerateFiles(Fs.Long(c), "local_*.json", SearchOption.AllDirectories).Any()) return c; } catch { }
                }
            }
            return cands[0];
        }
    }

    // ---- Raccourcis Windows (COM)
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLinkCoClass { }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl); void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey); void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd); void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFileW
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    // ---- Interface : Native, UiState, Theme, IconArt
    static class Native
    {
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("kernel32.dll")] public static extern bool FreeConsole();
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
        public static void AttachIfNoStdout()
        {
            try
            {
                IntPtr h = GetStdHandle(-11);
                if (h == IntPtr.Zero || h == new IntPtr(-1))
                {
                    if (AttachConsole(-1)) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                }
            }
            catch { }
        }
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool DestroyIcon(IntPtr h);
    }
    public enum UiState { Idle, Working, Offline, Conflict, Error, Blocked, Waiting }
    static class Theme
    {
        public static bool Dark = true;
        public static Color Bg, Card, Text, Subtle, Track, Accent, Accent2, Ok, Warn, Bad, Line;
        public static void Load()
        {
            try { object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0); Dark = !(v is int && (int)v == 1); } catch { Dark = true; }
            if (Dark)
            {
                Bg = Color.FromArgb(27, 29, 34); Card = Color.FromArgb(37, 40, 48); Text = Color.FromArgb(236, 238, 242); Subtle = Color.FromArgb(150, 157, 170);
                Track = Color.FromArgb(52, 56, 66); Line = Color.FromArgb(48, 52, 62);
            }
            else
            {
                Bg = Color.FromArgb(245, 246, 249); Card = Color.FromArgb(255, 255, 255); Text = Color.FromArgb(28, 30, 36); Subtle = Color.FromArgb(105, 112, 126);
                Track = Color.FromArgb(222, 226, 234); Line = Color.FromArgb(226, 229, 236);
            }
            Accent = Color.FromArgb(79, 140, 255); Accent2 = Color.FromArgb(56, 217, 195);
            Ok = Color.FromArgb(52, 211, 153); Warn = Color.FromArgb(245, 165, 36); Bad = Color.FromArgb(248, 113, 113);
        }
        public static FontFamily Family(params string[] names)
        {
            foreach (string n in names) foreach (FontFamily f in FontFamily.Families) if (f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return f;
            return FontFamily.GenericSansSerif;
        }
    }
    public static class IconArt
    {
        static readonly float[] Rays = new float[] { 0.33f, 0.22f, 0.29f, 0.20f, 0.34f, 0.23f, 0.28f, 0.21f, 0.32f, 0.22f, 0.30f, 0.20f };
        public static Color StateRing(UiState st)
        {
            switch (st) { case UiState.Working: return Color.FromArgb(79, 140, 255); case UiState.Idle: return Color.FromArgb(52, 211, 153); case UiState.Conflict: return Color.FromArgb(245, 165, 36); case UiState.Error: return Color.FromArgb(248, 113, 113); default: return Color.FromArgb(140, 146, 158); }
        }
        public static Bitmap Render(int size, bool tile, UiState st, float angle)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
                float S = size, c = S / 2f;
                Color spark, ring;
                if (tile)
                {
                    RectangleF r = new RectangleF(S * 0.03f, S * 0.03f, S * 0.94f, S * 0.94f);
                    using (GraphicsPath gp = RoundPath(r, S * 0.24f))
                    using (LinearGradientBrush lb = new LinearGradientBrush(r, Color.FromArgb(232, 132, 99), Color.FromArgb(193, 88, 58), 70f)) g.FillPath(lb, gp);
                    spark = Color.White; ring = Color.FromArgb(240, 255, 255, 255);
                }
                else { spark = Color.FromArgb(217, 119, 87); ring = StateRing(st); }
                // etincelle : 12 rayons arrondis, longueurs irregulieres
                float w = Math.Max(1.6f, S * (tile ? 0.062f : 0.085f));
                float scale = tile ? 0.80f : 0.88f;
                using (Pen pen = new Pen(spark, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    for (int i = 0; i < 12; i++)
                    {
                        double a = (i * 30.0 + 12) * Math.PI / 180.0;
                        float r1 = S * 0.045f, r2 = S * Rays[i] * scale;
                        g.DrawLine(pen, c + (float)Math.Cos(a) * r1, c + (float)Math.Sin(a) * r1, c + (float)Math.Cos(a) * r2, c + (float)Math.Sin(a) * r2);
                    }
                }
                // anneau de synchronisation : deux arcs et deux pointes de fleche
                float R = S * (tile ? 0.405f : 0.44f), rw = Math.Max(1.5f, S * (tile ? 0.052f : 0.085f));
                float a0 = (tile ? 200f : 200f) + angle, sweep = 118f;
                using (Pen pen = new Pen(ring, rw) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                using (SolidBrush hb = new SolidBrush(ring))
                {
                    RectangleF rr = new RectangleF(c - R, c - R, 2 * R, 2 * R);
                    for (int k = 0; k < 2; k++)
                    {
                        float st0 = a0 + k * 180f, en = st0 + sweep;
                        g.DrawArc(pen, rr, st0, sweep);
                        double ea = en * Math.PI / 180.0;
                        float ex = c + (float)Math.Cos(ea) * R, ey = c + (float)Math.Sin(ea) * R;
                        float tx = (float)-Math.Sin(ea), ty = (float)Math.Cos(ea), nx = (float)Math.Cos(ea), ny = (float)Math.Sin(ea);
                        float head = Math.Max(3f, S * (tile ? 0.15f : 0.2f));
                        PointF[] tri = new PointF[] { new PointF(ex + tx * head * 0.75f, ey + ty * head * 0.75f), new PointF(ex + nx * head * 0.62f, ey + ny * head * 0.62f), new PointF(ex - nx * head * 0.62f, ey - ny * head * 0.62f) };
                        g.FillPolygon(hb, tri);
                    }
                }
            }
            return bmp;
        }
        static GraphicsPath RoundPath(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        // icone systeme depuis un rendu (la poignee GDI est liberee)
        public static Icon ToIcon(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            try { using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); } finally { Native.DestroyIcon(h); }
        }
        // fichier .ico multi-tailles (images PNG) pour l'executable et les raccourcis
        public static void WriteIco(string path)
        {
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int sz in sizes) using (Bitmap b = Render(sz, true, UiState.Idle, 0)) using (MemoryStream ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); pngs.Add(ms.ToArray()); }
            using (FileStream fs = new FileStream(path, FileMode.Create)) using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int off = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(off); off += pngs[i].Length;
                }
                foreach (byte[] b in pngs) w.Write(b);
            }
        }
    }

    // ---- CardPanel
    sealed class CardPanel : Panel
    {
        public CardPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1); float rad = 12, d = rad * 2;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
                using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                using (Pen pen = new Pen(Theme.Line, 1)) g.DrawPath(pen, p);
            }
        }
    }

}
