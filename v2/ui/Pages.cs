// Claude-Sync V2 : les pages de la fenetre unique (Accueil, Conflits, Corbeille, Options, Connexion au serveur, A propos).
// Meme style que la V1 ; plus de fenetres qui s'empilent : la navigation se fait par le menu a trois barres et le bouton Retour.
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClaudeSync.V2;

public enum PageId { Home, Conflicts, Trash, Options, Server, About }

abstract class PageBase : Panel
{
    protected readonly AppModel M;
    protected float Sc = Gfx.Dpi();
    public event Action<PageId>? GoTo;
    protected void Go(PageId p) => GoTo?.Invoke(p);

    protected PageBase(AppModel m) { M = m; Dock = DockStyle.Fill; BackColor = Theme.Bg; DoubleBuffered = true; }

    protected int D(int v) => (int)Math.Round(v * Sc);
    protected Font F(float px, FontStyle st = FontStyle.Regular) => new Font(Gfx.Fam, px * Sc, st, GraphicsUnit.Pixel);
    protected Font FD(float px) => new Font(Gfx.FamDisplay, px * Sc, FontStyle.Regular, GraphicsUnit.Pixel);

    protected Label Lbl(string text, Font f, Color c, Color back, Rectangle r, ContentAlignment al = ContentAlignment.MiddleLeft) =>
        new Label { Text = text, Font = f, ForeColor = c, BackColor = back, Left = r.X, Top = r.Y, Width = r.Width, Height = r.Height, TextAlign = al, AutoEllipsis = true, UseMnemonic = false };

    protected Pill Btn(string text, bool primary, Rectangle r, Color back) =>
        new Pill(text, primary) { Font = F(13), Left = r.X, Top = r.Y, Width = r.Width, Height = r.Height, BackColor = back };

    public abstract void Build();
    public virtual void ModelChanged() { }

    protected void Clear() { foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); } }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Build(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated && ClientSize.Width > 0) Build(); }

    protected void Ui(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (Exception) { } }

    public static string When(DateTime d) => d.ToString("d MMM yyyy  HH:mm", CultureInfo.CurrentCulture);
}

// ------------------------------------------------------------------------------------------------ ACCUEIL
sealed class HomeSurface : Control
{
    readonly AppModel m;
    readonly FontFamily fam = Gfx.Fam, famDisp = Gfx.FamDisplay;
    public event Action? OpenConflicts, OpenTrash, ButtonClicked;
    static readonly string[] Names = { "conversations", "historique", "projets", "sessions" };
    bool hoverBtn;
    float Sc => Gfx.Dpi();

    // Bouton principal dessine dans la surface (evite toute superposition de controles) : "Synchroniser maintenant" ou "Se connecter".
    RectangleF ButtonRect(float W, float H) => m.Configured ? new RectangleF(W - 24 - 170, 222, 170, 28) : new RectangleF(W / 2 - 130, H / 2 + 10, 260, 38);
    bool OverButton(Point pt)
    {
        float s = Sc; var r = ButtonRect(Width / s, Height / s);
        return (!m.Configured || !m.Busy) && r.Contains(pt.X / s, pt.Y / s);
    }

    public HomeSurface(AppModel model)
    {
        m = model;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    int HitFooter(Point pt)
    {
        float sc = Gfx.Dpi(); float x = pt.X / sc, y = pt.Y / sc, H = Height / sc, W = Width / sc;
        if (y < H - 46 || y > H - 14) return 0;
        return x < W / 2 ? 1 : 2;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hb = OverButton(e.Location);
        if (hb != hoverBtn) { hoverBtn = hb; Invalidate(); }
        Cursor = hb || HitFooter(e.Location) != 0 ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hoverBtn) { hoverBtn = false; Invalidate(); } }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (OverButton(e.Location)) { ButtonClicked?.Invoke(); return; }
        int h = HitFooter(e.Location);
        if (h == 1) OpenConflicts?.Invoke(); else if (h == 2) OpenTrash?.Invoke();
    }

    void Pill(Graphics g, RectangleF r, string text, bool primary, bool enabled)
    {
        using (var p = Gfx.Round(r, r.Height / 2))
        {
            Color fill = !enabled ? Theme.Track : primary ? (hoverBtn ? Color.FromArgb(100, 156, 255) : Theme.Accent) : (hoverBtn ? Theme.Track : Theme.Card);
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (!primary || !enabled) using (var pen = new Pen(Theme.Line, 1)) g.DrawPath(pen, p);
        }
        Gfx.Txt(g, text, fam, 13, FontStyle.Regular, !enabled ? Theme.Subtle : primary ? Color.White : Theme.Text, r, StringAlignment.Center, StringAlignment.Center);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; Gfx.Quality(g);
        float s = g.DpiX / 96f; g.ScaleTransform(s, s);
        float W = Width / s, H = Height / s;
        g.Clear(Theme.Bg);
        float mg = 24, x = mg, w = W - 2 * mg;
        bool working = m.State == UiState.Working; var P = m.P;

        if (!m.Configured)
        {
            Gfx.Txt(g, Loc.S("home.notconfigured"), fam, 15, FontStyle.Regular, Theme.Subtle, new RectangleF(x, H / 2 - 70, w, 60), StringAlignment.Center, StringAlignment.Center);
            Pill(g, ButtonRect(W, H), Loc.S("opt.connect"), true, true);
            return;
        }
        double fraction = P.TotalBytes > 0 ? (double)P.DoneBytes / P.TotalBytes : (P.TotalFiles > 0 ? (double)P.DoneFiles / P.TotalFiles : 0);
        string pct = working ? ((int)(fraction * 100)) + "%" : (m.State == UiState.Idle ? "OK" : "-");
        Gfx.Txt(g, pct, famDisp, 56, FontStyle.Regular, Theme.Text, new RectangleF(x - 2, 14, 220, 70), StringAlignment.Near, StringAlignment.Center);
        Gfx.Txt(g, m.PhaseOverride ?? m.Phase, fam, 15, FontStyle.Regular, Theme.Text, new RectangleF(x + 190, 24, w - 190, 26), StringAlignment.Far, StringAlignment.Center);
        string sub = working ? (P.Eta.TotalSeconds > 0 ? Loc.S("home.eta", Gfx.Eta(P.Eta)) : Loc.S("home.calc")) : m.Detail;
        Gfx.Txt(g, sub, fam, 13, FontStyle.Regular, Theme.Subtle, new RectangleF(x + 190, 50, w - 190, 22), StringAlignment.Far, StringAlignment.Center);

        Gfx.Bar(g, new RectangleF(x, 94, w, 10), working ? fraction : (m.State == UiState.Idle ? 1 : 0), Theme.Accent, Theme.Accent2);

        float cw = (w - 24) / 3f, cy = 124, ch = 84;
        string[] lab = { Loc.S("home.files"), Loc.S("home.data"), working ? Loc.S("home.speed") : Loc.S("home.avgspeed") };
        bool has = working || P.TotalFiles > 0;
        string[] val = { has ? Gfx.Num(P.DoneFiles) + " / " + Gfx.Num(P.TotalFiles) : "-", working || P.TotalBytes > 0 ? Gfx.Bytes(P.DoneBytes) : "-", P.BytesPerSec > 0 ? Gfx.Bytes((long)P.BytesPerSec) + "/" + Loc.S("unit.s") : "-" };
        string[] sb2 = { has ? Loc.S("home.left", Gfx.Num(Math.Max(0, P.TotalFiles - P.DoneFiles))) : "", working || P.TotalBytes > 0 ? Loc.S("home.outof", Gfx.Bytes(P.TotalBytes)) : "", working && P.Eta.TotalSeconds > 0 ? Loc.S("home.endsin", Gfx.Eta(P.Eta)) : "" };
        for (int i = 0; i < 3; i++)
        {
            var cr = new RectangleF(x + i * (cw + 12), cy, cw, ch);
            Gfx.Card(g, cr);
            Gfx.Txt(g, lab[i], fam, 12, FontStyle.Regular, Theme.Subtle, new RectangleF(cr.X + 14, cr.Y + 10, cr.Width - 20, 18), StringAlignment.Near, StringAlignment.Center);
            Gfx.TxtFit(g, val[i], famDisp, 22, Theme.Text, new RectangleF(cr.X + 14, cr.Y + 28, cr.Width - 20, 30));
            Gfx.Txt(g, sb2[i], fam, 12, FontStyle.Regular, Theme.Subtle, new RectangleF(cr.X + 14, cr.Y + 58, cr.Width - 20, 18), StringAlignment.Near, StringAlignment.Center);
        }

        string cur = working ? Loc.S("home.current", P.Current) : (m.Last.HasValue ? Loc.S("home.last", m.Last.Value.ToString("dd/MM HH:mm")) : Loc.S("home.never"));
        Gfx.Txt(g, cur, fam, 12, FontStyle.Regular, Theme.Subtle, new RectangleF(x, 226, w - 190, 20), StringAlignment.Near, StringAlignment.Center, working);
        Pill(g, ButtonRect(W, H), Loc.S("home.syncnow"), false, !m.Busy);

        float y = 262;
        Gfx.Txt(g, Loc.S("home.detail"), fam, 12, FontStyle.Bold, Theme.Subtle, new RectangleF(x, y, 200, 18), StringAlignment.Near, StringAlignment.Center);
        y += 26;
        foreach (string n in Names)
        {
            P.PerSet.TryGetValue(n, out var r);
            bool none = r == null || r[1] == 0;
            double f = none ? 0 : (double)r![0] / r[1];
            string name = n == "projets" ? Loc.S("set.projets", m.Config?.ProjetsRoot.Replace('/', '\\') ?? "") : Loc.S("set." + n);
            Gfx.Txt(g, name, fam, 13, FontStyle.Regular, Theme.Text, new RectangleF(x, y, w - 150, 20), StringAlignment.Near, StringAlignment.Center);
            Gfx.Txt(g, none ? (working || m.State == UiState.Idle ? Loc.S("home.uptodate") : "") : Gfx.Num(r![0]) + " / " + Gfx.Num(r[1]), fam, 12, FontStyle.Regular, Theme.Subtle, new RectangleF(x + w - 140, y, 140, 20), StringAlignment.Far, StringAlignment.Center);
            bool ok = none && (working || m.State == UiState.Idle);
            Gfx.Bar(g, new RectangleF(x, y + 24, w, 6), none ? (ok ? 1 : 0) : f, ok ? Theme.Ok : Theme.Accent, ok ? Theme.Ok : Theme.Accent2);
            y += 46;
        }

        int cc = m.Conflicts.Count, tc = m.Trash?.Select(t => t.Stamp).Distinct().Count() ?? 0;
        Gfx.Txt(g, cc > 0 ? Loc.S("home.conflicts", cc) : Loc.S("home.noconflict"), fam, 12, FontStyle.Regular, cc > 0 ? Theme.Warn : Theme.Subtle, new RectangleF(x, H - 40, w / 2, 20), StringAlignment.Near, StringAlignment.Center);
        Gfx.Txt(g, Loc.S("home.trash", tc), fam, 12, FontStyle.Regular, Theme.Subtle, new RectangleF(x + w / 2, H - 40, w / 2, 20), StringAlignment.Far, StringAlignment.Center);
    }
}

sealed class HomePage : PageBase
{
    HomeSurface? surf;
    public HomePage(AppModel m) : base(m) { }

    public override void Build()
    {
        Clear();
        surf = new HomeSurface(M) { Dock = DockStyle.Fill };
        surf.OpenConflicts += () => Go(PageId.Conflicts);
        surf.OpenTrash += () => Go(PageId.Trash);
        surf.ButtonClicked += () => { if (M.Configured) M.SyncNow(); else Go(PageId.Server); };
        surf.Controls.Add(new Label { Text = Loc.S("about.version", V2Info.Version), Font = F(12), ForeColor = Theme.Subtle, BackColor = Color.Transparent, Dock = DockStyle.Bottom, Height = D(30), TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false });
        Controls.Add(surf);
    }

    public override void ModelChanged() => surf?.Invalidate();
}

// ------------------------------------------------------------------------------------------------ CONFLITS
sealed class ConflictsPage : PageBase
{
    string message = ""; bool busy;
    public ConflictsPage(AppModel m) : base(m) { }

    static string Fmt(long size, long ticks) => Gfx.Bytes(size) + " · " + new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    public override void Build()
    {
        Clear(); SuspendLayout();
        int mg = D(24), w = ClientSize.Width - 2 * mg;
        Controls.Add(Lbl(Loc.S("conf.intro"), F(13), Theme.Subtle, Theme.Bg, new Rectangle(mg, D(12), w, D(44)), ContentAlignment.TopLeft));
        var list = new Panel { Left = 0, Top = D(64), Width = ClientSize.Width, Height = ClientSize.Height - D(64), AutoScroll = true, BackColor = Theme.Bg };
        Controls.Add(list);
        int y = D(4), iw = list.ClientSize.Width - 2 * mg - SystemInformation.VerticalScrollBarWidth;
        if (message != "") { list.Controls.Add(Lbl(message, F(13), Theme.Accent, Theme.Bg, new Rectangle(mg, y, iw, D(24)))); y += D(32); }
        if (M.Conflicts.Count == 0)
            list.Controls.Add(Lbl(Loc.S("conf.none"), F(15), Theme.Subtle, Theme.Bg, new Rectangle(mg, y + D(40), iw, D(28)), ContentAlignment.MiddleCenter));
        for (int i = 0; i < M.Conflicts.Count; i++)
        {
            var c = M.Conflicts[i]; int idx = i + 1;
            var card = new CardPanel { Left = mg, Top = y, Width = iw, Height = D(150), BackColor = Theme.Card };
            int pad = D(18), cw = (iw - 2 * pad) / 2; Color cb = Theme.Card;
            card.Controls.Add(Lbl(c.Rel.Replace('/', '\\'), F(14, FontStyle.Bold), Theme.Text, cb, new Rectangle(pad, D(12), iw - 2 * pad, D(24))));
            card.Controls.Add(Lbl(Loc.S("conf.here"), F(12), Theme.Subtle, cb, new Rectangle(pad, D(40), cw, D(18))));
            card.Controls.Add(Lbl(Fmt(c.LSize, c.LTicks), F(13), Theme.Text, cb, new Rectangle(pad, D(58), cw, D(22))));
            card.Controls.Add(Lbl(Loc.S("conf.server"), F(12), Theme.Subtle, cb, new Rectangle(pad + cw, D(40), cw, D(18))));
            card.Controls.Add(Lbl(Fmt(c.HSize, c.HTicks), F(13), Theme.Text, cb, new Rectangle(pad + cw, D(58), cw, D(22))));
            string[] choices = { "local", "hub", "both" }; string[] texts = { Loc.S("conf.mine"), Loc.S("conf.theirs"), Loc.S("conf.both") };
            int bw = (iw - 2 * pad - 2 * D(8)) / 3;
            for (int k = 0; k < 3; k++)
            {
                var b = Btn(texts[k], k == 0, new Rectangle(pad + k * (bw + D(8)), D(100), bw, D(34)), cb);
                string ch = choices[k];
                b.Click += delegate { if (busy) return; busy = true; message = Loc.S("conf.working"); Build(); M.Resolve(idx, ch, msg => Ui(() => { busy = false; message = msg; Build(); })); };
                card.Controls.Add(b);
            }
            list.Controls.Add(card); y += D(164);
        }
        ResumeLayout();
    }
}

// ------------------------------------------------------------------------------------------------ CORBEILLE
sealed class TrashPage : PageBase
{
    readonly HashSet<string> expanded = new(); bool busy; object? lastList; string lastErr = "";
    public TrashPage(AppModel m) : base(m) { }

    public override void ModelChanged()
    {
        if (!ReferenceEquals(lastList, M.Trash) || lastErr != M.TrashError) Build();
    }

    static string Describe(TrashEntry t) =>
        t.Kind == "session" ? Loc.S("trash.session", t.Title) : t.Kind == "projet" ? Loc.S("trash.folder", t.Title) : t.Title;

    public override void Build()
    {
        if (M.Trash == null && !M.Demo) M.LoadTrash();
        lastList = M.Trash; lastErr = M.TrashError;
        Clear(); SuspendLayout();
        int mg = D(24), w = ClientSize.Width - 2 * mg;
        Controls.Add(Lbl(Loc.S("trash.intro"), F(13), Theme.Subtle, Theme.Bg, new Rectangle(mg, D(12), w, D(44)), ContentAlignment.TopLeft));
        var list = new Panel { Left = 0, Top = D(64), Width = ClientSize.Width, Height = ClientSize.Height - D(64), AutoScroll = true, BackColor = Theme.Bg };
        Controls.Add(list);
        int y = D(4), iw = list.ClientSize.Width - 2 * mg - SystemInformation.VerticalScrollBarWidth;
        if (M.TrashError != "") list.Controls.Add(Lbl(Loc.S("trash.error", M.TrashError), F(14), Theme.Subtle, Theme.Bg, new Rectangle(mg, y + D(30), iw, D(24)), ContentAlignment.MiddleCenter));
        else if (M.Trash == null) list.Controls.Add(Lbl(Loc.S("trash.loading"), F(14), Theme.Subtle, Theme.Bg, new Rectangle(mg, y + D(30), iw, D(24)), ContentAlignment.MiddleCenter));
        else if (M.Trash.Count == 0) list.Controls.Add(Lbl(Loc.S("trash.empty"), F(15), Theme.Subtle, Theme.Bg, new Rectangle(mg, y + D(40), iw, D(28)), ContentAlignment.MiddleCenter));
        else
        {
            foreach (string stamp in M.Trash.Select(t => t.Stamp).Distinct().ToList())
            {
                var g = M.Trash.Where(t => t.Stamp == stamp).ToList();
                bool open = expanded.Contains(stamp);
                int h = D(122) + (open ? g.Count * D(40) + D(8) : 0);
                var card = new CardPanel { Left = mg, Top = y, Width = iw, Height = h, BackColor = Theme.Card };
                int pad = D(18), cw = iw - 2 * pad; Color cb = Theme.Card;
                card.Controls.Add(Lbl(When(g[0].Date), F(15, FontStyle.Bold), Theme.Text, cb, new Rectangle(pad, D(14), cw / 2, D(24))));
                long files = g.Sum(t => (long)t.Files), bytes = g.Sum(t => t.Bytes);
                card.Controls.Add(Lbl((g.Count > 1 ? Loc.S("trash.items", g.Count) : Loc.S("trash.item", g.Count)) + "  ·  " + (files == 1 ? Loc.S("trash.file", 1) : Loc.S("trash.files", Gfx.Num(files))) + "  ·  " + Gfx.Bytes(bytes), F(12), Theme.Subtle, cb, new Rectangle(pad + cw / 2, D(14), cw / 2, D(24)), ContentAlignment.MiddleRight));
                string summary = string.Join("   ·   ", g.Select(Describe));
                card.Controls.Add(Lbl(summary + (string.IsNullOrEmpty(g[0].Pc) ? "" : "     " + Loc.S("trash.by", g[0].Pc)), F(13), Theme.Text, cb, new Rectangle(pad, D(42), cw, D(40)), ContentAlignment.TopLeft));
                var all = Btn(Loc.S("trash.all"), true, new Rectangle(pad, D(84), D(150), D(30)), cb);
                var det = Btn(open ? Loc.S("trash.hide") : Loc.S("trash.show"), false, new Rectangle(pad + D(160), D(84), D(170), D(30)), cb);
                string st = stamp; var items = g;
                all.Click += delegate { DoRestore(items, all); };
                det.Click += delegate { if (!expanded.Add(st)) expanded.Remove(st); Build(); };
                card.Controls.Add(all); card.Controls.Add(det);
                if (open)
                {
                    int yy = D(122);
                    foreach (var t in g)
                    {
                        card.Controls.Add(Lbl(Describe(t) + "  -  " + (t.Files == 1 ? Loc.S("trash.file", 1) : Loc.S("trash.files", Gfx.Num(t.Files))) + ", " + Gfx.Bytes(t.Bytes), F(12), Theme.Subtle, cb, new Rectangle(pad, yy, cw - D(130), D(30))));
                        var one = Btn(Loc.S("trash.one"), false, new Rectangle(iw - pad - D(110), yy, D(110), D(28)), cb);
                        var single = new List<TrashEntry> { t };
                        one.Click += delegate { DoRestore(single, one); };
                        card.Controls.Add(one); yy += D(40);
                    }
                }
                list.Controls.Add(card); y += h + D(14);
            }
        }
        ResumeLayout();
    }

    void DoRestore(List<TrashEntry> items, Pill b)
    {
        if (busy) return;
        busy = true; b.Text = Loc.S("trash.working"); b.Enabled = false;
        M.Restore(items, _ => Ui(() => { busy = false; M.Trash = null; Build(); }));
    }
}

// ------------------------------------------------------------------------------------------------ OPTIONS
sealed class OptionsPage : PageBase
{
    string linkText = "", lastUpd = "";
    public OptionsPage(AppModel m) : base(m) { }
    string UpdKey() => (M.Update?.Version ?? "") + "|" + M.UpdateText + "|" + M.UpdateBusy;
    public override void ModelChanged() { if (UpdKey() != lastUpd) Ui(Build); }        // reconstruit seulement quand l'etat des mises a jour change (pas a chaque progression de synchro)

    public override void Build()
    {
        Clear(); SuspendLayout();
        int mg = D(24), cw = ClientSize.Width - 2 * mg;
        var host = new Panel { Left = 0, Top = 0, Width = ClientSize.Width, Height = ClientSize.Height, AutoScroll = true, BackColor = Theme.Bg };
        Controls.Add(host);
        int iw = host.ClientSize.Width - 2 * mg - SystemInformation.VerticalScrollBarWidth, y = D(10), pad = D(18); Color cb = Theme.Card;
        var c = M.Config;

        // --- serveur
        host.Controls.Add(Lbl(Loc.S("opt.server"), F(13, FontStyle.Bold), Theme.Subtle, Theme.Bg, new Rectangle(mg, y, iw, D(20)))); y += D(26);
        var s1 = new CardPanel { Left = mg, Top = y, Width = iw, Height = D(128), BackColor = Theme.Card };
        s1.Controls.Add(Lbl(c == null ? Loc.S("opt.notconf") : c.Host + ":" + c.Port, F(15, FontStyle.Bold), Theme.Text, cb, new Rectangle(pad, D(14), iw - 2 * pad, D(24))));
        s1.Controls.Add(Lbl(c == null ? "" : c.User + "  ·  " + c.Folder, F(12), Theme.Subtle, cb, new Rectangle(pad, D(40), iw - 2 * pad, D(20))));
        var edit = Btn(c == null ? Loc.S("opt.connect") : Loc.S("opt.edit"), c == null, new Rectangle(pad, D(74), D(230), D(34)), cb);
        edit.Click += delegate { Go(PageId.Server); };
        s1.Controls.Add(edit);
        host.Controls.Add(s1); y += D(142);

        // --- liaison
        host.Controls.Add(Lbl(Loc.S("opt.link"), F(13, FontStyle.Bold), Theme.Subtle, Theme.Bg, new Rectangle(mg, y, iw, D(20)))); y += D(26);
        var s2 = new CardPanel { Left = mg, Top = y, Width = iw, Height = D(160), BackColor = Theme.Card };
        s2.Controls.Add(Lbl(Loc.S("opt.slow"), F(14), Theme.Text, cb, new Rectangle(pad, D(14), iw - 2 * pad - D(190), D(24))));
        int slow = c?.SlowMs ?? 120;
        var val = Lbl(slow + " ms", F(14, FontStyle.Bold), Theme.Text, cb, new Rectangle(iw - pad - D(130), D(14), D(70), D(24)), ContentAlignment.MiddleCenter);
        var minus = Btn("−", false, new Rectangle(iw - pad - D(170), D(12), D(36), D(28)), cb); var plus = Btn("+", false, new Rectangle(iw - pad - D(50), D(12), D(36), D(28)), cb);
        minus.Click += delegate { M.SetSlowThreshold((M.Config?.SlowMs ?? 120) - 20); val.Text = (M.Config?.SlowMs ?? 120) + " ms"; };
        plus.Click += delegate { M.SetSlowThreshold((M.Config?.SlowMs ?? 120) + 20); val.Text = (M.Config?.SlowMs ?? 120) + " ms"; };
        s2.Controls.Add(val); s2.Controls.Add(minus); s2.Controls.Add(plus);
        s2.Controls.Add(Lbl(Loc.S("opt.slowhelp", c?.LaunchWaitSeconds ?? 120), F(12), Theme.Subtle, cb, new Rectangle(pad, D(46), iw - 2 * pad, D(40)), ContentAlignment.TopLeft));
        var test = Btn(Loc.S("opt.test"), false, new Rectangle(pad, D(100), D(190), D(34)), cb); test.Enabled = c != null;
        var res = Lbl(linkText, F(13), Theme.Subtle, cb, new Rectangle(pad + D(204), D(100), iw - 2 * pad - D(204), D(34)));
        test.Click += delegate
        {
            test.Enabled = false; res.Text = Loc.S("opt.testing");
            M.TestLink(ms => Ui(() =>
            {
                linkText = ms < 0 ? "" : ms >= 9999 ? Loc.S("opt.linkdown") : (ms > (M.Config?.SlowMs ?? 120) ? Loc.S("opt.linkslow", ms) : Loc.S("opt.linkfast", ms));
                res.Text = linkText; test.Enabled = true;
            }));
        };
        s2.Controls.Add(test); s2.Controls.Add(res);
        host.Controls.Add(s2); y += D(174);

        // --- application
        host.Controls.Add(Lbl(Loc.S("opt.app"), F(13, FontStyle.Bold), Theme.Subtle, Theme.Bg, new Rectangle(mg, y, iw, D(20)))); y += D(26);
        var s3 = new CardPanel { Left = mg, Top = y, Width = iw, Height = D(300), BackColor = Theme.Card };
        s3.Controls.Add(Lbl(Loc.S("opt.lang"), F(14), Theme.Text, cb, new Rectangle(pad, D(16), D(120), D(24))));
        var fr = Btn("Français", Loc.Lang == "fr", new Rectangle(iw - pad - D(220), D(12), D(100), D(32)), cb);
        var en = Btn("English", Loc.Lang == "en", new Rectangle(iw - pad - D(110), D(12), D(100), D(32)), cb);
        fr.Click += delegate { M.SetLanguage("fr"); Go(PageId.Options); };
        en.Click += delegate { M.SetLanguage("en"); Go(PageId.Options); };
        s3.Controls.Add(fr); s3.Controls.Add(en);
        s3.Controls.Add(Lbl(Loc.S("opt.startup"), F(14), Theme.Text, cb, new Rectangle(pad, D(60), iw - 2 * pad - D(200), D(24))));
        // Demarrage avec Windows : choix de l'utilisateur, jamais impose par l'installeur
        var tg = InstallTarget.Default(); bool installedHere = Installer2.IsInstalledCopy(tg), startOn = installedHere && Installer2.StartupEnabled(tg);
        var st = Btn(Loc.S(startOn ? "opt.startup.on" : "opt.startup.off"), startOn, new Rectangle(iw - pad - D(150), D(56), D(140), D(32)), cb);
        st.Enabled = installedHere;
        st.Click += delegate { try { Installer2.SetStartup(tg, !startOn); } catch (Exception) { } Go(PageId.Options); };
        s3.Controls.Add(st);
        if (!installedHere) s3.Controls.Add(Lbl(Loc.S("opt.startup.managed"), F(12), Theme.Subtle, cb, new Rectangle(pad, D(84), iw - 2 * pad, D(18))));
        // Prise en charge de l'ouverture de Claude : choix de l'utilisateur (Claude est ferme puis relance le temps de synchroniser)
        bool to = M.BlockLaunch;
        s3.Controls.Add(Lbl(Loc.S("opt.takeover"), F(14), Theme.Text, cb, new Rectangle(pad, D(104), iw - 2 * pad - D(160), D(22))));
        s3.Controls.Add(Lbl(Loc.S("opt.takeover.sub"), F(12), Theme.Subtle, cb, new Rectangle(pad, D(126), iw - 2 * pad - D(160), D(34)), ContentAlignment.TopLeft));
        var tb = Btn(Loc.S(to ? "opt.startup.on" : "opt.startup.off"), to, new Rectangle(iw - pad - D(150), D(104), D(140), D(32)), cb);
        tb.Enabled = M.Configured; tb.Click += delegate { M.SetBlockLaunch(!to); Go(PageId.Options); };
        s3.Controls.Add(tb);
        // Mises a jour automatiques : jamais par defaut ; seulement quand l'application est au repos
        bool au = M.AutoUpdate;
        s3.Controls.Add(Lbl(Loc.S("opt.autoupdate"), F(14), Theme.Text, cb, new Rectangle(pad, D(166), iw - 2 * pad - D(160), D(22))));
        s3.Controls.Add(Lbl(Loc.S("opt.autoupdate.sub"), F(12), Theme.Subtle, cb, new Rectangle(pad, D(188), iw - 2 * pad - D(160), D(34)), ContentAlignment.TopLeft));
        var ab = Btn(Loc.S(au ? "opt.startup.on" : "opt.startup.off"), au, new Rectangle(iw - pad - D(150), D(166), D(140), D(32)), cb);
        ab.Enabled = M.Configured; ab.Click += delegate { M.SetAutoUpdate(!au); Go(PageId.Options); };
        s3.Controls.Add(ab);
        // mises a jour : meme comportement que la page A propos (verification a la demande, installation seulement sur clic)
        bool updOn = Updater2.SourceUrl(M.UpdateSource) != null; var up = M.Update; lastUpd = UpdKey();
        var upd = Btn(up != null ? Loc.S("update.install", up.Version) : Loc.S("opt.update"), up != null, new Rectangle(pad, D(228), D(280), D(34)), cb);
        upd.Enabled = updOn && !M.UpdateBusy;
        var updMsg = Lbl(!updOn ? Loc.S("update.off") : M.UpdateText, F(12), up != null ? Theme.Accent : Theme.Subtle, cb, new Rectangle(pad, D(266), iw - 2 * pad, D(30)), ContentAlignment.TopLeft);
        upd.Click += delegate { if (M.Update != null) M.InstallUpdate(); else M.CheckUpdate(true); };
        s3.Controls.Add(upd); s3.Controls.Add(updMsg);
        host.Controls.Add(s3);
        ResumeLayout();
    }
}

// ------------------------------------------------------------------------------------------------ CONNEXION AU SERVEUR (inscription)
sealed class ServerPage : PageBase
{
    TextBox? host, port, user, pass, folder, projects, pc; Label? status; Pill? go; bool running;
    public ServerPage(AppModel m) : base(m) { }

    TextBox Field(string label, string value, int y, int mg, int w, bool secret = false)
    {
        Controls.Add(Lbl(label, F(12), Theme.Subtle, Theme.Bg, new Rectangle(mg, y, w, D(18))));
        var t = new TextBox { Left = mg, Top = y + D(20), Width = w, Font = F(14), BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Card, ForeColor = Theme.Text, Text = value };
        if (secret) t.UseSystemPasswordChar = true;
        Controls.Add(t); return t;
    }

    public override void Build()
    {
        string h0 = host?.Text ?? M.Config?.Host ?? "", p0 = port?.Text ?? (M.Config?.Port ?? 22).ToString(), u0 = user?.Text ?? M.Config?.User ?? "";
        string f0 = folder?.Text ?? (M.Config != null ? M.Config.Folder : AppModel.DefaultFolder ?? "claude-sync"), pr0 = projects?.Text ?? M.Config?.ProjetsRoot.Replace('/', '\\') ?? AppModel.DefaultProjects ?? @"C:\Projets", n0 = pc?.Text ?? M.Config?.Pc ?? AppModel.DefaultPc ?? Environment.MachineName;
        string pw0 = pass?.Text ?? "";
        Clear(); SuspendLayout();
        int mg = D(24), w = ClientSize.Width - 2 * mg;
        Controls.Add(Lbl(Loc.S("srv.intro"), F(13), Theme.Subtle, Theme.Bg, new Rectangle(mg, D(12), w, D(44)), ContentAlignment.TopLeft));
        int y = D(66), half = (w - D(12)) / 2;
        host = Field(Loc.S("srv.host"), h0, y, mg, w - D(12) - D(110)); host.Width = w - D(12) - D(110);
        port = Field(Loc.S("srv.port"), p0, y, mg + w - D(110), D(110));
        y += D(58);
        user = Field(Loc.S("srv.user"), u0, y, mg, half);
        pass = Field(Loc.S("srv.password"), pw0, y, mg + half + D(12), half, true);
        y += D(58);
        folder = Field(Loc.S("srv.folder"), f0, y, mg, w); y += D(58);
        projects = Field(Loc.S("srv.projects"), pr0, y, mg, w); y += D(58);
        pc = Field(Loc.S("srv.pc"), n0, y, mg, w); y += D(62);
        go = Btn(Loc.S("srv.register"), true, new Rectangle(mg, y, D(200), D(38)), Theme.Bg);
        go.Enabled = !running;
        go.Click += delegate { Register(); };
        Controls.Add(go);
        status = Lbl("", F(13), Theme.Subtle, Theme.Bg, new Rectangle(mg + D(212), y, w - D(212), D(38)));
        Controls.Add(status);
        ResumeLayout();
    }

    static string StepKey(string s) => s switch
    {
        "Empreinte du serveur" => "srv.step.pin", "Controle du serveur" => "srv.step.check", "Depot du programme serveur" => "srv.step.deposit",
        "Dossier de donnees" => "srv.step.folder", "Cle de ce PC" => "srv.step.key", _ => "srv.step.verify"
    };

    void SetStatus(string t, Color? c = null) { if (status != null) { status.Text = t; status.ForeColor = c ?? Theme.Subtle; } }

    void Register()
    {
        if (running || host == null) return;
        if (host.Text.Trim().Length == 0 || user!.Text.Trim().Length == 0 || pass!.Text.Length == 0) { SetStatus(Loc.S("srv.missing"), Theme.Warn); return; }
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var files = new Dictionary<string, byte[]>();
        try { foreach (string n in new[] { "claudesync-serveur", "claudesync-entree" }) files[n] = File.ReadAllBytes(Path.Combine(baseDir, "serveur", n)); }
        catch (Exception ex) { SetStatus(ex.Message, Theme.Bad); return; }
        var req = new EnrollRequest
        {
            Host = host.Text.Trim(), Port = int.TryParse(port!.Text, out int pt) ? pt : 22, User = user.Text.Trim(), Password = pass.Text,
            Folder = folder!.Text.Trim().Length > 0 ? folder.Text.Trim() : "claude-sync", Pc = pc!.Text.Trim().Length > 0 ? pc.Text.Trim() : Environment.MachineName,
            ProjetsRoot = projects!.Text.Trim(), ToolsDir = M.Config?.ToolsDir is { Length: > 0 } t && File.Exists(Path.Combine(t, "ssh.exe")) ? t : Path.Combine(baseDir, "outils", "win-x64"),
            StateDir = M.StateDir, AskpassExe = Assembly.GetExecutingAssembly().Location, ServerFiles = files,
            Progress = s => Ui(() => SetStatus(Loc.S(StepKey(s)) + "...")),
            ConfirmFingerprint = fp => (bool)Invoke(new Func<bool>(() => MessageBox.Show(FindForm(), Loc.S("srv.fp.ask", fp), Loc.S("srv.fp.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes))
        };
        running = true; go!.Enabled = false; SetStatus(Loc.S("srv.working"));
        Task.Run(() =>
        {
            var r = Enroller.Run(req);
            string imported = r.Ok && r.Config != null ? M.ImportPreviousVersion(r.Config) : "";
            Ui(() =>
            {
                running = false;
                if (!r.Ok) { go!.Enabled = true; SetStatus(r.Message, Theme.Bad); return; }
                pass!.Text = ""; M.Reload(); SetStatus(imported.Length > 0 ? imported : Loc.S("srv.done"), Theme.Ok);
                M.SyncNow(); Go(PageId.Home);
            });
        });
    }
}

// ------------------------------------------------------------------------------------------------ A PROPOS
sealed class AboutPage : PageBase
{
    public AboutPage(AppModel m) : base(m) { }
    public override void ModelChanged() => Ui(Build);
    public override void Build()
    {
        Clear();
        int mg = D(24), w = ClientSize.Width - 2 * mg, pad = D(18); Color cb = Theme.Card;
        var card = new CardPanel { Left = mg, Top = D(14), Width = w, Height = D(300), BackColor = Theme.Card };
        card.Controls.Add(Lbl(Loc.S("app"), FD(28), Theme.Text, cb, new Rectangle(pad, D(14), w - 2 * pad, D(40))));
        card.Controls.Add(Lbl(Loc.S("about.version", V2Info.Version), F(13), Theme.Subtle, cb, new Rectangle(pad, D(56), w - 2 * pad, D(22))));
        card.Controls.Add(Lbl(Loc.S("about.text"), F(13), Theme.Text, cb, new Rectangle(pad, D(86), w - 2 * pad, D(44)), ContentAlignment.TopLeft));
        card.Controls.Add(Lbl(Loc.S("about.license"), F(12), Theme.Subtle, cb, new Rectangle(pad, D(134), w - 2 * pad, D(48)), ContentAlignment.TopLeft));
        if (M.Config != null) card.Controls.Add(Lbl(Loc.S("about.server", M.Config.User + "@" + M.Config.Host), F(12), Theme.Subtle, cb, new Rectangle(pad, D(182), w - 2 * pad, D(20))));
        // mises a jour : verification a la demande ; l'installation ne part que sur clic
        bool on = Updater2.SourceUrl(M.UpdateSource) != null;
        string st = !on ? Loc.S("update.off") : M.UpdateText;
        card.Controls.Add(Lbl(st, F(12), M.Update != null ? Theme.Accent : Theme.Subtle, cb, new Rectangle(pad, D(214), w - 2 * pad, D(40)), ContentAlignment.TopLeft));
        if (on)
        {
            var b = M.Update != null ? Btn(Loc.S("update.install", M.Update.Version), true, new Rectangle(pad, D(254), D(250), D(34)), cb) : Btn(Loc.S("update.check"), false, new Rectangle(pad, D(254), D(250), D(34)), cb);
            b.Enabled = !M.UpdateBusy;
            b.Click += (_, _) => { if (M.Update != null) M.InstallUpdate(); else M.CheckUpdate(true); };
            card.Controls.Add(b);
        }
        Controls.Add(card);
    }
}
