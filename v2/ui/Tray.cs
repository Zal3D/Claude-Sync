// Claude-Sync V2 : icone de la zone de notification + execution des decisions de la surveillance (Watcher).
// Le cerveau (Watcher) decide ; ce fichier agit : synchroniser, fermer/relancer Claude a son ouverture, ecrire la presence, prevenir l'utilisateur.
#nullable enable
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeSync.V2;

sealed class TrayApp2 : ApplicationContext
{
    readonly AppModel M;
    readonly MainWindow win;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer anim, watch;
    readonly Watcher W = new();
    readonly ToolStripMenuItem pauseItem, syncItem, updItem;
    string? announcedUpdate; int updTicks;
    volatile bool handling; string? autoTried;
    float angle; Icon? cur; bool disposed;

    public TrayApp2(AppModel model, bool hidden, int exitAfterSeconds = 0, bool noWatch = false)
    {
        M = model;
        win = new MainWindow(M) { HideOnClose = true };
        win.QuitRequested += Quit;
        tray = new NotifyIcon { Visible = true, Text = Loc.S("app") };
        var menu = new ContextMenuStrip();
        menu.Items.Add(Loc.S("tray.open"), null, (_, _) => ShowWindow(PageId.Home));
        syncItem = new ToolStripMenuItem(Loc.S("tray.sync")); syncItem.Click += (_, _) => StartSync(W.Mode); menu.Items.Add(syncItem);
        pauseItem = new ToolStripMenuItem(Loc.S("tray.pause"));
        pauseItem.Click += (_, _) => { M.Paused = !M.Paused; pauseItem.Text = Loc.S(M.Paused ? "tray.resume" : "tray.pause"); };
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.S("nav.conflicts"), null, (_, _) => ShowWindow(PageId.Conflicts));
        menu.Items.Add(Loc.S("nav.trash"), null, (_, _) => ShowWindow(PageId.Trash));
        menu.Items.Add(Loc.S("nav.options"), null, (_, _) => ShowWindow(PageId.Options));
        menu.Items.Add(new ToolStripSeparator());
        updItem = new ToolStripMenuItem("") { Visible = false }; updItem.Click += (_, _) => M.InstallUpdate(); menu.Items.Add(updItem);
        var ver = new ToolStripMenuItem(Loc.S("about.version", V2Info.Version)) { Enabled = false }; menu.Items.Add(ver);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.S("nav.quit"), null, (_, _) => Quit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow(PageId.Home);
        tray.BalloonTipClicked += (_, _) => { if (M.Update != null) ShowWindow(PageId.About); else if (M.Conflicts.Count > 0) ShowWindow(PageId.Conflicts); };
        M.UpdateStarted += () => OnUi(Quit);

        anim = new System.Windows.Forms.Timer { Interval = 120 }; anim.Tick += (_, _) => { angle = (angle + 24) % 360; SetIcon(); };
        M.Changed += OnModel;
        SetIcon();
        IntPtr hh = win.Handle;                                    // cree le handle meme si la fenetre reste cachee
        SystemEvents.UserPreferenceChanged += (_, _) => { try { win.BeginInvoke(new Action(() => { Theme.Load(); win.ApplyTheme(); SetIcon(); })); } catch (Exception) { } };
        if (!hidden || !M.Configured) ShowWindow(PageId.Home);
        if (M.Configured && !M.Demo && !noWatch)       // --no-watch : aucune surveillance de Claude Desktop (jamais de fermeture/relance), pour les essais en bac a sable
        {
            Task.Run(() => { try { M.LoadTrash(); } catch (Exception) { } });
            watch = new System.Windows.Forms.Timer { Interval = 1000 }; watch.Tick += (_, _) => WatchTick(); watch.Start();
        }
        else watch = new System.Windows.Forms.Timer { Interval = 1000 };
        if (exitAfterSeconds > 0) { var t = new System.Windows.Forms.Timer { Interval = exitAfterSeconds * 1000 }; t.Tick += (_, _) => Quit(); t.Start(); }
    }

    // ------------------------------------------------------------ fenetre et icone
    void ShowWindow(PageId p)
    {
        win.Navigate(p); win.Show();
        if (win.WindowState == FormWindowState.Minimized) win.WindowState = FormWindowState.Normal;
        win.Activate();
    }

    public void RequestQuit() => OnUi(Quit);        // demande douce (installeur / mise a jour) : fermeture propre au lieu d'un arret force
    void OnUi(Action a) { try { if (win.InvokeRequired) win.BeginInvoke(a); else a(); } catch (Exception) { } }
    void Balloon(string msg, ToolTipIcon icon) => OnUi(() => { try { tray.ShowBalloonTip(5000, Loc.S("app"), msg, icon); } catch (Exception) { } });

    void OnModel()
    {
        SetIcon();
        if (M.State == UiState.Working) { if (!anim.Enabled) anim.Start(); } else if (anim.Enabled) anim.Stop();
        syncItem.Enabled = !M.Busy && M.Configured;
        var up = M.Update;
        updItem.Visible = up != null; if (up != null) { updItem.Text = Loc.S("tray.update", up.Version); updItem.Enabled = !M.UpdateBusy; }
        if (up != null && announcedUpdate != up.Version) { announcedUpdate = up.Version; Balloon(Loc.S("update.balloon", up.Version), ToolTipIcon.Info); }
    }

    void SetIcon()
    {
        if (disposed) return;
        int sz = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using (var bmp = IconArt.Render(sz, false, M.State, angle))
        {
            var old = cur; cur = IconArt.ToIcon(bmp); tray.Icon = cur; old?.Dispose();
        }
        string t = Loc.S("app") + " - " + Gfx.StateLabel(M.State).ToLowerInvariant();
        tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
    }

    void Quit()
    {
        disposed = true; anim.Stop(); watch.Stop();
        tray.Visible = false; tray.Dispose();
        win.HideOnClose = false; try { win.Close(); } catch (Exception) { }
        ExitThread();
    }

    // ------------------------------------------------------------ surveillance
    void WatchTick()
    {
        // verification des mises a jour : 90 s apres le demarrage puis toutes les 30 min (la regle des 6 h est appliquee par Updater2)
        if (++updTicks == 90 || updTicks % 1800 == 0) M.CheckUpdate(false);
        // installation automatique (si l'utilisateur l'a demandee) : au repos seulement, une seule tentative par version
        var upd = M.Update;
        if (upd != null && M.AutoUpdate && !M.UpdateBusy && autoTried != upd.Version && !M.Busy && !handling && !M.Paused && M.Conflicts.Count == 0 && M.State != UiState.Working)
        { autoTried = upd.Version; M.InstallUpdate(); }
        bool running = ClaudeDesktop.Running();
        var o = W.Tick(new WatchInput
        {
            Now = DateTime.Now, Running = running, WindowVisible = running && ClaudeDesktop.WindowVisible(), Busy = M.Busy, Handling = handling,
            Paused = M.Paused, Retryable = M.State == UiState.Offline || M.State == UiState.Blocked, BlockLaunch = M.BlockLaunch
        });
        foreach (var s in o.Steps)
            switch (s.Act)
            {
                case WatchAct.SyncNow: StartSync(o.ClaudeMode); break;
                case WatchAct.Launch: HandleLaunch(false); break;
                case WatchAct.LaunchCheck: HandleLaunch(true); break;
                case WatchAct.Presence: bool vis = s.Arg; Task.Run(() => { try { M.WritePresenceNow(vis); } catch (Exception) { } }); break;
            }
    }

    void StartSync(string mode)
    {
        if (M.Busy || handling || M.Paused || M.Demo || !M.Configured) return;
        Task.Run(() =>
        {
            int code = M.RunSyncBlocking(mode == "idle" ? "idle" : "auto");
            W.SyncDone(DateTime.Now, code);
            if (code == 1) Balloon(Loc.S("tray.conflicts"), ToolTipIcon.Warning);
            var n = M.Engine?.Notices; // (notices : jamais d'icone rouge)
        });
    }

    // Claude s'ouvre : on le ferme, on synchronise, on le relance (sauf serveur injoignable). Reprise de la V1, mode econome compris.
    void HandleLaunch(bool checkIncoming)
    {
        handling = true;
        Task.Run(() =>
        {
            try
            {
                var p = M.Probe();
                if (!p.ok)
                {
                    Balloon(Loc.S(p.refused ? "tray.refused" : "tray.unreachable"), ToolTipIcon.Warning);
                    M.State = UiState.Offline; W.LaunchSkipped(DateTime.Now, true); M.Raise();
                    return;
                }
                string? other = M.OtherPcActive();
                if (other != null)
                {
                    bool yes = false;
                    try { win.Invoke(new Action(() => yes = MessageBox.Show(Loc.S("tray.other.ask", other), Loc.S("tray.other.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)); } catch (Exception) { }
                    if (yes) return;
                    ClaudeDesktop.Kill(); W.ClaudeKilledByUs(); return;
                }
                if (checkIncoming && M.CountIncoming() == 0) return;     // fenetre rouverte : on ne ferme Claude que si le serveur a du nouveau
                var cfg = M.Config!;
                bool slow = LinkQuality.IsSlow(p.ms, cfg);
                M.PhaseOverride = slow ? Loc.S("tray.phase.slow", p.ms, cfg.LaunchWaitSeconds) : Loc.S("tray.phase.sync"); M.Raise();
                OnUi(() => ShowWindow(PageId.Home));
                ClaudeDesktop.Kill();
                int code;
                if (!slow) code = M.RunSyncBlocking("no");
                else
                {   // mode econome : au plus LaunchWaitSeconds ; ensuite Claude s'ouvre, les recuperations restantes sont reportees (envoi seul), reprises a sa fermeture
                    var st = Task.Run(() => M.RunSyncBlocking("no"));
                    if (st.Wait(cfg.LaunchWaitSeconds * 1000)) code = st.Result;
                    else
                    {
                        M.StopPulls();
                        Log.W("Liaison lente (" + p.ms + " ms) : " + cfg.LaunchWaitSeconds + " s ecoulees, Claude s'ouvre ; recuperations restantes reportees.");
                        code = st.Wait(20000) ? st.Result : 0;
                        Balloon(Loc.S("tray.slow"), ToolTipIcon.Info);
                        W.SlowLaunch();
                    }
                }
                M.PhaseOverride = null;
                W.SyncDone(DateTime.Now, code);
                W.LaunchHandled(DateTime.Now, true);
                ClaudeDesktop.Relaunch();
                if (code == 1) Balloon(Loc.S("tray.conflicts"), ToolTipIcon.Warning);
                else if (code == 0) OnUi(() => win.Hide());
            }
            catch (Exception ex) { Log.W("Prise en charge de l'ouverture de Claude : " + ex.Message); }
            finally { M.PhaseOverride = null; handling = false; M.Raise(); }
        });
    }
}
