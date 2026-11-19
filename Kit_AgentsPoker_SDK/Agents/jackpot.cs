using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// jackpot — agente de Limit Hold'em (6-max) para AgentsPoker SDK v0.3.0.
/// Decide con equity Monte Carlo contra RANGOS estimados de cada rival activo (no contra manos al azar):
///  - preflop: el rango de cada rival sale de su VPIP observado y de si ya subió o pagó en esta mano;
///    los rivales que aún no han hablado participan con probabilidad = su VPIP;
///  - postflop: si un rival apostó/subió, se descartan (con cierta probabilidad) sus manos sin pareja,
///    según lo agresivo que haya sido en manos anteriores.
/// Compara la equity con las pot odds (CALL/FOLD) y con un umbral que depende del número de rivales (RAISE),
/// hace apuestas de continuación selectivas y ajusta el riesgo al final de la mesa según el puesto (top N clasifica).
/// </summary>
public sealed class jackpot : IPokerAgent, IHandObserver
{
    // <AUTO-PARAMS>
    // Region autogenerada por el laboratorio de entrenamiento. No editar a mano.
    internal static readonly double[] ParametrosBase = new double[22]
    {
        -0.1970, 0.1113, 0.0572, 0.3996, 0.2478, -0.1242, 
        8.2535, 0.5072, -0.4239, -0.0791, -0.2715, 0.2160, 
        -0.3790, 400.2586, -0.2077, 0.7452, 0.3520, 0.4198, 
        -0.2356, 0.5116, 0.6256, 0.5591
    };
    // </AUTO-PARAMS>

    private const int PresupuestoMs = 150;          // corte del Monte Carlo (limite real: 1000 ms)
    private const int MaxSimulaciones = 6000;
    private const int IntentosRango = 25;           // muestreo por rechazo de la mano de cada rival
    private const int ManosPorMesa = 30;            // valor por defecto del torneo
    private const int Clasificados = 3;

    private readonly double[] _p;
    private readonly int _simulacionesFijas;

    private readonly Random _rng = new Random(20261027);
    private static readonly float[] _percentil = InicializarPercentiles(); // 0 = mejor mano inicial, 1 = peor
    private readonly Dictionary<string, Perfil> _rivales = new Dictionary<string, Perfil>(StringComparer.Ordinal);

    /// <summary>Estadisticas de un rival, acumuladas durante la mesa (clave: nombre).</summary>
    private sealed class Perfil
    {
        public int Manos, ManosJugadas, SubidasPreflop, Subidas, Acciones, Folds;
        public double Vpip(double priorVpip, double pesoPrior) => (ManosJugadas + priorVpip * pesoPrior) / (Manos + pesoPrior);
        public double Pfr(double priorVpip, double pesoPrior) => (SubidasPreflop + priorVpip * 0.5 * pesoPrior) / (Manos + pesoPrior);
        public double Agresion(double priorAgr, double pesoPrior) => (Subidas + priorAgr * pesoPrior) / (Acciones + pesoPrior);
        public double TasaFold(double priorFold, double pesoPrior) => (Folds + priorFold * pesoPrior) / (Acciones + pesoPrior);
    }

    /// <summary>Modelo de un rival activo para la decision actual.</summary>
    private struct Oponente
    {
        public double Participa;       // probabilidad de que siga en la mano (preflop, si aun no hablo)
        public double Rango;           // fraccion superior de manos iniciales que conserva (0..1)
        public double RechazoNada;     // prob. de descartar manos sin pareja en el board actual
        public double RechazoPareja;   // prob. de descartar manos de una sola pareja
    }

    public jackpot() : this(ParametrosBase, 0) { }

    internal jackpot(double[] parametros, int simulacionesFijas = 0)
    {
        _p = parametros ?? ParametrosBase;
        _simulacionesFijas = simulacionesFijas;
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
        for (int i = 0; i < ops.Count; i++) nEf += ops[i].Participa;
        nEf = Math.Max(1.0, nEf);

        double equity = EstimarEquity(o, ops, reloj);
        double ajuste = AjusteClasificacion(o);   // >0 = jugar mas apretado

        // RAISE / BET: equity claramente por encima de la parte justa del bote.
        double justa = 1.0 / (nEf + 1.0);
        double umbralSubir = justa + (o.ToCall == 0 ? _p[0] : _p[1])
                             + _p[2] * Math.Max(0, o.BetsThisStreet - 1) + ajuste;
        if (o.CanRaise && equity >= umbralSubir) return ActionType.RAISE;

        if (o.ToCall == 0)
        {
            if (o.CanRaise && DebeContinuar(o, ops)) return ActionType.RAISE;
            return ActionType.CALL; // check gratis: nunca foldear sin pagar
        }

        // CALL si la equity cubre las pot odds (con algo de odds implicitas en calles tempranas).
        double implicitas = o.Street == Street.Flop ? _p[8] : o.Street == Street.River ? _p[10] : _p[9];
        if (equity + implicitas - ajuste >= o.PotOdds) return ActionType.CALL;
        return ActionType.FOLD;
    }

    // ---------------------------------------------------------------- modelado de rivales

    private List<Oponente> ModelarOponentes(PlayerObservation o)
    {
        var lista = new List<Oponente>();
        bool preflop = o.Street == Street.Preflop;
        double priorVpip = _p[3];
        double priorAgr = _p[4];
        double pesoPrior = _p[6];

        for (int i = 0; i < o.Players.Count; i++)
        {
            var p = o.Players[i];
            if (p.Seat == o.Seat || !p.IsActive) continue;
            _rivales.TryGetValue(p.Name, out var perfil);
            double vpip = perfil != null ? perfil.Vpip(priorVpip, pesoPrior) : priorVpip;
            double pfr = perfil != null ? perfil.Pfr(priorVpip, pesoPrior) : priorVpip * 0.5;
            double agr = perfil != null ? perfil.Agresion(priorAgr, pesoPrior) : priorAgr;

            int subidasPre = 0, pagosPre = 0, agresionPost = 0;
            bool subioEnEstaCalle = false;
            for (int k = 0; k < o.ActionHistory.Count; k++)
            {
                var a = o.ActionHistory[k];
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
            if (subidasPre >= 2) op.Rango = Math.Max(_p[16], pfr * _p[15]);
            else if (subidasPre == 1) op.Rango = Math.Max(_p[17], pfr);
            else if (pagosPre > 0) op.Rango = Math.Max(_p[18], vpip);
            else
            {
                op.Rango = Math.Max(_p[19], vpip);
                // Aun no hablo voluntariamente: preflop puede foldear todavia.
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
                // Un rival pasivo que apuesta suele tener algo; un maniaco, no tanto.
                op.RechazoNada = Clamp(_p[20] - agr, 0.15, 0.8);
                if (agresionPost >= 2 || subioEnEstaCalle) op.RechazoPareja = Clamp(_p[21] - 0.5 * agr, 0.05, 0.4);
            }
            lista.Add(op);
        }
        return lista;
    }

    /// <summary>Apuesta de continuacion: fuimos el ultimo agresor preflop, flop, pocos rivales y rivales que foldean.</summary>
    private bool DebeContinuar(PlayerObservation o, List<Oponente> ops)
    {
        if (o.Street != Street.Flop || ops.Count == 0 || ops.Count > 2) return false;
        int ultimoAgresor = -1;
        for (int i = 0; i < o.ActionHistory.Count; i++)
        {
            var a = o.ActionHistory[i];
            if (a.Street == Street.Preflop && a.Kind == ActionKind.Raise) ultimoAgresor = a.Seat;
        }
        if (ultimoAgresor != o.Seat) return false;
        for (int i = 0; i < o.ActionHistory.Count; i++)
        {
            var a = o.ActionHistory[i];
            if (a.Street == Street.Flop && (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise)) return false;
        }

        double fold = 0;
        int n = 0;
        double priorFold = _p[5];
        double pesoPrior = _p[6];
        for (int i = 0; i < o.Players.Count; i++)
        {
            var p = o.Players[i];
            if (p.Seat == o.Seat || !p.IsActive) continue;
            fold += _rivales.TryGetValue(p.Name, out var perfil) ? perfil.TasaFold(priorFold, pesoPrior) : priorFold;
            n++;
        }
        if (n == 0 || fold / n < _p[14]) return false; // contra "calling stations" no se farolea
        return _rng.NextDouble() < _p[7];
    }

    /// <summary>
    /// Al final de la mesa importa el puesto, no las fichas: si vamos comodos en zona de clasificacion, mas apretado;
    /// si vamos fuera, un poco mas suelto.
    /// </summary>
    private double AjusteClasificacion(PlayerObservation o)
    {
        int restantes = ManosPorMesa - o.HandNumber;
        if (restantes > 6) return 0;

        int miStack = o.MyStack + o.MyCommittedThisHand;
        int vivos = 0, mejores = 0;
        var otros = new List<int>();
        for (int i = 0; i < o.Players.Count; i++)
        {
            var p = o.Players[i];
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
            // Margen sobre el primero que quedaria fuera.
            otros.Sort();
            otros.Reverse();
            int fuera = otros.Count > clasifican - 1 ? otros[clasifican - 1] : 0;
            return miStack - fuera > _p[13] ? _p[11] : 0.0;
        }
        return _p[12];
    }

    // ---------------------------------------------------------------- Monte Carlo

    private double EstimarEquity(PlayerObservation o, List<Oponente> ops, Stopwatch reloj)
    {
        var usadas = new bool[Card.DeckSize];
        for (int i = 0; i < o.HoleCards.Count; i++) usadas[o.HoleCards[i].Index] = true;
        for (int i = 0; i < o.Board.Count; i++) usadas[o.Board[i].Index] = true;
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
        int maxSims = _simulacionesFijas > 0 ? _simulacionesFijas : MaxSimulaciones;

        while (sims < maxSims && (_simulacionesFijas > 0 || reloj.ElapsedMilliseconds < PresupuestoMs))
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

    /// <summary>Fisher-Yates parcial: lleva una carta aleatoria del resto del mazo a la posicion pos.</summary>
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

    /// <summary>Ordena las 1326 combinaciones por la formula de Chen y guarda su percentil (0 = AA).</summary>
    private static float[] InicializarPercentiles()
    {
        var percentil = new float[Card.DeckSize * Card.DeckSize];
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
                percentil[a * Card.DeckSize + b] = pct;
                percentil[b * Card.DeckSize + a] = pct;
            }
            i = j;
        }
        return percentil;
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
