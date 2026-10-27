using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// jackpot_v1 — agente de Limit Hold'em (6-max) para AgentsPoker SDK v0.3.0.
/// Decide con equity Monte Carlo contra RANGOS estimados de cada rival activo (no contra manos al azar):
///  - preflop: el rango de cada rival sale de su VPIP observado y de si ya subió o pagó en esta mano;
///    los rivales que aún no han hablado participan con probabilidad = su VPIP;
///  - postflop: si un rival apostó/subió, se descartan (con cierta probabilidad) sus manos sin pareja,
///    según lo agresivo que haya sido en manos anteriores.
/// Compara la equity con las pot odds (CALL/FOLD) y con un umbral que depende del número de rivales (RAISE),
/// hace apuestas de continuación selectivas y ajusta el riesgo al final de la mesa según el puesto (top N clasifica).
/// </summary>
public sealed class jackpot_v1 : IPokerAgent, IHandObserver
{
    // ---- Parámetros (constantes: se pueden ajustar con el banco de pruebas) ----
    private const int PresupuestoMs = 150;          // corte del Monte Carlo (límite real: 1000 ms)
    private const int MaxSimulaciones = 6000;
    private const int IntentosRango = 25;           // muestreo por rechazo de la mano de cada rival
    private const double MargenApostar = 0.08;      // equity extra sobre 1/(n+1) para BET (nadie apostó)
    private const double MargenSubir = 0.14;        // equity extra para RAISE cuando nos apuestan
    private const double MargenPorApuesta = 0.05;   // más exigencia en guerras de subidas (3-bet, 4-bet)
    private const double PriorVpip = 0.35;          // suposición inicial sobre un rival desconocido
    private const double PriorAgresion = 0.30;
    private const double PriorFold = 0.30;
    private const int PesoPrior = 8;                // manos "ficticias" del prior (suaviza las estadísticas)
    private const double ProbCbet = 0.6;
    private const int ManosPorMesa = 30;            // valor por defecto del torneo (el árbitro puede cambiarlo)
    private const int Clasificados = 3;

    private readonly Random _rng = new Random(20261027);
    private readonly float[] _percentil = new float[Card.DeckSize * Card.DeckSize]; // 0 = mejor mano inicial, 1 = peor
    private readonly Dictionary<string, Perfil> _rivales = new Dictionary<string, Perfil>();

    /// <summary>Estadísticas de un rival, acumuladas durante la mesa (clave: nombre).</summary>
    private sealed class Perfil
    {
        public int Manos, ManosJugadas, SubidasPreflop, Subidas, Acciones, Folds;
        public double Vpip => (ManosJugadas + PriorVpip * PesoPrior) / (Manos + PesoPrior);
        public double Pfr => (SubidasPreflop + PriorVpip * 0.5 * PesoPrior) / (Manos + PesoPrior);
        public double Agresion => (Subidas + PriorAgresion * PesoPrior) / (Acciones + PesoPrior);
        public double TasaFold => (Folds + PriorFold * PesoPrior) / (Acciones + PesoPrior);
    }

    /// <summary>Modelo de un rival activo para la decisión actual.</summary>
    private struct Oponente
    {
        public double Participa;       // probabilidad de que siga en la mano (preflop, si aún no habló)
        public double Rango;           // fracción superior de manos iniciales que conserva (0..1)
        public double RechazoNada;     // prob. de descartar manos sin pareja en el board actual
        public double RechazoPareja;   // prob. de descartar manos de una sola pareja
    }

    public jackpot_v1()
    {
        CalcularPercentiles();
    }

    public void Initialize(string agentName) { }

    public ActionType GetAction(PlayerObservation o)
    {
        try
        {
            return Decidir(o);
        }
        catch
        {
            return o.ToCall == 0 ? ActionType.CALL : ActionType.FOLD; // respaldo legal y seguro
        }
    }

    private ActionType Decidir(PlayerObservation o)
    {
        var reloj = Stopwatch.StartNew();
        var ops = ModelarOponentes(o);
        double nEf = 0;
        foreach (var op in ops) nEf += op.Participa;
        nEf = Math.Max(1.0, nEf);

        double equity = EstimarEquity(o, ops, reloj);
        double ajuste = AjusteClasificacion(o);   // >0 = jugar más apretado

        // RAISE / BET: equity claramente por encima de la parte justa del bote.
        double justa = 1.0 / (nEf + 1.0);
        double umbralSubir = justa + (o.ToCall == 0 ? MargenApostar : MargenSubir)
                             + MargenPorApuesta * Math.Max(0, o.BetsThisStreet - 1) + ajuste;
        if (o.CanRaise && equity >= umbralSubir) return ActionType.RAISE;

        if (o.ToCall == 0)
        {
            if (o.CanRaise && DebeContinuar(o, ops)) return ActionType.RAISE;
            return ActionType.CALL; // check gratis: nunca foldear sin pagar
        }

        // CALL si la equity cubre las pot odds (con algo de odds implícitas en calles tempranas).
        double implicitas = o.Street == Street.Flop ? 0.04 : o.Street == Street.River ? 0.0 : 0.02;
        if (equity + implicitas - ajuste >= o.PotOdds) return ActionType.CALL;
        return ActionType.FOLD;
    }

    // ---------------------------------------------------------------- modelado de rivales

    private List<Oponente> ModelarOponentes(PlayerObservation o)
    {
        var lista = new List<Oponente>();
        bool preflop = o.Street == Street.Preflop;
        foreach (var p in o.Players)
        {
            if (p.Seat == o.Seat || !p.IsActive) continue;
            _rivales.TryGetValue(p.Name, out var perfil);
            double vpip = perfil?.Vpip ?? PriorVpip;
            double pfr = perfil?.Pfr ?? PriorVpip * 0.5;
            double agr = perfil?.Agresion ?? PriorAgresion;

            int subidasPre = 0, pagosPre = 0, agresionPost = 0;
            bool subioEnEstaCalle = false;
            foreach (var a in o.ActionHistory)
            {
                if (a.Seat != p.Seat) continue;
                bool agresiva = a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise;
                if (a.Street == Street.Preflop)
                {
                    if (a.Kind == ActionKind.Raise) subidasPre++;
                    else if (a.Kind == ActionKind.Call) pagosPre++;
                }
                else if (agresiva)
                {
                    agresionPost++;
                    if (a.Street == o.Street && a.Kind == ActionKind.Raise) subioEnEstaCalle = true;
                }
            }

            var op = new Oponente { Participa = 1.0 };
            if (subidasPre >= 2) op.Rango = Math.Max(0.05, pfr * 0.5);
            else if (subidasPre == 1) op.Rango = Math.Max(0.08, pfr);
            else if (pagosPre > 0) op.Rango = Math.Max(0.15, vpip);
            else
            {
                op.Rango = Math.Max(0.20, vpip);
                // Aún no habló voluntariamente: preflop puede foldear todavía.
                if (preflop)
                {
                    bool ciegaGrande = p.Seat == o.BigBlindSeat;
                    op.Participa = Math.Min(1.0, vpip + (ciegaGrande ? 0.15 : 0.0));
                    if (ciegaGrande && o.CurrentBet <= o.BigBlind) op.Participa = 1.0; // check gratis
                }
            }
            op.Rango = Math.Min(1.0, op.Rango);

            if (agresionPost > 0)
            {
                // Un rival pasivo que apuesta suele tener algo; un maníaco, no tanto.
                op.RechazoNada = Clamp(0.85 - agr, 0.15, 0.8);
                if (agresionPost >= 2 || subioEnEstaCalle) op.RechazoPareja = Clamp(0.5 - 0.5 * agr, 0.05, 0.4);
            }
            lista.Add(op);
        }
        return lista;
    }

    /// <summary>Apuesta de continuación: fuimos el último agresor preflop, flop, pocos rivales y rivales que foldean.</summary>
    private bool DebeContinuar(PlayerObservation o, List<Oponente> ops)
    {
        if (o.Street != Street.Flop || ops.Count == 0 || ops.Count > 2) return false;
        int ultimoAgresor = -1;
        foreach (var a in o.ActionHistory)
            if (a.Street == Street.Preflop && a.Kind == ActionKind.Raise) ultimoAgresor = a.Seat;
        if (ultimoAgresor != o.Seat) return false;
        foreach (var a in o.ActionHistory)
            if (a.Street == Street.Flop && (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise)) return false;

        double fold = 0;
        int n = 0;
        foreach (var p in o.Players)
        {
            if (p.Seat == o.Seat || !p.IsActive) continue;
            fold += _rivales.TryGetValue(p.Name, out var perfil) ? perfil.TasaFold : PriorFold;
            n++;
        }
        if (n == 0 || fold / n < 0.25) return false; // contra "calling stations" no se farolea
        return _rng.NextDouble() < ProbCbet;
    }

    /// <summary>
    /// Al final de la mesa importa el puesto, no las fichas: si vamos cómodos en zona de clasificación, más apretado;
    /// si vamos fuera, un poco más suelto.
    /// </summary>
    private double AjusteClasificacion(PlayerObservation o)
    {
        int restantes = ManosPorMesa - o.HandNumber;
        if (restantes > 6) return 0;

        int miStack = o.MyStack + o.MyCommittedThisHand;
        int vivos = 0, mejores = 0;
        var otros = new List<int>();
        foreach (var p in o.Players)
        {
            int s = p.Stack + p.CommittedThisHand;
            if (s <= 0) continue;
            vivos++;
            if (p.Seat == o.Seat) continue;
            otros.Add(s);
            if (s > miStack) mejores++;
        }
        int clasifican = Math.Min(Clasificados, vivos - 1);
        if (clasifican <= 0) return 0;

        if (mejores < clasifican)
        {
            // Margen sobre el primero que quedaría fuera.
            otros.Sort();
            otros.Reverse();
            int fuera = otros.Count > clasifican - 1 ? otros[clasifican - 1] : 0;
            return miStack - fuera > 400 ? 0.05 : 0.0;
        }
        return -0.03;
    }

    // ---------------------------------------------------------------- Monte Carlo

    private double EstimarEquity(PlayerObservation o, List<Oponente> ops, Stopwatch reloj)
    {
        var usadas = new bool[Card.DeckSize];
        foreach (var c in o.HoleCards) usadas[c.Index] = true;
        foreach (var c in o.Board) usadas[c.Index] = true;
        var mazo = new int[Card.DeckSize];
        int n = 0;
        for (int i = 0; i < Card.DeckSize; i++) if (!usadas[i]) mazo[n++] = i;

        var mesa = new Card[5];
        int enMesa = o.Board.Count;
        for (int i = 0; i < enMesa; i++) mesa[i] = o.Board[i];
        Card h0 = o.HoleCards[0], h1 = o.HoleCards[1];

        int nOps = ops.Count;
        var c1 = new int[nOps];
        var c2 = new int[nOps];
        var juega = new bool[nOps];

        double puntos = 0;
        int sims = 0;
        while (sims < MaxSimulaciones && reloj.ElapsedMilliseconds < PresupuestoMs)
        {
            int pos = 0;
            for (int r = 0; r < nOps; r++)
            {
                var op = ops[r];
                juega[r] = op.Participa >= 1.0 || _rng.NextDouble() < op.Participa;
                if (!juega[r]) continue;
                for (int intento = 0; ; intento++)
                {
                    Sacar(mazo, n, pos);
                    Sacar(mazo, n, pos + 1);
                    if (intento >= IntentosRango || Acepta(op, mazo[pos], mazo[pos + 1], o.Board)) break;
                }
                c1[r] = mazo[pos];
                c2[r] = mazo[pos + 1];
                pos += 2;
            }
            for (int k = enMesa; k < 5; k++)
            {
                Sacar(mazo, n, pos);
                mesa[k] = new Card(mazo[pos++]);
            }

            int mio = HandEvaluator.Evaluate(h0, h1, mesa);
            int empates = 0;
            bool pierdo = false;
            for (int r = 0; r < nOps && !pierdo; r++)
            {
                if (!juega[r]) continue;
                int v = HandEvaluator.Evaluate(new Card(c1[r]), new Card(c2[r]), mesa);
                if (v > mio) pierdo = true;
                else if (v == mio) empates++;
            }
            if (!pierdo) puntos += 1.0 / (1 + empates);
            sims++;
        }
        return sims == 0 ? 0.5 : puntos / sims;
    }

    /// <summary>Fisher-Yates parcial: lleva una carta aleatoria del resto del mazo a la posición pos.</summary>
    private void Sacar(int[] mazo, int n, int pos)
    {
        int j = pos + _rng.Next(n - pos);
        int t = mazo[pos];
        mazo[pos] = mazo[j];
        mazo[j] = t;
    }

    private bool Acepta(Oponente op, int a, int b, IReadOnlyList<Card> board)
    {
        if (op.Rango < 1.0 && _percentil[a * Card.DeckSize + b] > op.Rango) return false;
        if (board.Count >= 3 && (op.RechazoNada > 0 || op.RechazoPareja > 0))
        {
            var cat = HandEvaluator.GetCategory(HandEvaluator.Evaluate(new Card(a), new Card(b), board));
            if (cat == HandCategory.HighCard && _rng.NextDouble() < op.RechazoNada) return false;
            if (cat == HandCategory.OnePair && _rng.NextDouble() < op.RechazoPareja) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- tabla de manos iniciales

    /// <summary>Ordena las 1326 combinaciones por la fórmula de Chen y guarda su percentil (0 = AA).</summary>
    private void CalcularPercentiles()
    {
        var combos = new List<KeyValuePair<double, int>>(1326);
        for (int a = 0; a < Card.DeckSize; a++)
            for (int b = a + 1; b < Card.DeckSize; b++)
            {
                var ca = new Card(a);
                var cb = new Card(b);
                combos.Add(new KeyValuePair<double, int>(Chen(ca.Rank, cb.Rank, ca.Suit == cb.Suit), a * Card.DeckSize + b));
            }
        combos.Sort((x, y) => y.Key.CompareTo(x.Key));
        int i = 0;
        while (i < combos.Count)
        {
            int j = i;
            while (j < combos.Count && combos[j].Key == combos[i].Key) j++;
            float pct = (float)((i + j) / 2.0 / combos.Count);
            for (int k = i; k < j; k++)
            {
                int idx = combos[k].Value;
                int a = idx / Card.DeckSize, b = idx % Card.DeckSize;
                _percentil[a * Card.DeckSize + b] = pct;
                _percentil[b * Card.DeckSize + a] = pct;
            }
            i = j;
        }
    }

    private static double Chen(int r1, int r2, bool mismoPalo)
    {
        int alta = Math.Max(r1, r2), baja = Math.Min(r1, r2);
        double puntos = alta == 14 ? 10 : alta == 13 ? 8 : alta == 12 ? 7 : alta == 11 ? 6 : alta / 2.0;
        if (r1 == r2) return Math.Max(5, puntos * 2);
        if (mismoPalo) puntos += 2;
        int hueco = alta - baja - 1;
        puntos -= hueco == 0 ? 0 : hueco == 1 ? 1 : hueco == 2 ? 2 : hueco == 3 ? 4 : 5;
        if (hueco <= 1 && alta < 12) puntos += 1;
        return puntos;
    }

    private static double Clamp(double x, double min, double max) => x < min ? min : x > max ? max : x;

    // ---------------------------------------------------------------- aprendizaje entre manos

    /// <summary>Se llama al final de cada mano: solo actualiza contadores (rápido).</summary>
    public void OnHandEnd(HandSummary s)
    {
        for (int seat = 0; seat < s.PlayerNames.Count; seat++)
        {
            if (seat == s.MySeat || s.StacksBefore[seat] == 0) continue;
            var nombre = s.PlayerNames[seat];
            if (!_rivales.TryGetValue(nombre, out var p)) _rivales[nombre] = p = new Perfil();
            p.Manos++;
            bool jugo = false, subioPre = false;
            foreach (var a in s.Actions)
            {
                if (a.Seat != seat || a.Kind == ActionKind.SmallBlind || a.Kind == ActionKind.BigBlind) continue;
                p.Acciones++;
                if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) p.Subidas++;
                if (a.Kind == ActionKind.Fold) p.Folds++;
                if (a.Street == Street.Preflop && (a.Kind == ActionKind.Call || a.Kind == ActionKind.Raise)) jugo = true;
                if (a.Street == Street.Preflop && a.Kind == ActionKind.Raise) subioPre = true;
            }
            if (jugo) p.ManosJugadas++;
            if (subioPre) p.SubidasPreflop++;
        }
    }
}
