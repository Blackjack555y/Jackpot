using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
//   .\entrenar.cmd selfplay 5000   autoentrenamiento de jackpot_q (Q-learning + UCB + self-play) hasta 5000 mesas
//   .\entrenar.cmd selfplay infinito   igual, sin tope: se detiene solo al llegar a la meseta (o con liga2/STOP)
//   Opciones: --hilos N (núcleos a usar), --tanda 500, --validacion 600, --dir liga2
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
var paralelo = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Opcion("--hilos", Environment.ProcessorCount)) };

if (modo == "selfplay")
{
    AutoEntrenar();
    return;
}

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
        ("jackpot_q", () => new jackpot_q()),
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
    Parallel.For(0, mesas.Count, paralelo, t =>
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

int Opcion(string nombre, int porDefecto)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == nombre) return int.Parse(args[i + 1]);
    return porDefecto;
}

string OpcionTexto(string nombre, string porDefecto)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == nombre) return args[i + 1];
    return porDefecto;
}

// ======================================= AUTOENTRENAMIENTO (self-play) =======================================
// Un solo "cerebro" (tabla Q compartida) juega en 1-3 asientos de cada mesa contra el salón de la fama (tablas campeonas
// anteriores congeladas) y anclas fijas (pool). Mejora de política: los aprendices juegan como la base (v1 + campeón
// actual) y solo en algunas decisiones de prueba eligen con UCB; la recompensa de esas pruebas es si el asiento clasificó
// (1/0) al final de la mesa, así que Q(s,a) = prob. de clasificar haciendo a en s y siguiendo con la base (promedio
// incremental, alfa = 1/N). Solo cambia de campeón si gana en una validación separada (solo anclas, semillas que nunca
// se entrenan) y lo confirma en mesas nuevas (z combinado > 2); entonces el campeón pasa a ser la base y Q se reinicia.
void AutoEntrenar()
{
    bool infinito = args.Length > 1 && args[1].ToLowerInvariant() == "infinito";
    int objetivo = !infinito && args.Length > 1 && int.TryParse(args[1], out var om) ? om : 5000;
    int tanda = Opcion("--tanda", 500);
    int nVal = Opcion("--validacion", 600);
    string dir = Path.Combine("Entrenamiento", OpcionTexto("--dir", "liga2"));
    double cUcb = Math.Sqrt(2);     // UCB1: recompensas entre 0 y 1
    const double probPrueba = 0.35; // prob. de que una decisión sea de prueba (≈ una por mano)
    const int nMin = 30;            // visitas mínimas por acción para poder corregir
    const double zCorreccion = 2.5; // la ventaja debe superar 2,5 errores estándar (límite inferior)
    const int maxSalon = 5, paciencia = 6, ventanaMeseta = 10;
    Directory.CreateDirectory(dir);
    string archEstado = Path.Combine(dir, "estado.bin"), archHist = Path.Combine(dir, "historial.csv");
    string archCampeon = Path.Combine(dir, "campeon_tabla.txt"), archStop = Path.Combine(dir, "STOP");
    if (File.Exists(archStop)) File.Delete(archStop);

    var e = File.Exists(archEstado) ? EstadoLiga.Cargar(archEstado) : new EstadoLiga();
    if (!File.Exists(archHist))
        File.WriteAllText(archHist, "tanda,mesas,minutos,clasif_entrenamiento,val_candidato,val_campeon,dif,mas_menos,z,aceptado,correcciones,estados_visitados\n");
    Console.WriteLine($"Autoentrenamiento {(infinito ? "INDEFINIDO (para en la meseta o con " + archStop + ")" : $"hasta {objetivo} mesas")}" +
                      $" | tanda {tanda} | validación {nVal} | {paralelo.MaxDegreeOfParallelism} hilos | carpeta {dir}");
    if (e.Tandas > 0) Console.WriteLine($"Retomando: {e.Tandas} tandas, {e.Mesas} mesas, salón de la fama {e.Salon.Count}, campeón {(e.Campeon == null ? "v1" : "tabla")}");

    var valMesas = GenerarMesas(nVal, 777_777);                 // validación fija: nunca se usa para entrenar
    int[] valCampeon = Evaluar(() => new jackpot_q(e.Campeon), valMesas).Clasif;
    var reloj = Stopwatch.StartNew();

    while (true)
    {
        if (!infinito && e.Mesas >= objetivo) { Console.WriteLine("Fin: se alcanzó el tope de mesas."); break; }
        if (File.Exists(archStop)) { Console.WriteLine("Fin: archivo STOP."); break; }

        // ---- 1. Entrenamiento: una tanda de mesas mixtas (semilla según la tanda: reproducible y retomable) ----
        var rng = new Random(1_000_003 + 7919 * e.Tandas);
        var defs = new List<(int Semilla, int[] Tipos)>();
        for (int t = 0; t < tanda; t++)
        {
            double u = rng.NextDouble();
            int tam = u < 0.6 ? 6 : u < 0.8 ? 4 : 3;
            int aprendices = rng.Next(1, Math.Min(3, tam - 1) + 1);
            var tipos = new int[tam];                                // -1 aprendiz, -2-k salón k, >= 0 ancla del pool
            for (int s = 0; s < tam; s++)
                tipos[s] = s < aprendices ? -1 : e.Salon.Count > 0 && rng.NextDouble() < 0.25 ? -2 - rng.Next(e.Salon.Count) : rng.Next(pool.Count);
            defs.Add((rng.Next(), tipos.OrderBy(_ => rng.Next()).ToArray()));
        }
        var pruebas = new List<(int Estado, int Accion, int Clasifico)>[tanda];
        var clasif = new int[tanda];
        var asientos = new int[tanda];
        var q = e.Q; var n = e.N;                                   // solo lectura durante la tanda
        Parallel.For(0, tanda, paralelo, t =>
        {
            var (semilla, tipos) = defs[t];
            var mesa = new PokerTable(GameConfig.Training(seed: semilla));
            var ap = new List<(jackpot_q Agente, int Asiento, string Nombre)>();
            for (int s = 0; s < tipos.Length; s++)
            {
                if (tipos[s] == -1)
                {
                    var ag = new jackpot_q(e.Campeon, q, n, cUcb, probPrueba, semilla ^ (s * 7919 + 17));
                    ap.Add((ag, mesa.Seat(ag, "L" + s), "L" + s));
                }
                else if (tipos[s] <= -2) mesa.Seat(new jackpot_q(e.Salon[-2 - tipos[s]]), "Salon" + s);
                else mesa.Seat(pool[tipos[s]].Crear(), pool[tipos[s]].Nombre + "#" + s);
            }
            var partida = new TableMatch(mesa, handsToPlay: 30, qualifiers: Math.Min(3, tipos.Length - 1));
            while (partida.PlayNext() != null) { }
            var pos = partida.GetStandings();
            var lista = new List<(int, int, int)>();
            foreach (var a in ap)
            {
                int cl = pos.First(x => x.Name == a.Nombre).Qualifies ? 1 : 0;   // recompensa = clasificar
                foreach (var (est, acc) in a.Agente.Pruebas) lista.Add((est, acc, cl));
                clasif[t] += cl;
            }
            pruebas[t] = lista;
            asientos[t] = ap.Count;
        });

        // ---- 2. Actualizar Q con el promedio incremental, en orden fijo (un solo hilo, sin carreras) ----
        // El resto de la mesa lo juega la base (política fija), así que la meta es la recompensa final: no hace falta max Q.
        foreach (var lista in pruebas)
            foreach (var (est, acc, cl) in lista)
            {
                int i = est * jackpot_q.NumAcciones + acc;
                e.N[i]++;
                e.Q[i] += (cl - e.Q[i]) / e.N[i];
            }
        e.Tandas++;
        e.Mesas += tanda;
        double clasifEntreno = clasif.Sum() / (double)Math.Max(1, asientos.Sum());

        // ---- 3. Validación y puerta de campeón ----
        var cand = jackpot_q.ConstruirCorreccion(e.Q, e.N, e.Campeon, nMin, zCorreccion);
        int correcciones = cand.Count(x => x >= 0);
        bool distinto = e.Campeon == null ? correcciones > 0 : !cand.SequenceEqual(e.Campeon);
        int[] valCand = Evaluar(() => new jackpot_q(cand), valMesas).Clasif;
        var (d1, se1) = DifPareada(valCand, valCampeon);
        e.HistVal.Add(valCand.Average());
        double z = 0;
        bool aceptado = false;
        if (distinto && d1 > 0 && d1 > se1)
        {
            var conf = GenerarMesas(nVal, 5_000_000 + 1009 * e.Tandas);      // mesas nuevas en cada confirmación
            var (d2, se2) = DifPareada(Evaluar(() => new jackpot_q(cand), conf).Clasif, Evaluar(() => new jackpot_q(e.Campeon), conf).Clasif);
            z = (d1 + d2) / Math.Sqrt(se1 * se1 + se2 * se2 + 1e-12);
            Console.WriteLine($"   confirmación: {100 * d2:+0.0;-0.0} ± {100 * 1.96 * se2:F1} pts (z combinado {z:F2})");
            if (d2 > 0 && z > 2.0)
            {
                aceptado = true;
                e.Campeon = cand;
                e.Salon.Add(cand);
                if (e.Salon.Count > maxSalon) e.Salon.RemoveAt(0);
                valCampeon = valCand;
                EscribirCampeon(archCampeon, cand, e);
                Array.Clear(e.Q); Array.Clear(e.N);       // los valores eran de la base anterior: se vuelve a medir
            }
        }
        e.SinMejora = aceptado ? 0 : e.SinMejora + 1;
        int visitados = Enumerable.Range(0, jackpot_q.NumEstados).Count(s => e.N[s * 3] + e.N[s * 3 + 1] + e.N[s * 3 + 2] > 0);

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        File.AppendAllText(archHist, string.Format(ci, "{0},{1},{2:F1},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4},{8:F2},{9},{10},{11}\n",
            e.Tandas, e.Mesas, reloj.Elapsed.TotalMinutes, clasifEntreno, valCand.Average(), valCampeon.Average(),
            d1, 1.96 * se1, z, aceptado ? 1 : 0, correcciones, visitados));
        Console.WriteLine($"Tanda {e.Tandas,3} | {e.Mesas,6} mesas | {reloj.Elapsed.TotalMinutes,6:F1} min | entreno {100 * clasifEntreno,5:F1}%" +
                          $" | validación cand {100 * valCand.Average(),5:F1}% vs campeón {100 * valCampeon.Average(),5:F1}%" +
                          $" ({100 * d1:+0.0;-0.0} ± {100 * 1.96 * se1:F1}) | correcciones {correcciones} | estados {visitados}" +
                          (aceptado ? " | NUEVO CAMPEÓN" : $" | sin mejora {e.SinMejora}"));
        e.Guardar(archEstado);

        // ---- 4. Meseta: varias puertas sin campeón y tendencia de validación plana ----
        if (e.SinMejora >= paciencia && e.HistVal.Count >= ventanaMeseta)
        {
            var y = e.HistVal.Skip(e.HistVal.Count - ventanaMeseta).ToArray();
            var (pend, sePend) = Pendiente(y);
            if (Math.Abs(pend) < 1.96 * sePend)
            {
                Console.WriteLine($"Fin: meseta ({paciencia} puertas sin campeón y pendiente {100 * pend:+0.00;-0.00} ± {100 * 1.96 * sePend:F2} pts/tanda).");
                break;
            }
        }
    }
    Console.WriteLine(e.Campeon == null
        ? "Ningún candidato superó a la v1: jackpot.cs se queda igual."
        : $"Campeón guardado en {archCampeon}. Verifícalo con 'comparar' antes de pegarlo en jackpot.cs.");
}

static (double, double) Pendiente(double[] y)
{
    int k = y.Length;
    double mx = (k - 1) / 2.0, my = y.Average(), sxx = 0, sxy = 0;
    for (int i = 0; i < k; i++) { sxx += (i - mx) * (i - mx); sxy += (i - mx) * (y[i] - my); }
    double b = sxy / sxx, sse = 0;
    for (int i = 0; i < k; i++) { double r = y[i] - (my + b * (i - mx)); sse += r * r; }
    return (b, Math.Sqrt(sse / Math.Max(1, k - 2) / sxx));
}

static void EscribirCampeon(string archivo, sbyte[] tabla, EstadoLiga e)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"// Campeón de la liga: tanda {e.Tandas}, {e.Mesas} mesas de entrenamiento, {DateTime.Now:yyyy-MM-dd HH:mm}.");
    sb.AppendLine("// Pegar en Agents/jackpot_q.cs (o jackpot.cs) reemplazando la línea de TablaCorreccion, tras verificar con 'comparar'.");
    sb.AppendLine("private static readonly sbyte[] TablaCorreccion =");
    sb.AppendLine("{");
    for (int i = 0; i < tabla.Length; i += 36)
        sb.AppendLine("    " + string.Join(", ", tabla.Skip(i).Take(36)) + ",");
    sb.AppendLine("};");
    File.WriteAllText(archivo, sb.ToString());
}

/// <summary>Estado persistente de la liga (tabla Q, conteos, campeón y salón de la fama): permite retomar.</summary>
sealed class EstadoLiga
{
    public float[] Q = new float[jackpot_q.NumEstados * jackpot_q.NumAcciones];
    public int[] N = new int[jackpot_q.NumEstados * jackpot_q.NumAcciones];
    public int Tandas, SinMejora;
    public long Mesas;
    public sbyte[] Campeon;                       // null = v1; también es la base de los aprendices
    public List<sbyte[]> Salon = new List<sbyte[]>();
    public List<double> HistVal = new List<double>();

    public void Guardar(string archivo)
    {
        string tmp = archivo + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write(2); w.Write(Tandas); w.Write(SinMejora); w.Write(Mesas);
            foreach (var v in Q) w.Write(v);
            foreach (var v in N) w.Write(v);
            EscribirTabla(w, Campeon);
            w.Write(Salon.Count); foreach (var s in Salon) EscribirTabla(w, s);
            w.Write(HistVal.Count); foreach (var v in HistVal) w.Write(v);
        }
        File.Move(tmp, archivo, overwrite: true);   // escritura atómica: un corte no deja el estado a medias
    }

    public static EstadoLiga Cargar(string archivo)
    {
        var e = new EstadoLiga();
        using var r = new BinaryReader(File.OpenRead(archivo));
        if (r.ReadInt32() != 2) throw new InvalidDataException("Versión de estado desconocida");
        e.Tandas = r.ReadInt32(); e.SinMejora = r.ReadInt32(); e.Mesas = r.ReadInt64();
        for (int i = 0; i < e.Q.Length; i++) e.Q[i] = r.ReadSingle();
        for (int i = 0; i < e.N.Length; i++) e.N[i] = r.ReadInt32();
        e.Campeon = LeerTabla(r);
        int k = r.ReadInt32(); for (int i = 0; i < k; i++) e.Salon.Add(LeerTabla(r));
        k = r.ReadInt32(); for (int i = 0; i < k; i++) e.HistVal.Add(r.ReadDouble());
        return e;
    }

    static void EscribirTabla(BinaryWriter w, sbyte[] t)
    {
        w.Write(t == null ? -1 : t.Length);
        if (t != null) foreach (var v in t) w.Write(v);
    }

    static sbyte[] LeerTabla(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len < 0) return null;
        var t = new sbyte[len];
        for (int i = 0; i < len; i++) t[i] = r.ReadSByte();
        return t;
    }
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
