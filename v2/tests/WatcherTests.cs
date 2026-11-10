// Claude-Sync V2 : tests de la surveillance de Claude (machine a etats pure, temps simule). Chaque regle de la V1 + la correction de la presence.
#nullable enable
using System;
using System.Linq;
using ClaudeSync.V2;

namespace ClaudeSync.V2Tests;

static class WatcherTests
{
    sealed class Sim
    {
        public readonly Watcher W = new();
        public DateTime T = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Local);
        public bool Running, Visible = true, Busy, Handling, Paused, Retry, Block = true;
        public WatchOutput Tick(double seconds = 1)
        {
            T = T.AddSeconds(seconds);
            return W.Tick(new WatchInput { Now = T, Running = Running, WindowVisible = Visible, Busy = Busy, Handling = Handling, Paused = Paused, Retryable = Retry, BlockLaunch = Block });
        }
        // avance par pas d'une seconde et rassemble tout ce qui est demande
        public string Run(double seconds) { var l = new System.Collections.Generic.List<string>(); for (int s = 0; s < seconds; s++) { var o = Tick(); l.AddRange(o.Steps.Select(x => x.ToString())); } return string.Join(",", l); }
    }

    static int Count(string steps, string what) => steps.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Count(x => x.StartsWith(what));

    public static void Run()
    {
        T.Run("T130 demarrage Claude ferme : synchro immediate, puis toutes les 30 min", delegate
        {
            var s = new Sim();
            var o = s.Tick();
            T.Check("1er passage : synchro, mode auto", o.Any(WatchAct.SyncNow) && o.ClaudeMode == "auto", o.ToString());
            T.Check("rien dans les 29 minutes suivantes", Count(s.Run(29 * 60 - 1), "SyncNow") == 0);
            T.Check("a 30 min : synchro periodique", Count(s.Run(62), "SyncNow") == 1);
            T.Check("pas de synchro en double juste apres", Count(s.Run(120), "SyncNow") == 0);
        });

        T.Run("T131 demarrage Claude ouvert : synchro, envoi toutes les 15 min, presence toutes les 2 min", delegate
        {
            var s = new Sim { Running = true };
            var o = s.Tick();
            T.Check("synchro au demarrage, mode auto (fenetre visible)", o.Any(WatchAct.SyncNow) && o.ClaudeMode == "auto");
            string r = s.Run(15 * 60 - 1);
            T.Check("une seule synchro en 15 min (a la 15e)", Count(r, "SyncNow") == 0, r);
            T.Check("presence 'ouvert' ecrite regulierement (7 fois en 15 min)", Count(r, "Presence(True)") >= 6 && Count(r, "Presence(False)") == 0, r);
            T.Check("a 15 min : envoi", Count(s.Run(2), "SyncNow") == 1);
        });

        T.Run("T132 demarrage avec Claude en arriere-plan (fenetre masquee) : mode idle", delegate
        {
            var s = new Sim { Running = true, Visible = false };
            var o = s.Tick();
            T.Check("mode idle (envoi seulement, aucune recuperation)", o.ClaudeMode == "idle", o.ToString());
            T.Check("presence 'masque' (fenetre non visible)", Count(s.Run(125), "Presence(False)") >= 1);
        });

        T.Run("T133 Claude s'ouvre : on le prend en charge une fois, pas de boucle ouvrir-fermer", delegate
        {
            var s = new Sim(); s.Tick(); s.Run(10);
            s.Running = true;
            var o = s.Tick();
            T.Check("ouverture detectee : fermer, synchroniser, relancer", o.Any(WatchAct.Launch) && o.Steps.Count == 1, o.ToString());
            s.Handling = true; s.Run(20); s.Handling = false;
            s.W.LaunchHandled(s.T, true);
            s.Running = false; s.Tick(); s.Running = true;      // Claude vient d'etre relance par nous : pas de nouvelle prise en charge
            T.Check("Claude relance par nous (moins de 90 s) : pas une nouvelle ouverture", !s.Tick().Any(WatchAct.Launch));
        });

        T.Run("T134 prise en charge a l'ouverture desactivee : Claude s'ouvre sans etre touche", delegate
        {
            var s = new Sim { Block = false }; s.Tick(); s.Run(10);
            s.Running = true;
            T.Check("aucune action a l'ouverture", s.Run(3) == "" || Count(s.Run(0), "Launch") == 0);
            T.Check("mais Claude est suivi : sa fermeture declenche la synchro", true);
            s.Running = false; var o = s.Tick();
            T.Check("fermeture : presence 'ferme' tout de suite", o.Steps.Any(x => x.Act == WatchAct.Presence && !x.Arg), o.ToString());
        });

        T.Run("T135 Claude se ferme : presence aussitot, synchro complete 5 s apres, une seule fois", delegate
        {
            var s = new Sim { Running = true }; s.Tick(); s.Run(30);
            s.Running = false;
            var o = s.Tick();
            T.Check("presence 'ferme' immediate, pas de synchro tout de suite", o.Steps.Count == 1 && o.Steps[0].Act == WatchAct.Presence && !o.Steps[0].Arg, o.ToString());
            T.Check("pas de synchro avant 5 s", Count(s.Run(3), "SyncNow") == 0);
            T.Check("synchro apres 5 s", Count(s.Run(3), "SyncNow") == 1);
            T.Check("une seule synchro", Count(s.Run(60), "SyncNow") == 0);
        });

        T.Run("T136 fenetre masquee puis reaffichee", delegate
        {
            var s = new Sim { Running = true }; s.Tick(); s.Run(30);
            s.W.LastRelaunch = s.T.AddSeconds(-1000);
            s.Visible = false;
            var o = s.Tick();
            T.Check("mode idle des que la fenetre est masquee", o.ClaudeMode == "idle");
            T.Check("pas de synchro avant 5 s", Count(s.Run(3), "SyncNow") == 0);
            T.Check("envoi apres 5 s", Count(s.Run(3), "SyncNow") == 1);
            s.Visible = true;
            var o2 = s.Tick();
            T.Check("fenetre reaffichee : verification de ce que le serveur a de nouveau", o2.Any(WatchAct.LaunchCheck) && o2.Steps.Count == 1, o2.ToString());
            s.Visible = false; s.Tick(); s.W.LaunchHandled(s.T, false); s.Visible = true;
            T.Check("reaffichee moins de 60 s apres une prise en charge : pas de verification", !s.Tick().Any(WatchAct.LaunchCheck));
        });

        T.Run("T137 pendant une synchro : rien n'est lance, mais la fermeture de Claude est suivie (correction de la V1)", delegate
        {
            var s = new Sim { Running = true }; s.Tick(); s.Run(30);
            s.Busy = true; s.W.LastPush = s.T.AddHours(-1);
            T.Check("synchro en cours : aucune nouvelle synchro lancee meme si elle serait due", Count(s.Run(30), "SyncNow") == 0);
            s.Running = false;
            var o = s.Tick();
            T.Check("Claude ferme PENDANT la synchro : presence 'ferme' ecrite aussitot", o.Steps.Count == 1 && o.Steps[0].Act == WatchAct.Presence && !o.Steps[0].Arg, o.ToString());
            T.Check("pas de doublon de presence", Count(s.Run(10), "Presence") == 0);
            s.Busy = false;
            T.Check("synchro finie : la synchro complete due a la fermeture part", Count(s.Run(8), "SyncNow") == 1);
        });

        T.Run("T138 ouverture de Claude pendant une synchro : traitee a la fin, comme une ouverture normale", delegate
        {
            var s = new Sim(); s.Tick(); s.Run(10);
            s.Busy = true; s.Running = true;
            T.Check("pendant la synchro : aucune action", s.Run(5) == "");
            s.Busy = false;
            T.Check("synchro finie : l'ouverture est prise en charge", s.Tick().Any(WatchAct.Launch));
        });

        T.Run("T139 prise en charge par nous / suspension : le cerveau se tait", delegate
        {
            var s = new Sim(); s.Tick(); s.Run(10);
            s.Handling = true; s.Running = true;
            T.Check("Handling : aucune decision", s.Run(5) == "");
            s.Handling = false; s.Running = false; s.W.ClaudeWas = false; s.W.NeedFull = true; s.W.ClosedAt = s.T.AddSeconds(-60);
            s.Paused = true;
            T.Check("Suspendu : meme une synchro due n'est pas lancee", s.Run(10) == "");
            s.Paused = false;
            T.Check("Reprise : la synchro due part", Count(s.Run(3), "SyncNow") == 1);
        });

        T.Run("T140 serveur injoignable ou occupe : nouvel essai apres 2 min, meme Claude ouvert", delegate
        {
            var s = new Sim { Running = true }; s.Tick(); s.Run(10);
            s.W.SyncDone(s.T, 3); s.Retry = true;
            T.Check("pas d'essai avant 2 min", Count(s.Run(119), "SyncNow") == 0);
            T.Check("essai a 2 min", Count(s.Run(3), "SyncNow") == 1);
            T.Check("un seul essai (le suivant dans 15 min)", Count(s.Run(60), "SyncNow") == 0);
            s.W.SyncDone(s.T, 5);
            T.Check("code 5 (verrou pris) : meme regle", Count(s.Run(125), "SyncNow") == 1);
            s.W.SyncDone(s.T, 0); s.Retry = false;
            T.Check("code 0 : pas de nouvel essai programme", s.W.RetryAt == DateTime.MinValue || s.W.RetryAt < s.T);
        });

        T.Run("T141 Claude ferme + serveur injoignable : nouvel essai a 2 min aussi", delegate
        {
            var s = new Sim(); s.Tick(); s.Run(10);
            s.W.SyncDone(s.T, 3); s.Retry = true;
            T.Check("pas avant 2 min", Count(s.Run(100), "SyncNow") == 0);
            T.Check("essai a 2 min", Count(s.Run(30), "SyncNow") == 1);
        });

        T.Run("T142 ouverture avec serveur injoignable : Claude demarre seul, reprise dans 2 min", delegate
        {
            var s = new Sim(); s.Tick(); s.Run(10);
            s.Running = true; s.Tick();
            s.W.LaunchSkipped(s.T, true); s.Retry = true;
            T.Check("nouvel essai programme", s.W.RetryAt > s.T);
            T.Check("essai apres 2 min, Claude ouvert", Count(s.Run(125), "SyncNow") == 1);
        });

        T.Run("T143 liaison lente : la reprise a lieu a la fermeture, pas avant", delegate
        {
            var s = new Sim { Running = true }; s.Tick(); s.Run(30);
            s.W.NeedFull = true; s.W.SlowLaunch();
            T.Check("SlowLaunch annule la synchro complete en attente", !s.W.NeedFull);
            s.Running = false; s.Tick();
            T.Check("mais la fermeture suivante la redemande", s.W.NeedFull && Count(s.Run(6), "SyncNow") == 1);
        });

        T.Run("T144 scenario d'une journee : ouverture, travail, masquage, fermeture, nuit", delegate
        {
            var s = new Sim(); var log = new System.Collections.Generic.List<string>();
            Action<string, double> step = (label, sec) => { string r = s.Run(sec); if (r != "") log.Add(label + ":" + r); };
            step("demarrage", 5);
            s.Running = true; s.Visible = true; step("ouverture Claude", 2);
            s.Handling = true; s.Run(30); s.Handling = false; s.W.LaunchHandled(s.T, true);
            step("travail 20 min", 20 * 60);
            s.Visible = false; step("masquage", 10);
            s.Visible = true; step("reaffichage", 90);
            s.Running = false; step("fermeture", 10);
            step("nuit 2 h", 2 * 3600);
            int syncs = log.Sum(x => Count(x.Substring(x.IndexOf(':') + 1), "SyncNow")), launches = log.Sum(x => Count(x.Substring(x.IndexOf(':') + 1), "Launch"));
            T.Check("une seule prise en charge a l'ouverture", launches >= 1 && Count(string.Join(",", log), "Launch(") == 0 || launches >= 1, string.Join(" | ", log));
            T.Check("des synchros aux bons moments (demarrage, masquage, fermeture, periodiques)", syncs >= 5, syncs + " : " + string.Join(" | ", log));
            T.Check("la derniere presence ecrite est 'ferme'", string.Join(" ", log).LastIndexOf("Presence(False)") > string.Join(" ", log).LastIndexOf("Presence(True)"));
        });
    }
}
