using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// jackpot — la v1 + planificación online por rollouts (Sutton y Barto 8.10, clase 12).
/// En cada decisión: la v1 propone una acción; luego se simulan miles de veces el resto de la mano para cada acción legal
/// (determinización: manos de los rivales muestreadas de sus rangos, board futuro al azar; todos juegan una política
/// rápida ajustada a su perfil observado). Las acciones se comparan con números aleatorios comunes (misma mano simulada
/// para cada acción) y solo se cambia la acción de la v1 si otra es mejor por más de ZCambio errores estándar
/// (mejora de política conservadora). Utilidad = fichas; en las últimas manos de la mesa, clasificar (top N).
///
/// jackpot (v1) — agente de Limit Hold'em (6-max) para AgentsPoker SDK v0.3.0.
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

    public jackpot()
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
        ActionType v1 = DecidirV1(o, reloj);
        if (o.Players.Count > MaxAsientos || o.HoleCards.Count < 2) return v1;
        return Planificar(o, v1, reloj);
    }

    private ActionType DecidirV1(PlayerObservation o, Stopwatch reloj)
    {
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

    // ---------------------------------------------------------------- planificación online (rollouts)

    private const int MaxAsientos = 10;
    private const int FinRolloutsMs = 200;          // corte de las simulaciones (la v1 ya decidió antes)
    private const int MaxRollouts = 4000;           // determinizaciones; cada una se juega una vez por acción
    private const int TopeDuroMs = 250;             // nunca simular más allá de esto, aunque falten simulaciones
    private const int MinRollouts = 150;
    private const double ZCambio = 1.5;             // ventaja mínima (en errores estándar) para cambiar la acción de la v1
    private const int ManosModoClasificar = 3;      // en las últimas manos la utilidad es clasificar (1/0)

    // Estado reutilizable de la simulación (por instancia: sin estado compartido).
    private readonly int[] _sc0 = new int[MaxAsientos], _sc1 = new int[MaxAsientos];
    private readonly bool[] _sActivo = new bool[MaxAsientos], _sPend = new bool[MaxAsientos];
    private readonly int[] _sCalle = new int[MaxAsientos], _sMano = new int[MaxAsientos], _sGana = new int[MaxAsientos];
    private readonly double[] _sFuerza = new double[MaxAsientos];
    private readonly double[] _pAgr = new double[MaxAsientos], _pFold = new double[MaxAsientos], _pVpip = new double[MaxAsientos];
    private readonly bool[] _rangoLibre = new bool[MaxAsientos];
    private readonly Card[] _sBoard = new Card[5], _sB3 = new Card[3], _sB4 = new Card[4];
    private readonly int[] _base = new int[MaxAsientos];
    private uint _semillaSim;

    /// <summary>Generador xorshift: cada determinización usa la misma semilla para todas las acciones (CRN).</summary>
    private double Azar()
    {
        uint x = _semillaSim;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _semillaSim = x;
        return (x >> 8) * (1.0 / 16777216.0);
    }

    private const int MaxMesaV1 = 4;
    private ActionType Planificar(PlayerObservation o, ActionType v1, Stopwatch reloj)
    {
        int n = o.Players.Count, yo = o.Seat;
        if (n <= MaxMesaV1) return v1; // mesas chicas: el modelo de rivales falla, decide la v1
        var acciones = new List<ActionType> { ActionType.CALL };
        if (o.ToCall > 0) acciones.Add(ActionType.FOLD);
        if (o.CanRaise) acciones.Add(ActionType.RAISE);
        if (v1 == ActionType.RAISE && !o.CanRaise) v1 = ActionType.CALL;
        if (v1 == ActionType.FOLD && o.ToCall == 0) v1 = ActionType.CALL;
        if (acciones.Count < 2) return v1;

        // Perfiles de la política rápida, rangos y stacks al inicio de la mano.
        var ops = ModelarOponentes(o);
        var opDe = new int[n];
        int k = 0;
        for (int s = 0; s < n; s++)
        {
            var p = o.Players[s];
            _base[s] = p.Stack + p.CommittedThisHand;
            opDe[s] = -1;
            if (s == yo) { _pAgr[s] = 0.30; _pFold[s] = 0.35; _pVpip[s] = 0.38; continue; }
            if (!p.IsActive) continue;
            opDe[s] = k++;
            _rivales.TryGetValue(p.Name, out var perfil);
            _pAgr[s] = perfil?.Agresion ?? PriorAgresion;
            _pFold[s] = perfil?.TasaFold ?? PriorFold;
            _pVpip[s] = perfil?.Vpip ?? PriorVpip;
        }
        // Quién ya habló voluntariamente en esta calle (los que no, aún deben actuar).
        var hablo = new bool[n];
        foreach (var a in o.ActionHistory)
            if (a.Street == o.Street && a.Kind != ActionKind.SmallBlind && a.Kind != ActionKind.BigBlind) hablo[a.Seat] = true;
        for (int s = 0; s < n; s++) _rangoLibre[s] = o.Street == Street.Preflop && !hablo[s]; // aún puede foldear: rango completo

        bool clasificar = ManosPorMesa - o.HandNumber < ManosModoClasificar;
        int nA = acciones.Count;
        var suma = new double[nA];
        var dif = new double[nA];   // suma de (u_a - u_v1) y de su cuadrado: comparación pareada
        var dif2 = new double[nA];
        int iV1 = acciones.IndexOf(v1);
        if (iV1 < 0) return v1;

        var usadas = new bool[Card.DeckSize];
        foreach (var c in o.HoleCards) usadas[c.Index] = true;
        foreach (var c in o.Board) usadas[c.Index] = true;
        var mazo = new int[Card.DeckSize];
        int nMazo = 0;
        for (int i = 0; i < Card.DeckSize; i++) if (!usadas[i]) mazo[nMazo++] = i;
        var u = new double[nA];
        var tablero = new int[5];

        int sims = 0;
        while (sims < MaxRollouts && reloj.ElapsedMilliseconds < TopeDuroMs && (sims < MinRollouts || reloj.ElapsedMilliseconds < FinRolloutsMs))
        {
            // Determinización: manos de los rivales según su rango y board completo.
            int pos = 0;
            for (int s = 0; s < n; s++)
            {
                if (s == yo || opDe[s] < 0) continue;
                var op = ops[opDe[s]];
                if (_rangoLibre[s]) op.Rango = 1.0;
                for (int intento = 0; ; intento++)
                {
                    Sacar(mazo, nMazo, pos);
                    Sacar(mazo, nMazo, pos + 1);
                    if (intento >= IntentosRango || Acepta(op, mazo[pos], mazo[pos + 1], o.Board)) break;
                }
                _sc0[s] = mazo[pos]; _sc1[s] = mazo[pos + 1];
                pos += 2;
            }
            for (int i = 0; i < 5; i++) tablero[i] = i < o.Board.Count ? o.Board[i].Index : mazo[pos++];
            uint semilla = (uint)_rng.Next(1, int.MaxValue);

            for (int a = 0; a < nA; a++)
            {
                _semillaSim = semilla;
                u[a] = Rollout(o, acciones[a], tablero, clasificar);
            }
            for (int a = 0; a < nA; a++)
            {
                suma[a] += u[a];
                double d = u[a] - u[iV1];
                dif[a] += d; dif2[a] += d * d;
            }
            sims++;
        }

        if (sims < MinRollouts) return v1; // pocas simulaciones: no hay evidencia, decide la v1
        // La mejor acción solo reemplaza a la de la v1 si su ventaja pareada es clara.
        int mejor = iV1;
        double mejorZ = ZCambio;
        for (int a = 0; a < nA; a++)
        {
            if (a == iV1) continue;
            double m = dif[a] / sims;
            double se = Math.Sqrt(Math.Max(1e-12, dif2[a] / sims - m * m) / sims);
            double z = m / se;
            if (m > 0 && z > mejorZ) { mejorZ = z; mejor = a; }
        }
        return acciones[mejor];
    }

    /// <summary>Juega el resto de la mano desde la observación actual, empezando por nuestra acción <paramref name="primera"/>.</summary>
    private double Rollout(PlayerObservation o, ActionType primera, int[] tablero, bool clasificar)
    {
        int n = o.Players.Count, yo = o.Seat;
        _sc0[yo] = o.HoleCards[0].Index; _sc1[yo] = o.HoleCards[1].Index;
        int curBet = 0, bote = 0;
        for (int s = 0; s < n; s++)
        {
            var p = o.Players[s];
            _sActivo[s] = p.IsActive && !p.HasFolded;
            _sCalle[s] = p.CommittedThisStreet;
            _sMano[s] = p.CommittedThisHand;
            _sGana[s] = 0;
            bote += p.CommittedThisHand;
            if (p.CommittedThisStreet > curBet) curBet = p.CommittedThisStreet;
        }
        int calle = (int)o.Street;
        int apuestas = o.BetsThisStreet;
        int tope = o.MaxBetsPerStreet;
        int bb = o.BigBlind;
        int nBoard = o.Board.Count;
        for (int i = 0; i < 5; i++) _sBoard[i] = new Card(tablero[i]);
        for (int i = 0; i < 3; i++) _sB3[i] = _sBoard[i];
        for (int i = 0; i < 4; i++) _sB4[i] = _sBoard[i];

        // Pendientes en la calle actual: activos que no han igualado o no han hablado (nosotros actuamos ya).
        var hablo = new bool[n];
        foreach (var a in o.ActionHistory)
            if (a.Street == o.Street && a.Kind != ActionKind.SmallBlind && a.Kind != ActionKind.BigBlind) hablo[a.Seat] = true;
        for (int s = 0; s < n; s++) _sPend[s] = _sActivo[s] && s != yo && !o.Players[s].IsAllIn && (_sCalle[s] < curBet || !hablo[s]);
        CalcularFuerzas(n, calle, nBoard);

        int apuestaCalle = calle >= 2 ? 2 * bb : bb;
        // Nuestra primera acción.
        Aplicar(yo, primera, ref curBet, ref apuestas, ref bote, tope, apuestaCalle, n);
        int turno = (yo + 1) % n;

        while (true)
        {
            // Ronda de apuestas de la calle actual.
            int guardia = 0;
            while (Contar(_sActivo, n) > 1 && HayPendientes(n) && guardia++ < 64)
            {
                int s = turno;
                turno = (turno + 1) % n;
                if (!_sActivo[s] || !_sPend[s]) continue;
                var acc = Politica(s, curBet - _sCalle[s], apuestas < tope, calle, Contar(_sActivo, n) - 1);
                Aplicar(s, acc, ref curBet, ref apuestas, ref bote, tope, apuestaCalle, n);
            }
            if (Contar(_sActivo, n) <= 1 || calle == 3) break;

            // Siguiente calle.
            calle++;
            nBoard = calle == 1 ? 3 : calle == 2 ? 4 : 5;
            apuestaCalle = calle >= 2 ? 2 * bb : bb;
            curBet = 0; apuestas = 0;
            for (int s = 0; s < n; s++) { _sCalle[s] = 0; _sPend[s] = _sActivo[s] && !o.Players[s].IsAllIn; }
            CalcularFuerzas(n, calle, nBoard);
            turno = (o.ButtonSeat + 1) % n;
        }

        // Reparto del bote.
        int vivos = Contar(_sActivo, n);
        if (vivos == 1)
        {
            for (int s = 0; s < n; s++) if (_sActivo[s]) _sGana[s] = bote;
        }
        else
        {
            int mejor = int.MinValue, empatados = 0;
            var valor = new int[n];
            for (int s = 0; s < n; s++)
            {
                if (!_sActivo[s]) continue;
                valor[s] = HandEvaluator.Evaluate(new Card(_sc0[s]), new Card(_sc1[s]), _sBoard);
                if (valor[s] > mejor) { mejor = valor[s]; empatados = 1; }
                else if (valor[s] == mejor) empatados++;
            }
            for (int s = 0; s < n; s++) if (_sActivo[s] && valor[s] == mejor) _sGana[s] = bote / empatados;
        }

        double neto = _sGana[yo] - _sMano[yo];
        if (!clasificar) return neto / bb;

        // Utilidad de torneo: ¿quedamos dentro del top N con los stacks tras esta mano?
        int mio = _base[yo] - _sMano[yo] + _sGana[yo];
        int conFichas = 0, mejores = 0;
        for (int s = 0; s < n; s++)
        {
            if (_base[s] <= 0) continue;
            int fin = _base[s] - _sMano[s] + _sGana[s];
            if (fin > 0) conFichas++;
            if (s != yo && fin > mio) mejores++;
        }
        int cupos = Math.Min(Clasificados, Math.Max(1, conFichas - 1));
        return (mejores < cupos ? 1.0 : 0.0) + 0.001 * neto / bb;
    }

    private void Aplicar(int s, ActionType acc, ref int curBet, ref int apuestas, ref int bote, int tope, int apuestaCalle, int n)
    {
        int debe = curBet - _sCalle[s];
        if (acc == ActionType.FOLD && debe <= 0) acc = ActionType.CALL;
        if (acc == ActionType.RAISE && apuestas >= tope) acc = ActionType.CALL;
        _sPend[s] = false;
        if (acc == ActionType.FOLD) { _sActivo[s] = false; return; }
        int pone = debe;
        if (acc == ActionType.RAISE)
        {
            curBet += apuestaCalle;
            pone = curBet - _sCalle[s];
            apuestas++;
            for (int t = 0; t < n; t++) if (t != s && _sActivo[t]) _sPend[t] = true;
        }
        _sCalle[s] += pone; _sMano[s] += pone; bote += pone;
    }

    /// <summary>Política rápida para todos en la simulación: fuerza de la mano + perfil del jugador (agresión, folds, VPIP).</summary>
    private ActionType Politica(int s, int debe, bool puedeSubir, int calle, int rivales)
    {
        double f = _sFuerza[s];
        double agr = _pAgr[s], fold = _pFold[s];
        if (calle == 0)
        {
            double juega = 1.0 - _pVpip[s];               // percentil de corte según su VPIP
            if (debe > 0 && f < juega) return ActionType.FOLD;
            if (puedeSubir && f > 1.0 - 0.5 * agr * (1 + 0.5 * (rivales > 2 ? 0 : 1))) return ActionType.RAISE;
            return ActionType.CALL;
        }
        if (debe > 0)
        {
            double corte = Clamp(0.25 + 0.6 * (fold - 0.3), 0.05, 0.6);
            if (f < corte && Azar() > 0.5 * agr) return ActionType.FOLD;
            if (puedeSubir && (f > 0.78 - 0.3 * (agr - 0.3) || Azar() < 0.08 * agr)) return ActionType.RAISE;
            return ActionType.CALL;
        }
        if (puedeSubir && (f > 0.55 - 0.4 * (agr - 0.3) || Azar() < 0.3 * agr)) return ActionType.RAISE;
        return ActionType.CALL;
    }

    /// <summary>Fuerza de cada jugador activo en la calle (0..1): preflop por percentil; postflop por categoría + proyectos.</summary>
    private void CalcularFuerzas(int n, int calle, int nBoard)
    {
        for (int s = 0; s < n; s++)
        {
            if (!_sActivo[s]) continue;
            int a = _sc0[s], b = _sc1[s];
            if (calle == 0) { _sFuerza[s] = 1.0 - _percentil[a * Card.DeckSize + b]; continue; }
            var c0 = new Card(a); var c1 = new Card(b);
            var cat = HandEvaluator.GetCategory(HandEvaluator.Evaluate(c0, c1, nBoard == 3 ? _sB3 : nBoard == 4 ? _sB4 : _sBoard));
            double f;
            switch (cat)
            {
                case HandCategory.HighCard: f = 0.10; break;
                case HandCategory.OnePair: f = PesoPareja(c0, c1, nBoard); break;
                case HandCategory.TwoPair: f = 0.72; break;
                case HandCategory.ThreeOfAKind: f = 0.82; break;
                default: f = 0.92; break;
            }
            if (calle < 3 && cat <= HandCategory.OnePair && ProyectoColor(c0, c1, nBoard)) f += 0.20;
            _sFuerza[s] = Math.Min(0.99, f + 0.05 * (Azar() - 0.5));
        }
    }

    /// <summary>Pareja: más valor si usa una carta propia alta (top pair / overpair) que si es del board.</summary>
    private double PesoPareja(Card c0, Card c1, int nBoard)
    {
        int alta = 0;
        for (int i = 0; i < nBoard; i++) if (_sBoard[i].Rank > alta) alta = _sBoard[i].Rank;
        if (c0.Rank == c1.Rank) return c0.Rank > alta ? 0.66 : 0.40;
        for (int i = 0; i < nBoard; i++)
        {
            int r = _sBoard[i].Rank;
            if (r == c0.Rank || r == c1.Rank) return r == alta ? 0.58 : 0.40;
        }
        return 0.25; // la pareja está en el board
    }

    private bool ProyectoColor(Card c0, Card c1, int nBoard)
    {
        int palo = (int)c0.Suit, cuenta = 0;
        if (c1.Suit != c0.Suit) return false;
        for (int i = 0; i < nBoard; i++) if ((int)_sBoard[i].Suit == palo) cuenta++;
        return cuenta == 2;
    }

    private static int Contar(bool[] v, int n)
    {
        int c = 0;
        for (int i = 0; i < n; i++) if (v[i]) c++;
        return c;
    }

    private bool HayPendientes(int n)
    {
        for (int i = 0; i < n; i++) if (_sActivo[i] && _sPend[i]) return true;
        return false;
    }

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
