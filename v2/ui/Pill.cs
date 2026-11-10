// Claude-Sync V2 : bouton arrondi (principal = couleur d'accent ; secondaire = contour net). Remplace le PillButton de la V1 dans la V2 :
// contour lisible sur la carte, survol et appui nets, etat desactive clair, anneau de focus au clavier.
#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClaudeSync.V2;

sealed class Pill : Control
{
    public bool Primary;
    bool hover, down;

    public Pill(string text, bool primary)
    {
        Text = text; Primary = primary; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k) => k == Keys.Enter || k == Keys.Space || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R * (1 - t) + b.R * t), (int)(a.G * (1 - t) + b.G * t), (int)(a.B * (1 - t) + b.B * t));

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; Gfx.Quality(g);
        // fond = celui de la carte parente (aucun coin carre visible autour de l'arrondi)
        g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill, border, text;
        if (!Enabled) { fill = Mix(Theme.Card, Theme.Track, 0.5); border = Mix(Theme.Card, Theme.Subtle, 0.18); text = Mix(Theme.Subtle, Theme.Card, 0.25); }
        else if (Primary) { fill = down ? Color.FromArgb(55, 112, 224) : hover ? Color.FromArgb(100, 156, 255) : Theme.Accent; border = fill; text = Color.White; }
        else
        {
            fill = down ? Mix(Theme.Card, Theme.Text, 0.14) : hover ? Mix(Theme.Card, Theme.Text, 0.09) : Theme.Card;
            border = Mix(Theme.Card, Theme.Text, hover ? 0.42 : 0.30);      // contour lisible sur la carte, clair comme sombre
            text = Theme.Text;
        }
        using (var p = Gfx.Round(r, r.Height / 2))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            using (var pen = new Pen(border, 1f)) g.DrawPath(pen, p);
        }
        if (Focused && Enabled && ShowFocusCues)
        {
            var fr = new RectangleF(r.X + 2.5f, r.Y + 2.5f, r.Width - 5, r.Height - 5);
            using (var p = Gfx.Round(fr, fr.Height / 2)) using (var pen = new Pen(Color.FromArgb(150, Theme.Accent), 1.5f)) g.DrawPath(pen, p);
        }
        using (var tb = new SolidBrush(text)) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(Text, Font, tb, new RectangleF(0, 0, Width, Height), sf);
    }
}
