// Claude-Sync V2 : outils de dessin communs (style de la V1 : coins arrondis, barres degradees, polices Segoe UI Variable).
#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace ClaudeSync.V2;

static class Gfx
{
    public static FontFamily Fam => Theme.Family("Segoe UI Variable Text", "Segoe UI");
    public static FontFamily FamDisplay => Theme.Family("Segoe UI Variable Display", "Segoe UI Semilight", "Segoe UI");

    public static float Dpi() { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        if (rad <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }

    public static void Card(Graphics g, RectangleF r)
    {
        using (var p = Round(r, 12)) using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
        using (var p = Round(r, 12)) using (var pen = new Pen(Theme.Line, 1)) g.DrawPath(pen, p);
    }

    public static void Bar(Graphics g, RectangleF r, double f, Color c1, Color c2)
    {
        float rad = r.Height / 2;
        using (var p = Round(r, rad)) using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
        f = Math.Max(0, Math.Min(1, f));
        if (f <= 0.001) return;
        float w = Math.Max(r.Height, (float)(r.Width * f));
        var fr = new RectangleF(r.X, r.Y, w, r.Height);
        using (var p = Round(fr, rad)) using (var lb = new LinearGradientBrush(fr, c1, c2, LinearGradientMode.Horizontal)) g.FillPath(lb, p);
    }

    public static void Txt(Graphics g, string s, FontFamily f, float size, FontStyle st, Color c, RectangleF r, StringAlignment h, StringAlignment v, bool ellipsisPath = false)
    {
        using (var font = new Font(f, size, st, GraphicsUnit.Pixel)) using (var b = new SolidBrush(c))
        using (var sf = new StringFormat(StringFormatFlags.NoWrap))
        {
            sf.Alignment = h; sf.LineAlignment = v; sf.Trimming = ellipsisPath ? StringTrimming.EllipsisPath : StringTrimming.EllipsisCharacter;
            g.DrawString(s, font, b, r, sf);
        }
    }

    public static void TxtFit(Graphics g, string s, FontFamily f, float size, Color c, RectangleF r)
    {
        float z = size;
        while (z > 12)
        {
            using (var font = new Font(f, z, FontStyle.Regular, GraphicsUnit.Pixel)) { if (g.MeasureString(s, font).Width <= r.Width) break; }
            z -= 1;
        }
        Txt(g, s, f, z, FontStyle.Regular, c, r, StringAlignment.Near, StringAlignment.Center);
    }

    public static string Bytes(long b)
    {
        var ci = CultureInfo.CurrentCulture;
        if (b >= 1073741824L) return (b / 1073741824.0).ToString("0.0", ci) + " " + Loc.S("unit.gb");
        if (b >= 1048576L) return (b / 1048576.0).ToString("0.0", ci) + " " + Loc.S("unit.mb");
        if (b >= 1024L) return (b / 1024.0).ToString("0", ci) + " " + Loc.S("unit.kb");
        return b + " " + Loc.S("unit.b");
    }

    public static string Eta(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return Loc.S("eta.few");
        if (t.TotalHours >= 1) return ((int)t.TotalHours) + " " + Loc.S("unit.h") + " " + t.Minutes.ToString("00") + " " + Loc.S("unit.min");
        if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes) + " " + Loc.S("unit.min") + " " + t.Seconds.ToString("00") + " " + Loc.S("unit.s");
        return t.Seconds + " " + Loc.S("unit.s");
    }

    public static string Num(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static Color StateColor(UiState s) => s switch
    {
        UiState.Working => Theme.Accent, UiState.Idle => Theme.Ok, UiState.Conflict => Theme.Warn,
        UiState.Error => Theme.Bad, UiState.Blocked => Theme.Warn, _ => Theme.Subtle
    };

    public static string StateLabel(UiState s) => s switch
    {
        UiState.Working => Loc.S("state.working"), UiState.Idle => Loc.S("state.idle"), UiState.Conflict => Loc.S("state.conflict"),
        UiState.Error => Loc.S("state.error"), UiState.Offline => Loc.S("state.offline"), UiState.Blocked => Loc.S("state.blocked"),
        _ => Loc.S("state.waiting")
    };

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R * (1 - t) + b.R * t), (int)(a.G * (1 - t) + b.G * t), (int)(a.B * (1 - t) + b.B * t));

    public static void Quality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }
}
