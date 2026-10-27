using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;

// =====================================================================================================
// LABORATORIO DE ENTRENAMIENTO (no se entrega: edítalo a tu gusto)
//
//   .\entrenar.cmd                 medición original (A: bb/100, B: formato torneo), 30 s por prueba
//   .\entrenar.cmd 300             igual, 300 s por prueba
//   .\entrenar.cmd comparar 2000   jackpot vs jackpot_v1 vs MiAgente en LAS MISMAS 2000 mesas
//   .\entrenar.cmd ajustar 1500    búsqueda de parámetros de jackpot_v2 (1500 mesas por candidato)
//
// Aquí sí se usan hilos (Parallel.For): este proyecto NO se entrega, y cada mesa crea sus propios agentes.
// =====================================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Valores iniciales de los parámetros (deben coincidir con ParamsPorDefecto de Agents/jackpot_v2.cs).
double[] Inicial = { 0.08, 0.14, 0.05, 0.04, 0.60, 0.04, 0.05, 0.03, 0.35, 0.04, 0.50 };
string[] NombresP = { "MargenApostar", "MargenSubir", "MargenPorApuesta", "Implicitas", "ProbCbet", "Posicion",
                      "Proteger", "Perseguir", "PriorVpip", "PagarManiaco", "ValorStation" };
double[] Paso = { 0.03, 0.03, 0.02, 0.02, 0.20, 0.02, 0.03, 0.02, 0.08, 0.03, 0.25 };
double[] Min = { 0, 0, 0, 0, 0, 0, 0, 0, 0.1, 0, 0 };
double[] Max = { 0.4, 0.4, 0.2, 0.2, 1, 0.2, 0.2, 0.2, 0.8, 0.2, 1 };

// Pool de rivales: builtins del SDK + agentes de Agents/. Heuristic (Jefe Final) pesa doble.
var pool = new List<(string Nombre, Func<IPokerAgent> Crear)>
{
    ("Heuristic", () => new SimpleHeuristicAgent()),
    ("Heuristic", () => new SimpleHeuristicAgent()),
    ("Rival", () => new Rival()),
    ("Station", () => new CallingStationAgent()),
    ("Raiser", () => new AlwaysRaiseAgent()),
    ("Random", () => new RandomAgent(null)),
    ("MiAgente", () => new MiAgente()),
    ("jackpot_v1", () => new jackpot_v1()),
};

string modo = args.Length > 0 ? args[0].ToLowerInvariant() : "";
if (modo == "comparar")
{
    int mesas = args.Length > 1 ? int.Parse(args[1]) : 1000;
    int semillaComp = args.Length > 2 ? int.Parse(args[2]) : 1000;
    var mesasDef = GenerarMesas(mesas, semillaComp);
    var candidatos = new List<(string, Func<IPokerAgent>)>
    {
        ("jackpot", () => new jackpot()),
        ("jackpot_v1", () => new jackpot_v1()),
        ("jackpot_v2", () => new jackpot_v2()),
        ("jackpot_v3", () => new jackpot_v3()),
        ("MiAgente", () => new MiAgente()),
        ("Rival", () => new Rival()),
        ("Heuristic", () => new SimpleHeuristicAgent()),
    };
    Console.WriteLine($"Comparación en las MISMAS {mesas} mesas (tamaños 3, 4 y 6; rivales al azar del pool)\n");
    Console.WriteLine($"{"Agente",-12} {"Clasif%",8} {"±",6} {"Gana%",7} {"Puesto",7} {"Stack",8}");
    int[] ref0 = null;
    foreach (var (nombre, crear) in candidatos)
    {
        var r = Evaluar(crear, mesasDef);
        double p = r.Clasif.Average();
        Console.Write($"{nombre,-12} {100 * p,8:F1} {100 * 1.96 * Math.Sqrt(p * (1 - p) / mesas),6:F1} {100.0 * r.Gana / mesas,7:F1} {r.RankProm,7:F2} {r.StackProm,8:N0}");
        if (ref0 == null) { ref0 = r.Clasif; Console.WriteLine("   (referencia)"); }
        else
        {
            var (d, se) = DifPareada(ref0, r.Clasif);
            Console.WriteLine($"   jackpot - este = {100 * d:+0.0;-0.0} ± {100 * 1.96 * se:F1} pts");
        }
    }
    return;
}

if (modo == "ajustar")
{
    int mesas = args.Length > 1 ? int.Parse(args[1]) : 1500;
    int pasadas = args.Length > 2 ? int.Parse(args[2]) : 2;
    var mejor = (double[])Inicial.Clone();
    int semilla = 50_000;
    Console.WriteLine($"Ajuste por coordenadas: {mesas} mesas por candidato, {pasadas} pasadas, {Environment.ProcessorCount} hilos");
    var total = Stopwatch.StartNew();
    for (int pasada = 1; pasada <= pasadas; pasada++)
    {
        bool cambio = false;
        for (int i = 0; i < Inicial.Length; i++)
        {
            var conjunto = GenerarMesas(mesas, semilla); semilla += mesas;
            var copiaMejor = (double[])mejor.Clone();
            var basal = Evaluar(() => new jackpot_v2(copiaMejor), conjunto).Clasif;
            double[] ganador = null; double mejorD = 0, mejorSe = 0;
            foreach (int signo in new[] { +1, -1 })
            {
                var cand = (double[])mejor.Clone();
                cand[i] = Math.Round(Math.Clamp(cand[i] + signo * Paso[i], Min[i], Max[i]), 4);
                if (cand[i] == mejor[i]) continue;
                var c = Evaluar(() => new jackpot_v2(cand), conjunto).Clasif;
                var (d, se) = DifPareada(c, basal);
                Console.WriteLine($"  [{pasada}] {NombresP[i],-16} {mejor[i],5:0.###} -> {cand[i],5:0.###}: {100 * d:+0.0;-0.0} ± {100 * 1.96 * se:F1} pts");
                if (d > mejorD) { mejorD = d; mejorSe = se; ganador = cand; }
            }
            if (ganador == null || mejorD < 1.0 * mejorSe) continue;

            // Confirmación con mesas NUEVAS: el cambio debe seguir ganando (evita quedarnos con la suerte).
            var conf = GenerarMesas(mesas, semilla); semilla += mesas;
            var g = ganador;
            var (d2, se2) = DifPareada(Evaluar(() => new jackpot_v2(g), conf).Clasif, Evaluar(() => new jackpot_v2(copiaMejor), conf).Clasif);
            double z = (mejorD + d2) / Math.Sqrt(mejorSe * mejorSe + se2 * se2);
            Console.WriteLine($"      confirmación: {100 * d2:+0.0;-0.0} ± {100 * 1.96 * se2:F1} pts  (z combinado {z:F2})");
            if (d2 > 0 && z > 2.0)
            {
                mejor = ganador; cambio = true;
                Console.WriteLine($"      ACEPTADO {NombresP[i]} = {mejor[i]}");
            }
        }
        Console.WriteLine($"Fin pasada {pasada} ({total.Elapsed.TotalMinutes:F1} min). Parámetros:");
        Console.WriteLine("    " + string.Join(", ", mejor.Select(v => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture))) + ",");
        if (!cambio) break;
    }
    Console.WriteLine("\nPega esa línea en ParamsPorDefecto de Agents/jackpot_v2.cs y verifica con 'comparar' y con run.cmd bench.");
    return;
}

// ------------------------------- modo original (A largo plazo / B formato torneo) con jackpot -------------------------------
Func<IPokerAgent> crearMiAgente = () => new jackpot();
var rivales = new List<(string Nombre, Func<IPokerAgent> Crear)>
{
    ("Heuristic", () => new SimpleHeuristicAgent()),
    ("Heuristic2", () => new SimpleHeuristicAgent()),
    ("Rival", () => new Rival()),
    ("Station", () => new CallingStationAgent()),
    ("Raiser", () => new AlwaysRaiseAgent()),
};
double segundos = args.Length > 0 ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 30;
Console.WriteLine($"AgentsPoker.Core v{typeof(IPokerAgent).Assembly.GetName().Version.ToString(3)} | rivales: {string.Join(", ", rivales.Select(r => r.Nombre))} | {segundos} s por prueba");
{
    var cfg = GameConfig.Training(seed: 1);
    var mesa = new PokerTable(cfg);
    var yo = new Cronometro(crearMiAgente());
    mesa.Seat(yo, "Yo");
    foreach (var r in rivales) mesa.Seat(r.Crear(), r.Nombre);

    long manos = 0;
    double sumaBb = 0, sumaBb2 = 0;
    int errores = 0;
    var reloj = Stopwatch.StartNew();
    while (reloj.Elapsed.TotalSeconds < segundos)
    {
        for (int s = 0; s < mesa.SeatCount; s++)
            if (mesa.GetStack(s) == 0) mesa.SetStack(s, cfg.StartingStack);
        var res = mesa.PlayHand();
        double bb = res.Net[0] / (double)cfg.BigBlind;
        sumaBb += bb;
        sumaBb2 += bb * bb;
        manos++;
        foreach (var e in res.Errors)
            if (e.Seat == 0 && errores++ < 10) Console.WriteLine("  ERROR " + e);
    }
    double media = sumaBb / manos;
    double margen = 1.96 * Math.Sqrt(Math.Max(0, sumaBb2 / manos - media * media) / manos);
    Console.WriteLine($"\nA) Largo plazo: {manos:N0} manos ({manos / reloj.Elapsed.TotalSeconds:N0} manos/s)");
    Console.WriteLine($"   Resultado: {media * 100:+0.0;-0.0} ± {margen * 100:0.0} bb/100");
    Console.WriteLine($"   Decisión: {yo.TotalMs / Math.Max(1, yo.Decisiones):F2} ms prom, {yo.MaxMs:F1} ms máx   Errores: {errores}");
}
{
    var rng = new Random(7);
    int mesas = 0, clasifica = 0, gana = 0;
    long rankSuma = 0, stackSuma = 0;
    int clasificanPorMesa = Math.Min(3, rivales.Count);
    var reloj = Stopwatch.StartNew();
    while (reloj.Elapsed.TotalSeconds < segundos)
    {
        var asientos = new List<(string Nombre, Func<IPokerAgent> Crear)> { ("Yo", crearMiAgente) };
        asientos.AddRange(rivales);
        asientos = asientos.OrderBy(_ => rng.Next()).ToList();
        var mesa = new PokerTable(GameConfig.Training(seed: rng.Next()));
        foreach (var a in asientos) mesa.Seat(a.Crear(), a.Nombre);
        var partida = new TableMatch(mesa, handsToPlay: 30, qualifiers: clasificanPorMesa);
        partida.PlayAll();
        var yo = partida.GetStandings().First(s => s.Name == "Yo");
        mesas++;
        if (yo.Qualifies) clasifica++;
        if (yo.Rank == 1) gana++;
        rankSuma += yo.Rank;
        stackSuma += yo.Stack;
    }
    double p = clasifica / (double)mesas;
    Console.WriteLine($"\nB) Formato torneo: {mesas:N0} mesas de 30 manos, {1 + rivales.Count} jugadores, top {clasificanPorMesa} clasifica");
    Console.WriteLine($"   Clasif% {100 * p:F1} ± {100 * 1.96 * Math.Sqrt(p * (1 - p) / mesas):F1}   Gana% {100.0 * gana / mesas:F1}" +
                      $"   Puesto prom {rankSuma / (double)mesas:F2}   Stack prom {stackSuma / mesas:N0}");
}
return;

// ------------------------------- utilidades -------------------------------

// Una "mesa" = semilla + tamaño + rivales elegidos + asiento del candidato. Igual para todos los candidatos
// (números aleatorios comunes): así las diferencias se deben al agente y no a las cartas.
List<(int Semilla, int[] Rivales, int MiAsiento)> GenerarMesas(int cuantas, int semillaBase)
{
    var rng = new Random(semillaBase);
    var lista = new List<(int, int[], int)>();
    for (int t = 0; t < cuantas; t++)
    {
        double u = rng.NextDouble();
        int tam = u < 0.6 ? 6 : u < 0.8 ? 4 : 3;
        var riv = Enumerable.Range(0, tam - 1).Select(_ => rng.Next(pool.Count)).ToArray();
        lista.Add((rng.Next(), riv, rng.Next(tam)));
    }
    return lista;
}

(int[] Clasif, int Gana, double RankProm, double StackProm) Evaluar(Func<IPokerAgent> crear, List<(int Semilla, int[] Rivales, int MiAsiento)> mesas)
{
    var clasif = new int[mesas.Count];
    int gana = 0; long rank = 0, stack = 0;
    Parallel.For(0, mesas.Count, t =>
    {
        var (semilla, riv, miAsiento) = mesas[t];
        int tam = riv.Length + 1;
        var mesa = new PokerTable(GameConfig.Training(seed: semilla));
        int k = 0;
        for (int s = 0; s < tam; s++)
        {
            if (s == miAsiento) mesa.Seat(crear(), "Yo");
            else { var r = pool[riv[k]]; mesa.Seat(r.Crear(), r.Nombre + "#" + k); k++; }
        }
        var partida = new TableMatch(mesa, handsToPlay: 30, qualifiers: Math.Min(3, tam - 1));
        partida.PlayAll();
        var yo = partida.GetStandings().First(s => s.Name == "Yo");
        clasif[t] = yo.Qualifies ? 1 : 0;
        if (yo.Rank == 1) Interlocked.Increment(ref gana);
        Interlocked.Add(ref rank, yo.Rank);
        Interlocked.Add(ref stack, yo.Stack);
    });
    return (clasif, gana, rank / (double)mesas.Count, stack / (double)mesas.Count);
}

// Diferencia media pareada (a - b) y su error estándar.
(double, double) DifPareada(int[] a, int[] b)
{
    int n = a.Length;
    double m = 0, m2 = 0;
    for (int i = 0; i < n; i++) { double d = a[i] - b[i]; m += d; m2 += d * d; }
    m /= n;
    double var = Math.Max(0, m2 / n - m * m);
    return (m, Math.Sqrt(var / n));
}

/// <summary>Mide el tiempo de cada decisión y reenvía OnHandEnd si tu agente lo implementa.</summary>
sealed class Cronometro : IPokerAgent, IHandObserver
{
    private readonly IPokerAgent _agente;
    public double MaxMs, TotalMs;
    public long Decisiones;

    public Cronometro(IPokerAgent agente) { _agente = agente; }

    public void Initialize(string agentName) => _agente.Initialize(agentName);

    public ActionType GetAction(PlayerObservation o)
    {
        var sw = Stopwatch.StartNew();
        try { return _agente.GetAction(o); }
        finally
        {
            double ms = sw.Elapsed.TotalMilliseconds;
            TotalMs += ms;
            Decisiones++;
            if (ms > MaxMs) MaxMs = ms;
        }
    }

    public void OnHandEnd(HandSummary s)
    {
        if (_agente is IHandObserver h) h.OnHandEnd(s);
    }
}
