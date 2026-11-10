// Claude-Sync V2 : fenetre d'installation (une seule fenetre : presentation, option de demarrage, progression, ouverture).
// Affichee quand on lance le programme telecharge (hors dossier d'installation), ou par "install" ; en mode maintenance si deja installe.
#nullable enable
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeSync.V2;

sealed class SetupBar : Control
{
    double frac;
    public double Fraction { get => frac; set { frac = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
    public SetupBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; Gfx.Quality(g); g.Clear(Parent?.BackColor ?? Theme.Bg);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var p = Gfx.Round(r, r.Height / 2)) using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
        if (frac > 0)
        {
            var f = new RectangleF(0, 0, Math.Max(r.Height, (float)(r.Width * frac)), r.Height);
            using (var p = Gfx.Round(f, f.Height / 2)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
        }
    }
}

sealed class SetupWindow : Form
{
    readonly InstallTarget target;
    readonly string installedVer;
    readonly Label title, intro, folder, status;
    readonly CheckBox startup;
    readonly SetupBar bar;
    readonly Pill main, cancel, uninstall;
    bool busy, done;

    public SetupWindow(InstallTarget t)
    {
        target = t; installedVer = Installer2.InstalledVersion(t);
        bool installed = installedVer.Length > 0;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Text = Loc.S("app"); BackColor = Theme.Bg; ForeColor = Theme.Text; Font = new Font(Gfx.Fam, 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 360);
        try { using (var ib = IconArt.Render(48, true, UiState.Idle, 0)) Icon = IconArt.ToIcon(ib); } catch (Exception) { }

        title = new Label { Text = Loc.S(installed ? "setup.title.update" : "setup.title"), Font = new Font(Gfx.FamDisplay, 20f, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Theme.Bg, Left = 96, Top = 26, Width = 440, Height = 44 };
        intro = new Label { Text = Loc.S("setup.intro"), ForeColor = Theme.Text, BackColor = Theme.Bg, Left = 96, Top = 78, Width = 440, Height = 78 };
        folder = new Label { Text = Loc.S("setup.folder", t.Dir) + (installed ? "\n" + Loc.S("setup.installed", installedVer) : ""), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Left = 96, Top = 160, Width = 440, Height = 42, AutoEllipsis = true };
        startup = new CheckBox { Text = Loc.S("setup.startup"), Checked = true, ForeColor = Theme.Text, BackColor = Theme.Bg, Left = 96, Top = 208, Width = 440, Height = 26, FlatStyle = FlatStyle.System };
        status = new Label { Text = "", ForeColor = Theme.Subtle, BackColor = Theme.Bg, Left = 96, Top = 246, Width = 440, Height = 24 };
        bar = new SetupBar { Left = 96, Top = 274, Width = 440, Height = 8, Visible = false };
        main = new Pill(Loc.S(!installed ? "setup.install" : installedVer == V2Info.Version ? "setup.reinstall" : "setup.update"), true) { Left = 306, Top = 304, Width = 132, Height = 36 };
        cancel = new Pill(Loc.S("setup.cancel"), false) { Left = 446, Top = 304, Width = 90, Height = 36 };
        uninstall = new Pill(Loc.S("setup.uninstall"), false) { Left = 20, Top = 304, Width = 120, Height = 36, Visible = installed };
        main.Click += (_, _) => OnMain(); cancel.Click += (_, _) => Close(); uninstall.Click += (_, _) => OnUninstall();
        Controls.AddRange(new Control[] { title, intro, folder, startup, status, bar, main, cancel, uninstall });
        AcceptButton = null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Gfx.Quality(e.Graphics);
        try { using (var ib = IconArt.Render(64, true, UiState.Idle, 0)) e.Graphics.DrawImage(ib, 20, 26, 64, 64); } catch (Exception) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { if (busy) e.Cancel = true; base.OnFormClosing(e); }

    void SetStatus(string s, int pct, bool error = false)
    {
        status.Text = s; status.ForeColor = error ? Theme.Bad : Theme.Subtle;
        if (pct >= 0) { bar.Visible = true; bar.Fraction = pct / 100.0; }
    }

    void OnMain()
    {
        if (done) { try { Installer2.Launch(target); } catch (Exception) { } Close(); return; }
        busy = true; main.Enabled = cancel.Enabled = uninstall.Enabled = startup.Enabled = false;
        bool st = startup.Checked;
        var th = new Thread(() =>
        {
            var r = Installer2.Install(target, new InstallOptions { Startup = st }, (m, p) => BeginInvoke(new Action(() => SetStatus(m, p, p < 0))));
            BeginInvoke(new Action(() =>
            {
                busy = false; cancel.Enabled = true;
                if (r.Ok) { done = true; SetStatus(r.Message, 100); main.Text = Loc.S("setup.open"); main.Width = 170; main.Left = 266; cancel.Text = Loc.S("setup.close"); main.Enabled = true; main.Invalidate(); }
                else { SetStatus(r.Message, -1, true); main.Enabled = true; uninstall.Enabled = true; startup.Enabled = true; }
            }));
        }) { IsBackground = true };
        th.Start();
    }

    void OnUninstall()
    {
        if (MessageBox.Show(this, Loc.S("setup.uninstall.ask"), Loc.S("app"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var r = Installer2.Uninstall(target, false);
        SetStatus(r.Message, -1, !r.Ok);
        main.Visible = uninstall.Visible = startup.Visible = false; cancel.Text = Loc.S("setup.close"); cancel.Invalidate();
    }
}
