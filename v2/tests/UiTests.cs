// Claude-Sync V2 : tests de l'interface (textes FR/EN, navigation de la fenetre unique, menu, Retour, Echap, rendu non vide).
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class UiTests
{
    static string srcDir = "";

    public static void Run()
    {
        srcDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "v2", "ui"));
        Textes();
        Exception? err = null;
        var t = new Thread(() => { try { Fenetre(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (err != null) T.Check("interface : aucune exception", false, err.GetType().Name + " : " + err.Message + "\n" + err.StackTrace);
    }

    static void Textes()
    {
        T.Run("T120 textes : chaque cle existe en francais ET en anglais, memes parametres, aucune cle manquante dans le code", delegate
        {
            var vides = Loc.Keys.Where(k => string.IsNullOrWhiteSpace(Loc.Fr(k)) || string.IsNullOrWhiteSpace(Loc.En(k))).ToList();
            T.Check("aucune traduction vide", vides.Count == 0, string.Join(",", vides));
            Func<string, string> ph = s => string.Join(",", Regex.Matches(s, @"\{\d+\}").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(x => x));
            var diff = Loc.Keys.Where(k => ph(Loc.Fr(k)) != ph(Loc.En(k))).ToList();
            T.Check("memes parametres {0} {1}... dans les deux langues", diff.Count == 0, string.Join(",", diff));
            var memes = Loc.Keys.Where(k => Loc.Fr(k) == Loc.En(k) && Regex.Replace(Loc.Fr(k), @"\{\d+\}", "").Trim().Contains(' ')).ToList();   // un mot identique (Conversations, Claude-Sync) est normal
            T.Check("pas de phrase laissee en francais dans la version anglaise", memes.Count == 0, string.Join(",", memes));
            var used = new HashSet<string>();
            foreach (string f in Directory.GetFiles(srcDir, "*.cs")) foreach (Match m in Regex.Matches(File.ReadAllText(f), @"Loc\.S\(""([^""]+)""")) used.Add(m.Groups[1].Value);
            var missing = used.Where(k => !k.EndsWith(".") && !Loc.Has(k)).ToList();   // "set." = prefixe d'une cle construite
            T.Check(used.Count + " cles utilisees dans le code, aucune absente de la table", missing.Count == 0, string.Join(",", missing));
            Loc.Set("fr"); string a = Loc.S("home.left", 5); Loc.Set("en"); string b = Loc.S("home.left", 5); Loc.Set("fr");
            T.Check("parametres substitues", a == "5 restants" && b == "5 left");
            T.Check("cle inconnue : la cle elle-meme (jamais d'exception)", Loc.S("zzz.inconnue") == "zzz.inconnue");
        });
    }

    static MainWindow NewWindow(string state = "uistate")
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ThemeX.Force(true); Loc.Set("fr");
        var m = AppModel.DemoModel(Path.Combine(Env.Tmp, state));
        var w = new MainWindow(m);
        w.Show(); for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(30); }
        return w;
    }

    static double Variance(Bitmap b)
    {
        long n = 0; double s = 0, s2 = 0;
        for (int y = 0; y < b.Height; y += 3) for (int x = 0; x < b.Width; x += 3) { var c = b.GetPixel(x, y); double v = c.R * 0.3 + c.G * 0.59 + c.B * 0.11; s += v; s2 += v * v; n++; }
        double mean = s / n; return s2 / n - mean * mean;
    }

    static string Sig(Bitmap b)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < b.Height; y += 7) for (int x = 0; x < b.Width; x += 7) sb.Append(b.GetPixel(x, y).ToArgb() & 0xFFFFFF).Append(',');
        return sb.ToString();
    }

    static void Settle() { for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(30); } }

    static void Fenetre()
    {
        T.Run("T121 navigation : menu, Retour a droite, Echap, une seule fenetre", delegate
        {
            using var w = NewWindow();
            T.Check("demarre sur l'Accueil, sans bouton Retour", w.Current == PageId.Home && !w.Bar.ShowBack && w.Bar.ShowState);
            T.Check("le clic sur les 3 barres ouvre le menu", w.Bar.ClickAt(w.Bar.BurgerCenter) && w.Drawer.Visible);
            T.Check("6 lignes dans le menu (Accueil, Conflits, Corbeille, Options, A propos, Quitter)", w.Drawer.ItemCount == 6);
            w.Drawer.ClickAt(w.Drawer.ItemCenter(2));
            T.Check("clic sur Corbeille : page Corbeille, menu referme", w.Current == PageId.Trash && !w.Drawer.Visible);
            T.Check("bouton Retour affiche, titre de la page", w.Bar.ShowBack && !w.Bar.ShowState && w.Bar.Title == Loc.S("nav.trash"));
            T.Check("Retour ramene a l'Accueil", w.Bar.ClickAt(w.Bar.BackCenter) && w.Current == PageId.Home && !w.Bar.ShowBack);
            w.Navigate(PageId.Conflicts); w.HandleEscape();
            T.Check("Echap sur une page : retour a l'Accueil", w.Current == PageId.Home);
            w.OpenDrawer(true); w.HandleEscape();
            T.Check("Echap avec le menu ouvert : ferme le menu seulement", !w.Drawer.Visible && w.Current == PageId.Home);
            w.OpenDrawer(true); w.Drawer.ClickAt(new Point(w.Drawer.Width - 5, 300));
            for (int i = 0; i < 40 && w.Drawer.Visible; i++) { Application.DoEvents(); Thread.Sleep(25); }   // fermeture animee
            T.Check("clic a cote du menu : le ferme", !w.Drawer.Visible);
            foreach (var (i, p) in new[] { (0, PageId.Home), (1, PageId.Conflicts), (2, PageId.Trash), (3, PageId.Options), (4, PageId.About) })
            { w.OpenDrawer(true); w.Drawer.ClickAt(w.Drawer.ItemCenter(i)); T.Check("ligne " + i + " -> " + p, w.Current == p); }
            w.Navigate(PageId.Server);
            T.Check("la page Serveur est dans la meme fenetre, avec Retour", w.Current == PageId.Server && w.Bar.ShowBack);
            bool quit = false; w.QuitRequested += () => quit = true;
            w.OpenDrawer(true); w.Drawer.ClickAt(w.Drawer.ItemCenter(5));
            T.Check("Quitter : demande d'arret, menu ferme", quit && !w.Drawer.Visible);
        });

        T.Run("T122 pastilles du menu : conflits et corbeille suivent le modele", delegate
        {
            using var w = NewWindow();
            T.Check("2 conflits, 2 suppressions affichees", w.Drawer.ConflictCount == 2 && w.Drawer.TrashCount == 2, w.Drawer.ConflictCount + "/" + w.Drawer.TrashCount);
        });

        T.Run("T123 langues : le changement s'applique aux titres", delegate
        {
            using var w = NewWindow("uistate-l");
            w.Navigate(PageId.Options);
            T.Check("titre en francais", w.Bar.Title == "Options" || w.Bar.Title == Loc.S("nav.options"));
            Loc.Set("en"); w.Navigate(PageId.Trash);
            T.Check("titre en anglais apres changement", w.Bar.Title == "Trash" && Loc.Lang == "en", w.Bar.Title);
            w.Navigate(PageId.Options);
            T.Check("Options -> Settings", w.Bar.Title == "Settings", w.Bar.Title);
            Loc.Set("fr");
        });

        T.Run("T124 rendu : chaque page dessine du contenu, les pages different entre elles, clair et sombre different", delegate
        {
            using var w = NewWindow("uistate-r");
            var sigs = new Dictionary<PageId, string>();
            foreach (PageId p in Enum.GetValues(typeof(PageId)))
            {
                w.Navigate(p); Settle();
                using var bmp = w.Screenshot();
                double v = Variance(bmp);
                T.Check("page " + p + " : image non uniforme", v > 15, "variance " + v);
                sigs[p] = Sig(bmp);
            }
            T.Check("les 6 pages ont chacune un rendu different", sigs.Values.Distinct().Count() == 6);
            w.Navigate(PageId.Home); w.OpenDrawer(true);
            using (var menu = w.Screenshot()) T.Check("menu ouvert : image differente de l'accueil", Sig(menu) != sigs[PageId.Home]);
            w.Drawer.Close(true); ThemeX.Force(false); w.Navigate(PageId.Home); Settle();
            using (var clair = w.Screenshot()) T.Check("theme clair : rendu different du sombre", Sig(clair) != sigs[PageId.Home]);
            ThemeX.Force(true);
        });

        T.Run("T125 etat vide : sans serveur, l'accueil et la page de connexion existent", delegate
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ThemeX.Force(true);
            var m = new AppModel(Path.Combine(Env.Tmp, "uivide"));
            T.Check("modele non configure", !m.Configured);
            using var w = new MainWindow(m); w.Show(); Settle();
            using (var bmp = w.Screenshot()) T.Check("accueil dessine (non vide)", Variance(bmp) > 15);
            w.Navigate(PageId.Server); T.Check("page de connexion accessible", w.Current == PageId.Server);
        });
    }
}
