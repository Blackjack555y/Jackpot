using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// jackpot_v2 — agente de Limit Hold'em (6-max) para AgentsPoker SDK v0.3.0.
///
/// Fase A (inicio): tight-aggressive. Equity Monte Carlo contra RANGOS estimados de cada rival (no contra manos al azar),
///   pot odds para pagar, umbral de subida según número de rivales y posición.
/// Fase B (aprende): OnHandEnd acumula VPIP, PFR, agresión, fold ante apuesta postflop y faroles mostrados en showdown;
///   cada rival se clasifica (station, maníaco, roca, normal) y se explota.
/// Fase C (final de mesa): importa el puesto (top N clasifica): protege la clasificación o arriesga si va fuera.
///
/// Los parámetros ajustables viven en ParamsPorDefecto (valores salidos del laboratorio). La Arena usa el constructor
/// sin parámetros; el constructor con parámetros existe solo para entrenar en Entrenamiento/.
/// </summary>
public sealed class jackpot_v2 : IPokerAgent, IHandObserver
{
    // ---- Índices de parámetros ajustables ----
    public const int PMargenApostar = 0;    // equity extra sobre 1/(n+1) para BET (nadie apostó)
    public const int PMargenSubir = 1;      // equity extra para RAISE cuando nos apuestan
    public const int PMargenPorApuesta = 2; // más exigencia en guerras de subidas
    public const int PImplicitas = 3;       // odds implícitas en el flop (la mitad en preflop/turn)
    public const int PProbCbet = 4;         // prob. de apuesta de continuación
    public const int PPosicion = 5;         // ajuste de umbral por posición
    public const int PProteger = 6;         // final de mesa: más apretado si clasificamos con margen
    public const int PPerseguir = 7;        // final de mesa: más suelto si vamos fuera
    public const int PPriorVpip = 8;        // suposición inicial de VPIP de un desconocido
    public const int PPagarManiaco = 9;     // pagar más ligero ante un maníaco
    public const int PValorStation = 10;    // fracción del margen de BET contra calling stations
    public const int NumParams = 11;

    /// <summary>Valores por defecto (datos fijos: nunca se modifican; privados para que nadie más pueda tocarlos).</summary>
    private static readonly double[] ParamsPorDefecto =
    {
        0.08, 0.14, 0.05, 0.04, 0.60, 0.04, 0.05, 0.03, 0.35, 0.04, 0.50,
    };

    private const int PresupuestoMs = 150;     // corte del Monte Carlo (límite real: 1000 ms)
    private const int MaxSimulaciones = 6000;
    private const int IntentosRango = 25;      // muestreo por rechazo de la mano de cada rival
    private const double PriorAgresion = 0.30;
    private const double PriorFoldPost = 0.35;
    private const int PesoPrior = 8;           // manos "ficticias" del prior (suaviza las estadísticas)
    private const int ManosMinTipo = 6;        // manos observadas antes de etiquetar a un rival
    private const int ManosPorMesa = 30;       // valor por defecto del torneo (el árbitro puede cambiarlo)
    private const int Clasificados = 3;

    private readonly double[] _p;
    private readonly Random _rng = new Random(20261027);
    private readonly float[] _percentil = new float[Card.DeckSize * Card.DeckSize]; // 0 = mejor mano inicial, 1 = peor
    private readonly Dictionary<string, Perfil> _rivales = new Dictionary<string, Perfil>();

    private enum Tipo { Normal, Station, Maniaco, Roca }

    /// <summary>Estadísticas de un rival, acumuladas durante la mesa (clave: nombre).</summary>
    private sealed class Perfil
    {
        public int Manos, ManosJugadas, SubidasPreflop, Subidas, Acciones;
        public int EnfrentaApuestaPost, FoldsApuestaPost, FarolesMostrados;
        public double Vpip(double prior) => (ManosJugadas + prior * PesoPrior) / (Manos + PesoPrior);
        public double Pfr(double prior) => (SubidasPreflop + prior * 0.5 * PesoPrior) / (Manos + PesoPrior);
        public double Agresion => (Subidas + PriorAgresion * PesoPrior) / (Acciones + PesoPrior);
        public double FoldPost => (FoldsApuestaPost + PriorFoldPost * PesoPrior) / (EnfrentaApuestaPost + PesoPrior);
    }

    /// <summary>Modelo de un rival activo para la decisión actual.</summary>
    private struct Oponente
    {
        public string Nombre;
        public Tipo Tipo;
        public double Participa;       // probabilidad de que siga en la mano (preflop, si aún no habló)
        public double Rango;           // fracción superior de manos iniciales que conserva (0..1)
        public double RechazoNada;     // prob. de descartar manos sin pareja en el board actual
        public double RechazoPareja;   // prob. de descartar manos de una sola pareja
    }

    public jackpot_v2() : this(ParamsPorDefecto) { }

    /// <summary>Solo para el laboratorio de entrenamiento: crea el agente con otros parámetros.</summary>
    public jackpot_v2(double[] parametros)
    {
        _p = parametros != null && parametros.Length == NumParams ? (double[])parametros.Clone() : ParamsPorDefecto;
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
        bool todosStation = ops.Count > 0;
        foreach (var op in ops)
        {
            nEf += op.Participa;
            if (op.Tipo != Tipo.Station) todosStation = false;
        }
        nEf = Math.Max(1.0, nEf);

        double equity = EstimarEquity(o, ops, reloj);
        double ajuste = AjusteClasificacion(o) + AjustePosicion(o); // >0 = jugar más apretado

        // RAISE / BET: equity claramente por encima de la parte justa del bote.
        double margen = o.ToCall == 0 ? _p[PMargenApostar] * (todosStation ? _p[PValorStation] : 1.0) : _p[PMargenSubir];
        double umbralSubir = 1.0 / (nEf + 1.0) + margen + _p[PMargenPorApuesta] * Math.Max(0, o.BetsThisStreet - 1) + ajuste;
        if (o.CanRaise && equity >= umbralSubir) return ActionType.RAISE;

        if (o.ToCall == 0)
        {
            if (o.CanRaise && DebeContinuar(o, ops)) return ActionType.RAISE;
            return ActionType.CALL; // check gratis: nunca foldear sin pagar
        }

        // CALL si la equity cubre las pot odds (con algo de odds implícitas en calles tempranas).
        double implicitas = o.Street == Street.Flop ? _p[PImplicitas] : o.Street == Street.River ? 0.0 : _p[PImplicitas] * 0.5;
        if (AgresorEsManiaco(o, ops)) implicitas += _p[PPagarManiaco];
        if (equity + implicitas - ajuste >= o.PotOdds) return ActionType.CALL;
        return ActionType.FOLD;
    }

    // ---------------------------------------------------------------- modelado de rivales

    private Tipo TipoDe(Perfil p)
    {
        if (p == null || p.Manos < ManosMinTipo) return Tipo.Normal;
        double vpip = p.Vpip(_p[PPriorVpip]);
        if (p.Agresion > 0.5 || p.FarolesMostrados >= 2) return Tipo.Maniaco;
        if ((vpip > 0.5 && p.Agresion < 0.25) || p.FoldPost < 0.15) return Tipo.Station;
        if (vpip < 0.2) return Tipo.Roca;
        return Tipo.Normal;
    }

    private List<Oponente> ModelarOponentes(PlayerObservation o)
    {
        var lista = new List<Oponente>();
        bool preflop = o.Street == Street.Preflop;
        foreach (var p in o.Players)
        {
            if (p.Seat == o.Seat || !p.IsActive) continue;
            _rivales.TryGetValue(p.Name, out var perfil);
            double vpip = perfil != null ? perfil.Vpip(_p[PPriorVpip]) : _p[PPriorVpip];
            double pfr = perfil != null ? perfil.Pfr(_p[PPriorVpip]) : _p[PPriorVpip] * 0.5;
            double agr = perfil?.Agresion ?? PriorAgresion;
            var tipo = TipoDe(perfil);

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

            var op = new Oponente { Nombre = p.Name, Tipo = tipo, Participa = 1.0 };
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
            if (tipo == Tipo.Maniaco) op.Rango = Math.Max(op.Rango, 0.5); // sube con casi cualquier cosa
            op.Rango = Math.Min(1.0, op.Rango);

            if (agresionPost > 0)
            {
                // Un rival pasivo que apuesta suele tener algo; un maníaco, no tanto.
                op.RechazoNada = tipo == Tipo.Maniaco ? 0.1 : Clamp(0.85 - agr, 0.15, 0.8);
                if ((agresionPost >= 2 || subioEnEstaCalle) && tipo != Tipo.Maniaco)
                    op.RechazoPareja = Clamp(0.5 - 0.5 * agr, 0.05, 0.4);
            }
            lista.Add(op);
        }
        return lista;
    }

    private static bool AgresorEsManiaco(PlayerObservation o, List<Oponente> ops)
    {
        string agresor = null;
        for (int i = o.ActionHistory.Count - 1; i >= 0 && agresor == null; i--)
        {
            var a = o.ActionHistory[i];
            if (a.Street != o.Street) break;
            if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) agresor = a.PlayerName;
        }
        if (agresor == null) return false;
        foreach (var op in ops) if (op.Nombre == agresor) return op.Tipo == Tipo.Maniaco;
        return false;
    }

    /// <summary>Apuesta de continuación: fuimos el último agresor preflop, flop, pocos rivales y ninguno es "paga todo".</summary>
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
        foreach (var op in ops)
        {
            if (op.Tipo == Tipo.Station) return false; // contra quien paga todo no se farolea
            fold += _rivales.TryGetValue(op.Nombre, out var perfil) ? perfil.FoldPost : PriorFoldPost;
        }
        if (fold / ops.Count < 0.25) return false;
        return _rng.NextDouble() < _p[PProbCbet];
    }

    /// <summary>Hablar de último da ventaja: en posición tardía bajamos el umbral, en temprana lo subimos.</summary>
    private double AjustePosicion(PlayerObservation o)
    {
        int n = o.Players.Count;
        int ultimo = -1;
        if (o.Street == Street.Preflop) ultimo = o.BigBlindSeat;
        else
            for (int k = 0; k < n && ultimo < 0; k++)
            {
                int s = (o.ButtonSeat - k + n) % n;
                if (o.Players[s].IsActive) ultimo = s;
            }
        if (ultimo < 0) return 0;
        int distUltimo = (ultimo - o.Seat + n) % n;
        int despues = 0;
        foreach (var p in o.Players)
        {
            if (p.Seat == o.Seat || !p.IsActive) continue;
            int d = (p.Seat - o.Seat + n) % n;
            if (d <= distUltimo) despues++;
        }
        if (despues == 0) return -_p[PPosicion];
        if (despues >= 3) return _p[PPosicion];
        return 0;
    }

    /// <summary>
    /// Al final de la mesa importa el puesto, no las fichas: si vamos cómodos en zona de clasificación, más apretado;
    /// si vamos fuera, más suelto (más aún en las 2 últimas manos).
    /// </summary>
    private double AjusteClasificacion(PlayerObservation o)
    {
        int restantes = ManosPorMesa - o.HandNumber; // HandNumber empieza en 1
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
            otros.Sort();
            otros.Reverse();
            int fuera = otros.Count > clasifican - 1 ? otros[clasifican - 1] : 0; // primero que quedaría fuera
            return miStack - fuera > 400 ? _p[PProteger] : 0.0;
        }
        return restantes <= 1 ? -2 * _p[PPerseguir] : -_p[PPerseguir];
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
            bool jugo = false, subioPre = false, agresivoPost = false;
            var calle = Street.Preflop;
            bool hayApuesta = false;
            foreach (var a in s.Actions)
            {
                if (a.Street != calle) { calle = a.Street; hayApuesta = false; }
                bool agresiva = a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise;
                if (a.Seat == seat && a.Kind != ActionKind.SmallBlind && a.Kind != ActionKind.BigBlind)
                {
                    p.Acciones++;
                    if (agresiva) p.Subidas++;
                    if (a.Street == Street.Preflop && (a.Kind == ActionKind.Call || a.Kind == ActionKind.Raise)) jugo = true;
                    if (a.Street == Street.Preflop && a.Kind == ActionKind.Raise) subioPre = true;
                    if (a.Street != Street.Preflop)
                    {
                        if (agresiva) agresivoPost = true;
                        if (hayApuesta)
                        {
                            // Fold ante apuesta postflop: solo cuenta si había algo que pagar.
                            p.EnfrentaApuestaPost++;
                            if (a.Kind == ActionKind.Fold) p.FoldsApuestaPost++;
                        }
                    }
                }
                if (agresiva) hayApuesta = true;
            }
            if (jugo) p.ManosJugadas++;
            if (subioPre) p.SubidasPreflop++;

            // Farol mostrado: apostó/subió postflop y enseñó carta alta en el showdown.
            if (agresivoPost && s.CardsOf(seat) != null && HandEvaluator.GetCategory(s.HandValues[seat]) == HandCategory.HighCard)
                p.FarolesMostrados++;
        }
    }
}
