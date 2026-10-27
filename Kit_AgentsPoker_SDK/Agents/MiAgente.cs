using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// Plantilla de agente (verificada). Decide con equity Monte Carlo vs los rivales activos y pot odds,
/// y APRENDE de los rivales con IHandObserver.OnHandEnd (cartas mostradas en showdown + historial de acciones).
/// Copia este archivo, cambia el nombre de la clase (= nombre de tu agente) y mejóralo.
/// </summary>
public sealed class MiAgente : IPokerAgent, IHandObserver
{
    private const int PresupuestoMs = 600;          // margen bajo el límite de 1000 ms
    private const int MaxSimulaciones = 20000;
    private readonly Random _rng = new Random(12345); // semilla fija = partidas reproducibles (opcional)

    /// <summary>Lo que sabemos de cada rival (por nombre: el asiento puede cambiar entre mesas).</summary>
    private sealed class Perfil
    {
        public int Manos, ManosJugadas, Subidas, Acciones, Showdowns, ShowdownsDebiles;
        /// <summary>VPIP: % de manos en que puso fichas voluntariamente preflop.</summary>
        public double Vpip => Manos == 0 ? 0.25 : ManosJugadas / (double)Manos;
        /// <summary>Agresión: % de sus acciones que son bet/raise.</summary>
        public double Agresion => Acciones == 0 ? 0.3 : Subidas / (double)Acciones;
    }
    private readonly Dictionary<string, Perfil> _rivales = new Dictionary<string, Perfil>();

    public void Initialize(string agentName) { }

    public ActionType GetAction(PlayerObservation o)
    {
        try
        {
            var reloj = Stopwatch.StartNew();
            int rivales = Math.Max(1, o.ActivePlayerCount - 1);
            double equity = EstimarEquity(o, rivales, reloj);
            double potOdds = o.PotOdds;   // ToCall / (Pot + ToCall)

            // Ajuste por rivales: si quien apuesta es muy agresivo (farolea), pagamos un poco más ligero.
            double ajuste = 0;
            var agresor = UltimoAgresor(o);
            if (agresor != null && _rivales.TryGetValue(agresor, out var p) && p.Acciones >= 20 && p.Agresion > 0.45) ajuste = 0.05;

            if (o.CanRaise && equity > 0.5 + 0.1 * (rivales - 1) / 4.0) return ActionType.RAISE;
            if (o.ToCall == 0) return ActionType.CALL;       // check gratis: nunca foldear gratis
            if (equity + ajuste >= potOdds) return ActionType.CALL;
            return ActionType.FOLD;
        }
        catch
        {
            return ActionType.CALL; // respaldo: el motor lo convierte en check si es gratis; si hay que pagar, pagamos
        }
    }

    /// <summary>Se llama al final de CADA mano (también si foldeaste). Aquí se aprende de los rivales.</summary>
    public void OnHandEnd(HandSummary s)
    {
        for (int seat = 0; seat < s.PlayerNames.Count; seat++)
        {
            if (seat == s.MySeat || s.StacksBefore[seat] == 0) continue;
            var nombre = s.PlayerNames[seat];
            if (!_rivales.TryGetValue(nombre, out var p)) _rivales[nombre] = p = new Perfil();
            p.Manos++;
            bool jugo = false;
            foreach (var a in s.Actions)
            {
                if (a.Seat != seat || a.Kind == ActionKind.SmallBlind || a.Kind == ActionKind.BigBlind) continue;
                p.Acciones++;
                if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) p.Subidas++;
                if (a.Street == Street.Preflop && (a.Kind == ActionKind.Call || a.Kind == ActionKind.Raise)) jugo = true;
            }
            if (jugo) p.ManosJugadas++;

            // Cartas reveladas (solo si llegó al showdown): ¿llegó con poco? (ej. carta alta o pareja baja)
            var cartas = s.CardsOf(seat);
            if (cartas != null)
            {
                p.Showdowns++;
                if (HandEvaluator.GetCategory(s.HandValues[seat]) <= HandCategory.OnePair) p.ShowdownsDebiles++;
            }
        }
    }

    private static string UltimoAgresor(PlayerObservation o)
    {
        for (int i = o.ActionHistory.Count - 1; i >= 0; i--)
        {
            var a = o.ActionHistory[i];
            if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) return a.PlayerName;
        }
        return null;
    }

    private double EstimarEquity(PlayerObservation o, int rivales, Stopwatch reloj)
    {
        var usadas = new bool[Card.DeckSize];
        foreach (var c in o.HoleCards) usadas[c.Index] = true;
        foreach (var c in o.Board) usadas[c.Index] = true;
        var mazo = new int[Card.DeckSize];
        int n = 0;
        for (int i = 0; i < Card.DeckSize; i++) if (!usadas[i]) mazo[n++] = i;

        var mesa = new Card[5];
        for (int i = 0; i < o.Board.Count; i++) mesa[i] = o.Board[i];
        int faltan = 5 - o.Board.Count;
        int necesarias = faltan + 2 * rivales;

        double puntos = 0; int sims = 0;
        while (sims < MaxSimulaciones && reloj.ElapsedMilliseconds < PresupuestoMs)
        {
            for (int k = 0; k < necesarias; k++)
            {
                int j = k + _rng.Next(n - k);
                (mazo[k], mazo[j]) = (mazo[j], mazo[k]);
            }
            for (int k = 0; k < faltan; k++) mesa[o.Board.Count + k] = new Card(mazo[k]);

            int mio = HandEvaluator.Evaluate(o.HoleCards[0], o.HoleCards[1], mesa);
            int empates = 0;
            bool pierdo = false;
            for (int r = 0; r < rivales && !pierdo; r++)
            {
                int v = HandEvaluator.Evaluate(new Card(mazo[faltan + 2 * r]), new Card(mazo[faltan + 2 * r + 1]), mesa);
                if (v > mio) pierdo = true;
                else if (v == mio) empates++;
            }
            if (!pierdo) puntos += 1.0 / (1 + empates);
            sims++;
        }
        return sims == 0 ? 0.5 : puntos / sims;
    }
}
