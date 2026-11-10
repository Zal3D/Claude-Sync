// Claude-Sync V2 : la fenetre unique. Barre du haut (menu a trois barres, titre, bouton Retour a droite), tiroir de navigation qui glisse
// sur un fond assombri, pages interchangeables. Remplace les fenetres separees (Corbeille, Conflits, Options) de la V1.
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeSync.V2;

static class ThemeX
{
    // Force le theme clair ou sombre (essais et captures) ; le theme normal suit Windows (Theme.Load).
    public static void Force(bool dark)
    {
        Theme.Load();
        if (Theme.Dark == dark) return;
        Theme.Dark = dark;
        if (dark) { Theme.Bg = Color.FromArgb(27, 29, 34); Theme.Card = Color.FromArgb(37, 40, 48); Theme.Text = Color.FromArgb(236, 238, 242); Theme.Subtle = Color.FromArgb(150, 157, 170); Theme.Track = Color.FromArgb(52, 56, 66); Theme.Line = Color.FromArgb(48, 52, 62); }
        else { Theme.Bg = Color.FromArgb(245, 246, 249); Theme.Card = Color.FromArgb(255, 255, 255); Theme.Text = Color.FromArgb(28, 30, 36); Theme.Subtle = Color.FromArgb(105, 112, 126); Theme.Track = Color.FromArgb(222, 226, 234); Theme.Line = Color.FromArgb(226, 229, 236); }
    }
}

// ------------------------------------------------------------------------------------------------ barre du haut
sealed class TopBar : Control
{
    public string Title = ""; public bool ShowBack; public bool ShowState; public UiState State = UiState.Waiting;
    public event Action? BurgerClick, BackClick;
    bool hoverBurger, hoverBack;
    float Sc => Gfx.Dpi();
    int D(float v) => (int)Math.Round(v * Sc);
    Rectangle BurgerRect => new(D(12), D(9), D(40), D(40));
    Rectangle BackRect => new(Width - D(16) - D(110), D(13), D(110), D(32));

    public TopBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool b = BurgerRect.Contains(e.Location), k = ShowBack && BackRect.Contains(e.Location);
        if (b != hoverBurger || k != hoverBack) { hoverBurger = b; hoverBack = k; Invalidate(); }
        Cursor = b || k ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverBurger = hoverBack = false; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); ClickAt(e.Location); }
    internal bool ClickAt(Point p)
    {
        if (BurgerRect.Contains(p)) { BurgerClick?.Invoke(); return true; }
        if (ShowBack && BackRect.Contains(p)) { BackClick?.Invoke(); return true; }
        return false;
    }
    internal Point BurgerCenter => new(BurgerRect.X + BurgerRect.Width / 2, BurgerRect.Y + BurgerRect.Height / 2);
    internal Point BackCenter => new(BackRect.X + BackRect.Width / 2, BackRect.Y + BackRect.Height / 2);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; Gfx.Quality(g);
        g.Clear(Theme.Bg);
        using (var pen = new Pen(Theme.Line, 1)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        var br = BurgerRect;
        if (hoverBurger) using (var p = Gfx.Round(br, D(8))) using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
        int bw = D(20), bh = Math.Max(2, D(2)), gap = D(6), bx = br.X + (br.Width - bw) / 2, by = br.Y + (br.Height - (3 * bh + 2 * gap)) / 2;
        for (int i = 0; i < 3; i++) using (var p = Gfx.Round(new RectangleF(bx, by + i * (bh + gap), bw, bh), bh / 2f)) using (var b = new SolidBrush(Theme.Text)) g.FillPath(b, p);
        Gfx.Txt(g, Title, Gfx.Fam, 18 * Sc, FontStyle.Bold, Theme.Text, new RectangleF(D(64), 0, Width - D(64) - D(140), Height - 1), StringAlignment.Near, StringAlignment.Center);
        if (ShowBack)
        {
            var r = BackRect; using (var p = Gfx.Round(r, r.Height / 2f))
            {
                using (var b = new SolidBrush(hoverBack ? Gfx.Mix(Theme.Card, Theme.Text, 0.09) : Theme.Card)) g.FillPath(b, p);
                using (var pen = new Pen(Gfx.Mix(Theme.Card, Theme.Text, hoverBack ? 0.42 : 0.30), 1f)) g.DrawPath(pen, p);
            }
            Gfx.Txt(g, "‹  " + Loc.S("nav.back"), Gfx.Fam, 13 * Sc, FontStyle.Regular, Theme.Text, r, StringAlignment.Center, StringAlignment.Center);
        }
        else if (ShowState)
        {
            Color c = Gfx.StateColor(State); var r = new RectangleF(Width - D(16) - D(112), D(16), D(112), D(26));
            using (var p = Gfx.Round(r, r.Height / 2f)) using (var b = new SolidBrush(Color.FromArgb(40, c))) g.FillPath(b, p);
            Gfx.Txt(g, Gfx.StateLabel(State), Gfx.Fam, 12 * Sc, FontStyle.Bold, c, r, StringAlignment.Center, StringAlignment.Center);
        }
    }
}

// ------------------------------------------------------------------------------------------------ tiroir de navigation
sealed class DrawerOverlay : Control
{
    public PageId Current = PageId.Home; public int ConflictCount, TrashCount;
    public event Action<PageId>? Choose; public event Action? QuitRequested;
    Bitmap? shot; int offset; int target; readonly Timer anim = new() { Interval = 12 };
    int hot = -1;
    float Sc => Gfx.Dpi();
    int D(float v) => (int)Math.Round(v * Sc);
    int DrawerW => D(250);
    public bool IsOpen => Visible && target == 0;

    record Item(PageId? Id, string Glyph, string Label, bool Quit = false, bool SepBefore = false);
    List<Item> Items() => new()
    {
        new(PageId.Home, "", Loc.S("nav.home")),
        new(PageId.Conflicts, "", Loc.S("nav.conflicts")),
        new(PageId.Trash, "", Loc.S("nav.trash")),
        new(PageId.Options, "", Loc.S("nav.options"), SepBefore: true),
        new(PageId.About, "", Loc.S("nav.about")),
        new(null, "", Loc.S("nav.quit"), Quit: true, SepBefore: true)
    };

    public DrawerOverlay()
    {
        Visible = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        anim.Tick += (_, _) =>
        {
            int diff = target - offset;
            if (Math.Abs(diff) <= D(3)) { offset = target; anim.Stop(); if (target < 0) { Visible = false; shot?.Dispose(); shot = null; } }
            else offset += (int)(diff * 0.38) + Math.Sign(diff);
            Invalidate();
        };
    }

    // snapshot : image de la fenetre sans le tiroir (sert de fond assombri)
    public void Open(Bitmap snapshot, bool instant = false)
    {
        shot?.Dispose(); shot = snapshot;
        offset = instant ? 0 : -DrawerW; target = 0; hot = -1;
        Visible = true; BringToFront(); Focus();
        if (instant) Invalidate(); else anim.Start();
    }

    public void Close(bool instant = false)
    {
        if (!Visible) return;
        target = -DrawerW;
        if (instant) { offset = target; Visible = false; shot?.Dispose(); shot = null; } else anim.Start();
    }

    Rectangle ItemRect(int i)
    {
        var items = Items(); int y = D(76);
        for (int k = 0; k <= i; k++)
        {
            if (items[k].Quit) { y = Height - D(8) - D(46); break; }        // "Quitter" est colle en bas
            if (k > 0 && items[k].SepBefore) y += D(18);                    // espace du separateur, compte une seule fois
            if (k == i) break;
            y += D(46);
        }
        return new Rectangle(offset, y, DrawerW, D(46));
    }

    int HitItem(Point p)
    {
        if (p.X > offset + DrawerW) return -2;           // en dehors du tiroir
        var items = Items();
        for (int i = 0; i < items.Count; i++) if (ItemRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); int h = HitItem(e.Location);
        if (h != hot) { hot = h; Invalidate(); }
        Cursor = h >= 0 || h == -2 ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); ClickAt(e.Location); }
    internal int ItemCount => Items().Count;
    internal Point ItemCenter(int i) { var r = ItemRect(i); return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }
    internal void ClickAt(Point p)
    {
        int h = HitItem(p);
        if (h == -2) { Close(); return; }
        if (h < 0) return;
        var it = Items()[h];
        if (it.Quit) { Close(true); QuitRequested?.Invoke(); } else if (it.Id != null) Choose?.Invoke(it.Id.Value);
    }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Escape) Close(); }
    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Escape || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; Gfx.Quality(g);
        if (shot != null) g.DrawImage(shot, 0, 0, Width, Height); else g.Clear(Theme.Bg);
        double open = Math.Max(0, Math.Min(1, 1 + (double)offset / DrawerW));
        using (var b = new SolidBrush(Color.FromArgb((int)(140 * open), 0, 0, 0))) g.FillRectangle(b, 0, 0, Width, Height);
        var dr = new Rectangle(offset, 0, DrawerW, Height);
        using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, dr);
        using (var pen = new Pen(Theme.Line, 1)) g.DrawLine(pen, dr.Right - 1, 0, dr.Right - 1, Height);
        // en-tete : pastille + nom + version
        try { using (var logo = IconArt.Render(D(28), true, UiState.Idle, 0)) g.DrawImage(logo, offset + D(16), D(20), D(28), D(28)); } catch (Exception) { }
        Gfx.Txt(g, Loc.S("app"), Gfx.Fam, 16 * Sc, FontStyle.Bold, Theme.Text, new RectangleF(offset + D(54), D(18), D(130), D(32)), StringAlignment.Near, StringAlignment.Center);
        Gfx.Txt(g, "v" + V2Info.Version, Gfx.Fam, 11 * Sc, FontStyle.Regular, Theme.Subtle, new RectangleF(offset + D(150), D(18), D(92), D(32)), StringAlignment.Far, StringAlignment.Center);
        using (var pen = new Pen(Theme.Line, 1)) g.DrawLine(pen, offset, D(64), offset + DrawerW, D(64));
        var items = Items();
        using (var icons = new Font(Theme.Family("Segoe Fluent Icons", "Segoe MDL2 Assets"), 18 * Sc, FontStyle.Regular, GraphicsUnit.Pixel))
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i]; var r = ItemRect(i);
                bool active = it.Id == Current || (it.Id == PageId.Options && Current == PageId.Server);
                if (it.SepBefore && !it.Quit) using (var pen = new Pen(Theme.Line, 1)) g.DrawLine(pen, offset + D(12), r.Y - D(9), offset + DrawerW - D(12), r.Y - D(9));
                if (it.Quit) using (var pen = new Pen(Theme.Line, 1)) g.DrawLine(pen, offset, r.Y - D(5), offset + DrawerW, r.Y - D(5));
                if (active) { using (var b = new SolidBrush(Theme.Track)) g.FillRectangle(b, r); using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, r.Y, D(3), r.Height); }
                else if (hot == i) using (var b = new SolidBrush(Theme.Track)) g.FillRectangle(b, r);
                using (var b = new SolidBrush(active ? Theme.Accent : Theme.Subtle)) g.DrawString(it.Glyph, icons, b, new RectangleF(r.X + D(16), r.Y, D(32), r.Height), new StringFormat { LineAlignment = StringAlignment.Center });
                int n = it.Id == PageId.Conflicts ? ConflictCount : it.Id == PageId.Trash ? TrashCount : 0;
                Gfx.Txt(g, it.Label, Gfx.Fam, 14 * Sc, FontStyle.Regular, Theme.Text, new RectangleF(r.X + D(54), r.Y, DrawerW - D(54) - (n > 0 ? D(66) : D(14)), r.Height), StringAlignment.Near, StringAlignment.Center);
                if (n > 0)
                {
                    var br = new RectangleF(r.Right - D(52), r.Y + (r.Height - D(22)) / 2f, D(36), D(22)); Color bc = it.Id == PageId.Conflicts ? Theme.Warn : Theme.Track;
                    using (var p = Gfx.Round(br, br.Height / 2)) using (var b = new SolidBrush(bc)) g.FillPath(b, p);
                    Gfx.Txt(g, n > 99 ? "99+" : n.ToString(), Gfx.Fam, 12 * Sc, FontStyle.Bold, it.Id == PageId.Conflicts ? Color.FromArgb(58, 38, 0) : Theme.Text, br, StringAlignment.Center, StringAlignment.Center);
                }
            }
    }
}

// ------------------------------------------------------------------------------------------------ la fenetre
sealed class MainWindow : Form
{
    readonly AppModel M;
    readonly TopBar bar = new();
    readonly Panel host = new();
    readonly DrawerOverlay drawer = new();
    PageBase? page;
    public PageId Current { get; private set; } = PageId.Home;
    public bool HideOnClose;
    public event Action? QuitRequested;

    public MainWindow(AppModel model)
    {
        M = model;
        float sc = Gfx.Dpi();
        Text = Loc.S("app"); StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Bg;
        ClientSize = new Size((int)(560 * sc), (int)(640 * sc)); MinimumSize = new Size((int)(520 * sc), (int)(600 * sc));
        try { Font = new Font("Segoe UI", 9f); using (var ib = IconArt.Render(48, true, UiState.Idle, 0)) Icon = IconArt.ToIcon(ib); } catch (Exception) { }
        bar.Dock = DockStyle.Top; bar.Height = (int)(58 * sc);
        host.Dock = DockStyle.Fill; host.BackColor = Theme.Bg;
        Controls.Add(host); Controls.Add(bar);
        drawer.Bounds = ClientRectangle; drawer.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(drawer); drawer.BringToFront();
        bar.BurgerClick += () => { if (drawer.Visible && drawer.IsOpen) drawer.Close(); else OpenDrawer(); };
        bar.BackClick += () => Navigate(PageId.Home);
        drawer.Choose += Navigate;
        drawer.QuitRequested += () => QuitRequested?.Invoke();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HandleEscape(); };
        SystemEvents.UserPreferenceChanged += OnPref;
        M.Changed += OnModel;
        Navigate(PageId.Home);
    }

    internal TopBar Bar => bar;
    internal DrawerOverlay Drawer => drawer;
    internal void HandleEscape() { if (drawer.Visible) drawer.Close(true); else if (Current != PageId.Home) Navigate(PageId.Home); }

    void OnPref(object? s, UserPreferenceChangedEventArgs e) { try { BeginInvoke(new Action(() => { Theme.Load(); ApplyTheme(); Navigate(Current); })); } catch (Exception) { } }

    void OnModel()
    {
        if (IsDisposed) return;
        bar.State = M.State; bar.Invalidate();
        drawer.ConflictCount = M.Conflicts.Count; drawer.TrashCount = M.Trash?.Select(t => t.Stamp).Distinct().Count() ?? 0;
        if (drawer.Visible) drawer.Invalidate();
        page?.ModelChanged();
    }

    static string TitleOf(PageId p) => p switch
    {
        PageId.Conflicts => Loc.S("nav.conflicts"), PageId.Trash => Loc.S("nav.trash"), PageId.Options => Loc.S("nav.options"),
        PageId.Server => Loc.S("nav.server"), PageId.About => Loc.S("nav.about"), _ => Loc.S("app")
    };

    public void Navigate(PageId id)
    {
        drawer.Close(true);
        if (page != null) { host.Controls.Remove(page); page.Dispose(); }
        page = id switch
        {
            PageId.Conflicts => new ConflictsPage(M), PageId.Trash => new TrashPage(M), PageId.Options => new OptionsPage(M),
            PageId.Server => new ServerPage(M), PageId.About => new AboutPage(M), _ => new HomePage(M)
        };
        page.GoTo += Navigate;
        host.Controls.Add(page);
        Current = id; Text = Loc.S("app");
        bar.Title = TitleOf(id); bar.ShowBack = id != PageId.Home; bar.ShowState = id == PageId.Home; bar.State = M.State; bar.Invalidate();
        drawer.Current = id; OnModel();
    }

    // Image de la fenetre (barre + page) : sert de fond au tiroir et aux captures d'ecran
    public Bitmap Snapshot()
    {
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Theme.Bg);
            using (var b = new Bitmap(bar.Width, bar.Height)) { bar.DrawToBitmap(b, new Rectangle(0, 0, bar.Width, bar.Height)); g.DrawImage(b, 0, 0); }
            using (var b = new Bitmap(host.Width, host.Height)) { host.DrawToBitmap(b, new Rectangle(0, 0, host.Width, host.Height)); g.DrawImage(b, host.Left, host.Top); }
        }
        return bmp;
    }

    public void OpenDrawer(bool instant = false)
    {
        drawer.Bounds = ClientRectangle;
        drawer.Open(Snapshot(), instant);
    }

    public Bitmap Screenshot()
    {
        if (!drawer.Visible) return Snapshot();
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        drawer.DrawToBitmap(bmp, new Rectangle(0, 0, drawer.Width, drawer.Height));
        return bmp;
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Bg; host.BackColor = Theme.Bg;
        try
        {
            int dark = Theme.Dark ? 1 : 0; Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int cap = (Theme.Bg.R << 0) | (Theme.Bg.G << 8) | (Theme.Bg.B << 16); Native.DwmSetWindowAttribute(Handle, 35, ref cap, 4);
        }
        catch (Exception) { }
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyTheme(); }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (HideOnClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { SystemEvents.UserPreferenceChanged -= OnPref; M.Changed -= OnModel; }
        base.Dispose(disposing);
    }
}
