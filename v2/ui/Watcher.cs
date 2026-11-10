// Claude-Sync V2 : la surveillance de Claude Desktop (cerveau). MACHINE A ETATS PURE : aucune lecture de processus, aucune horloge, aucun acces
// reseau. Elle recoit ce qui est observe (Claude tourne ? sa fenetre est visible ? une synchro est en cours ?) et l'heure, et dit quoi FAIRE.
// Reprend les regles de la V1 (TrayApp.Watch), avec une correction : la fermeture de Claude est detectee MEME pendant une synchro
// (en V1 la presence restait a "ouvert" pendant toute la synchro, et l'autre PC croyait Claude ouvert).
//   - Claude s'ouvre            -> on le ferme, on synchronise, on le relance (si BlockLaunch), sauf serveur injoignable
//   - Claude se ferme           -> synchro complete 5 s apres ; presence mise a "ferme" tout de suite
//   - fenetre masquee (croix)   -> programme residant : envoi seulement apres 5 s, aucune recuperation ("idle")
//   - fenetre reaffichee        -> on ne ferme Claude que si le serveur a du nouveau (verification), au plus toutes les 60 s
//   - Claude ouvert             -> envoi seulement toutes les 15 min ; Claude ferme : synchro complete toutes les 30 min
//   - serveur injoignable / PC occupe -> nouvel essai apres 2 min, meme Claude ouvert
#nullable enable
using System;
using System.Collections.Generic;

namespace ClaudeSync.V2;

public enum WatchAct { SyncNow, Launch, LaunchCheck, Presence }

public readonly struct WatchStep
{
    public readonly WatchAct Act; public readonly bool Arg;     // Presence : Claude visible ? ; autres : inutilise
    public WatchStep(WatchAct a, bool arg = false) { Act = a; Arg = arg; }
    public override string ToString() => Act + (Act == WatchAct.Presence ? "(" + Arg + ")" : "");
}

public sealed class WatchInput
{
    public DateTime Now;
    public bool Running;            // un processus Claude Desktop existe
    public bool WindowVisible;      // sa fenetre principale est visible (fermee avec la croix = masquee mais le programme reste actif)
    public bool Busy;               // une synchro est en cours
    public bool Handling;           // on est en train de fermer / synchroniser / relancer Claude nous-memes
    public bool Paused;             // l'utilisateur a suspendu la synchronisation
    public bool Retryable;          // dernier etat = hors ligne ou bloque (nouvel essai possible)
    public bool BlockLaunch = true; // a l'ouverture de Claude : synchroniser d'abord
}

public sealed class WatchOutput
{
    public readonly List<WatchStep> Steps = new();
    public string ClaudeMode = "auto";      // a donner au moteur : "auto" (fenetre visible ou Claude ferme) ou "idle" (fenetre masquee)
    public bool Any(WatchAct a) { foreach (var s in Steps) if (s.Act == a) return true; return false; }
    public override string ToString() => string.Join(",", Steps) + " mode=" + ClaudeMode;
}

public sealed class Watcher
{
    public bool FirstTick = true, ClaudeWas, WindowWas, NeedIdleSync, NeedFull;
    public DateTime HiddenAt = DateTime.MinValue, ClosedAt = DateTime.MinValue, LastPush = DateTime.MinValue, LastRelaunch = DateTime.MinValue, RetryAt = DateTime.MinValue, LastPresence = DateTime.MinValue;
    public string Mode = "auto";

    // ---- retours d'information du monde reel
    public void SyncDone(DateTime now, int code)
    {
        LastPush = now;
        if (code == 3 || code == 4 || code == 5) RetryAt = now.AddMinutes(2);       // injoignable, refuse, occupe : on reessaie dans 2 min
    }
    public void LaunchHandled(DateTime now, bool claudeRelaunched) { LastRelaunch = now; if (claudeRelaunched) ClaudeWas = true; }
    public void LaunchSkipped(DateTime now, bool retry) { if (retry) RetryAt = now.AddMinutes(2); }    // serveur injoignable a l'ouverture : Claude demarre seul
    public void SlowLaunch() { NeedFull = false; }                                                    // liaison lente : la reprise a lieu a la fermeture
    public void ClaudeKilledByUs() { ClaudeWas = false; }

    public WatchOutput Tick(WatchInput i)
    {
        var o = new WatchOutput { ClaudeMode = Mode };
        DateTime now = i.Now; bool running = i.Running;

        if (i.Handling || i.Paused) return o;                                                      // nous pilotons Claude, ou suspendu : on ne decide rien

        if (i.Busy)
        {   // une synchro tourne : on ne lance rien, mais on SUIT Claude (la presence doit etre juste pendant les longues synchros)
            // (l'OUVERTURE de Claude en pleine synchro n'est pas traitee ici : a la fin de la synchro, on la verra comme une ouverture normale)
            if (!running && ClaudeWas) { ClaudeWas = false; ClosedAt = now; NeedFull = true; o.Steps.Add(new WatchStep(WatchAct.Presence, false)); }
            return o;
        }

        if (FirstTick)
        {   // demarrage du programme : synchro tout de suite (Claude ouvert : envoi seulement)
            FirstTick = false; ClaudeWas = running; WindowWas = running && i.WindowVisible; NeedFull = false;
            ClosedAt = now.AddSeconds(-30); LastPush = now; LastPresence = now;
            Mode = running && !WindowWas ? "idle" : "auto"; o.ClaudeMode = Mode;
            o.Steps.Add(new WatchStep(WatchAct.SyncNow));
            return o;
        }

        if (running && !ClaudeWas)
        {   // Claude vient de s'ouvrir
            ClaudeWas = true;
            if (i.BlockLaunch && (now - LastRelaunch).TotalSeconds > 90) { WindowWas = true; o.Steps.Add(new WatchStep(WatchAct.Launch)); return o; }
        }
        else if (!running && ClaudeWas)
        {   // Claude vient de se fermer : synchro complete apres 5 s ; l'autre PC doit le savoir tout de suite
            ClaudeWas = false; ClosedAt = now; NeedFull = true;
            o.Steps.Add(new WatchStep(WatchAct.Presence, false));
        }

        if (running && ClaudeWas)
        {   // Claude tourne : sa fenetre est-elle encore visible ?
            bool win = i.WindowVisible;
            if (WindowWas && !win) { WindowWas = false; HiddenAt = now; NeedIdleSync = true; }
            else if (!WindowWas && win)
            {
                WindowWas = true; NeedIdleSync = false;
                if ((now - LastRelaunch).TotalSeconds > 60) { o.Steps.Add(new WatchStep(WatchAct.LaunchCheck)); return o; }
            }
            Mode = win ? "auto" : "idle";
            if (NeedIdleSync && (now - HiddenAt).TotalSeconds >= 5) { NeedIdleSync = false; LastPush = now; o.Steps.Add(new WatchStep(WatchAct.SyncNow)); }
        }
        else Mode = "auto";
        o.ClaudeMode = Mode;

        if (running)
        {
            if ((now - LastPresence).TotalSeconds >= 120) { LastPresence = now; o.Steps.Add(new WatchStep(WatchAct.Presence, WindowWas)); }
            if (i.Retryable && RetryAt != DateTime.MinValue && now >= RetryAt) { RetryAt = DateTime.MinValue; LastPush = now; o.Steps.Add(new WatchStep(WatchAct.SyncNow)); }
            else if ((now - LastPush).TotalMinutes >= 15) { LastPush = now; o.Steps.Add(new WatchStep(WatchAct.SyncNow)); }           // envoi seulement : le moteur voit que Claude tourne
        }
        else
        {
            bool due = NeedFull && (now - ClosedAt).TotalSeconds >= 5;
            bool retry = i.Retryable && RetryAt != DateTime.MinValue && now >= RetryAt;
            bool periodic = (now - LastPush).TotalMinutes >= 30;
            if (due || retry || periodic) { NeedFull = false; RetryAt = retry ? DateTime.MinValue : RetryAt; LastPush = now; o.Steps.Add(new WatchStep(WatchAct.SyncNow)); }
        }
        return o;
    }
}
