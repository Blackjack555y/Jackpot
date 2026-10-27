using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;
using AgentsPoker.Sim;

// AgentsPoker workbench. Run from Sdk/:  dotnet run -c Release --project tools/AgentsPoker.Sim -- <comando> [opciones]
//   list                                    agentes en Agents/ + builtins
//   match  --agent MiAgente --builtin Heuristic --hands 30 --seed 42 [--verbose]
//   bench  --agent MiAgente --agent Rival --builtin Heuristic --matches 200
//   tournament --all --builtin Heuristic --builtin Station --qualify 3
//   check  Agents/MiAgente.cs               solo compilar
//   speed  1000000                          benchmark del motor (antes: bench <n>)
Console.OutputEncoding = System.Text.Encoding.UTF8;
try { return Cli.Run(args); }
catch (AgentLoadException e) { Console.Error.WriteLine("ERROR: " + e.Message); return 2; }
catch (ArgumentException e) { Console.Error.WriteLine("ERROR: " + e.Message); Console.Error.WriteLine("Usa 'help' para ver los comandos."); return 1; }

static class Cli
{
    public static int Run(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToList();

        // legacy positional forms: bench 1000000 | match 30 | verbose 3
        if (rest.Count == 1 && int.TryParse(rest[0], out int legacyN))
        {
            if (mode == "bench" || mode == "speed") { Speed(legacyN); return 0; }
            if (mode == "match") rest = new List<string> { "--hands", rest[0] };
            if (mode == "verbose") { mode = "match"; rest = new List<string> { "--hands", rest[0], "--verbose" }; }
        }
        else if (mode == "verbose") { mode = "match"; rest.Add("--verbose"); }

        var p = Parse(rest);
        string dir = AgentLoader.FindAgentsDir(p.Get("agents-dir"));
        switch (mode)
        {
            case "list": List(dir); return 0;
            case "check": return Check(p, dir);
            case "speed": Speed(p.Int("hands", 200_000)); return 0;
            case "match": return Match(p, dir);
            case "bench": return Bench(p, dir);
            case "tournament": return TournamentCmd(p, dir);
            default: Help(dir); return mode == "help" || mode == "--help" || mode == "-h" ? 0 : 1;
        }
    }

    // ------------------------------------------------------------------ args
    sealed class Args
    {
        public readonly List<(string key, string value)> Items = new List<(string, string)>();
        public string Get(string k) => Items.LastOrDefault(i => i.key == k).value;
        public IEnumerable<string> All(string k) => Items.Where(i => i.key == k).Select(i => i.value);
        public bool Has(string k) => Items.Any(i => i.key == k);
        public int Int(string k, int def)
        {
            var v = Get(k);
            if (v == null) return def;
            if (!int.TryParse(v, out int x)) throw new ArgumentException($"--{k} espera un número (recibió '{v}').");
            return x;
        }
        public List<string> Positional = new List<string>();
    }

    static readonly HashSet<string> Flags = new HashSet<string> { "verbose", "fast", "all", "no-timeout" };

    static Args Parse(List<string> a)
    {
        var r = new Args();
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].StartsWith("--"))
            {
                var k = a[i].Substring(2).ToLowerInvariant();
                if (Flags.Contains(k)) { r.Items.Add((k, "true")); continue; }
                if (i + 1 >= a.Count) throw new ArgumentException($"--{k} necesita un valor.");
                r.Items.Add((k, a[++i]));
            }
            else r.Positional.Add(a[i]);
        }
        return r;
    }

    static RunOptions Options(Args p) => new RunOptions
    {
        Hands = p.Int("hands", 30),
        Qualify = p.Int("qualify", 3),
        Seed = p.Get("seed") != null ? p.Int("seed", 0) : (int?)null,
        TimeoutMs = p.Has("fast") || p.Has("no-timeout") ? 0 : p.Int("timeout", 1000),
        StartingStack = p.Int("stack", 10000),
        Verbose = p.Has("verbose"),
        Matches = p.Int("matches", 100),
        Tables = p.Int("tables", 0),
    };

    static List<AgentSpec> Lineup(Args p, string dir, bool allowDefault)
    {
        var specs = new List<AgentSpec>();
        if (p.Has("all"))
            foreach (var f in AgentLoader.AgentFiles(dir)) specs.Add(Load(f, dir));
        foreach (var a in p.All("agent").Concat(p.Positional)) specs.Add(Load(a, dir));
        foreach (var b in p.All("builtin"))
        {
            var parts = b.Split('*');
            int count = parts.Length == 2 && int.TryParse(parts[1], out int c) ? c : 1;   // Heuristic*3
            for (int i = 0; i < count; i++) specs.Add(AgentLoader.Builtin(parts[0]));
        }
        if (specs.Count == 0 && allowDefault)
            foreach (var b in new[] { "Random", "Random", "Station", "Raiser", "Heuristic", "Heuristic" }) specs.Add(AgentLoader.Builtin(b));
        // unique names
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
        {
            seen.TryGetValue(s.Name, out int k);
            seen[s.Name] = k + 1;
            if (k > 0) s.Name = s.Name + "#" + (k + 1);
        }
        return specs;
    }

    static AgentSpec Load(string arg, string dir)
    {
        var spec = AgentLoader.Resolve(arg, dir);
        var path = File.Exists(arg) ? arg : Path.Combine(dir, spec.Source);
        foreach (var w in AgentLoader.Warnings(path)) Console.WriteLine($"  aviso: {spec.Source} {w}");
        return new AgentSpec { Name = spec.Name, Source = spec.Source, Create = spec.Create };
    }

    // ------------------------------------------------------------------ commands
    static string CoreVersion => typeof(IPokerAgent).Assembly.GetName().Version.ToString(3);

    static void Help(string dir)
    {
        Console.WriteLine(@"AgentsPoker workbench (Limit Hold'em, reglas y timeouts del torneo) - motor AgentsPoker.Core v" + CoreVersion + @"
Los .cs se compilan como en la Arena de Unity: C# 9 y API .NET Standard 2.1.

Comandos (desde la carpeta Sdk):
  dotnet run -c Release --project tools/AgentsPoker.Sim -- list
  dotnet run -c Release --project tools/AgentsPoker.Sim -- check Agents/MiAgente.cs
  dotnet run -c Release --project tools/AgentsPoker.Sim -- match --agent MiAgente --agent Rival --builtin Heuristic*2 --hands 30 --seed 42 [--verbose]
  dotnet run -c Release --project tools/AgentsPoker.Sim -- bench --agent MiAgente --builtin Heuristic*5 --matches 200 [--fast]
  dotnet run -c Release --project tools/AgentsPoker.Sim -- tournament --all --builtin Heuristic*4 --builtin Station*3 --qualify 3
  dotnet run -c Release --project tools/AgentsPoker.Sim -- speed 1000000

Agentes:  --agent <Nombre | ruta.cs | ruta.dll>[:Clase]   (repetible; Nombre = archivo o clase en Agents/)
          --builtin <Fold|CheckFold|Station|Raiser|Random|Heuristic>[*N]
          --all   todos los agentes de Agents/
Opciones: --hands 30  --qualify 3  --seed N  --stack 10000  --timeout 1000  --fast (sin timeout, para entrenar)
          --matches 100 (bench)  --tables N (torneo; 0 = auto)  --verbose  --agents-dir <carpeta>
Carpeta de agentes: " + dir);
    }

    static void List(string dir)
    {
        Console.WriteLine($"AgentsPoker.Core v{CoreVersion} | compilación como la Arena: C# 9 + .NET Standard 2.1");
        Console.WriteLine($"Agentes en {dir}:");
        var files = AgentLoader.AgentFiles(dir).ToList();
        if (files.Count == 0) Console.WriteLine("  (vacío) copia aquí archivos .cs de agentes");
        foreach (var f in files)
        {
            try
            {
                var types = AgentLoader.AgentTypes(AgentLoader.LoadAssembly(f)).ToList();
                var obs = types.Select(t => t.Name + (typeof(IHandObserver).IsAssignableFrom(t) ? " [OnHandEnd]" : ""));
                Console.WriteLine($"  {Path.GetFileName(f),-24} OK   {(types.Count == 0 ? "(sin clases IPokerAgent)" : string.Join(", ", obs))}");
                foreach (var w in AgentLoader.Warnings(f)) Console.WriteLine($"  {"",-24}      aviso: {w}");
            }
            catch (AgentLoadException e) { Console.WriteLine($"  {Path.GetFileName(f),-24} FALLA\n{e.Message}"); }
        }
        Console.WriteLine("Builtins: " + string.Join(", ", AgentLoader.Builtins.Keys));
    }

    static int Check(Args p, string dir)
    {
        int bad = 0;
        foreach (var a in p.Positional.Concat(p.All("agent")))
        {
            try { var s = Load(a, dir); Console.WriteLine($"OK  {s.Source}: {s.Name}"); }
            catch (AgentLoadException e) { Console.WriteLine("FALLA " + e.Message); bad++; }
        }
        return bad == 0 ? 0 : 2;
    }

    static int Match(Args p, string dir)
    {
        var o = Options(p);
        var lineup = Lineup(p, dir, allowDefault: true);
        CheckSize(lineup);
        Console.WriteLine($"Mesa: {string.Join(", ", lineup.Select(s => s.Name))} | {o.Hands} manos | top {Math.Min(o.Qualify, lineup.Count - 1)} | timeout {(o.TimeoutMs > 0 ? o.TimeoutMs + " ms" : "OFF")}{(o.Seed.HasValue ? " | seed " + o.Seed : "")}");
        var stats = new Dictionary<string, AgentStats>();
        var outcome = Workbench.PlayTable(lineup, o, o.Seed, stats, o.Verbose);
        Console.WriteLine($"\nMesa terminada:");
        foreach (var s in outcome.Standings)
            Console.WriteLine($"  #{s.Rank} {lineup[s.Seat].Name,-16} {s.Stack,7:N0}{(s.Qualifies ? "  (clasifica)" : "")}{(s.TiedOnStack ? "  [desempate: " + s.TieBreak + "]" : "")}");
        Workbench.PrintStats(stats.Values, o.StartingStack);
        return 0;
    }

    static int Bench(Args p, string dir)
    {
        var o = Options(p);
        var lineup = Lineup(p, dir, allowDefault: true);
        CheckSize(lineup);
        var rng = o.Seed.HasValue ? new Random(o.Seed.Value) : new Random();
        var stats = new Dictionary<string, AgentStats>();
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Bench: {o.Matches} mesas de {o.Hands} manos, asientos rotados al azar, timeout {(o.TimeoutMs > 0 ? o.TimeoutMs + " ms" : "OFF")}");
        for (int m = 0; m < o.Matches; m++)
        {
            var seats = lineup.OrderBy(_ => rng.Next()).ToList();
            Workbench.PlayTable(seats, o, rng.Next(), stats, false);
            if ((m + 1) % Math.Max(1, o.Matches / 10) == 0) Console.Write($"\r  {m + 1}/{o.Matches} mesas...");
        }
        Console.WriteLine($"\r  {o.Matches} mesas en {sw.Elapsed.TotalSeconds:F1}s            ");
        Workbench.PrintStats(stats.Values, o.StartingStack);
        return 0;
    }

    static int TournamentCmd(Args p, string dir)
    {
        var o = Options(p);
        var lineup = Lineup(p, dir, allowDefault: false);
        if (lineup.Count < 2) throw new ArgumentException("El torneo necesita al menos 2 agentes (usa --all, --agent, --builtin).");
        Console.WriteLine($"Torneo: {lineup.Count} agentes | {o.Hands} manos por mesa | top {o.Qualify} avanza | stack {o.StartingStack:N0} (se reinicia en cada mesa)");
        var stats = new Dictionary<string, AgentStats>();
        Workbench.Tournament(lineup, o, stats);
        Workbench.PrintStats(stats.Values, o.StartingStack);
        return 0;
    }

    static void CheckSize(List<AgentSpec> lineup)
    {
        if (lineup.Count < 2 || lineup.Count > 6)
            throw new ArgumentException($"Una mesa necesita entre 2 y 6 agentes (hay {lineup.Count}). Para más usa 'tournament'.");
    }

    // ------------------------------------------------------------------ engine speed (old 'bench <n>')
    static void Speed(int hands)
    {
        var cfg = GameConfig.Training(seed: 123);
        cfg.IncludeHistoryInObservation = false;
        var t = new PokerTable(cfg);
        t.Seat(new RandomAgent(1), "Random1"); t.Seat(new RandomAgent(2), "Random2"); t.Seat(new CallingStationAgent(), "Station");
        t.Seat(new AlwaysRaiseAgent(), "Maniac"); t.Seat(new SimpleHeuristicAgent(), "Heuristic1"); t.Seat(new SimpleHeuristicAgent(), "Heuristic2");
        var sw = Stopwatch.StartNew();
        for (int h = 0; h < hands; h++) { if (!t.CanPlayHand) t.ResetStacks(); t.PlayHand(); }
        sw.Stop();
        Console.WriteLine($"{hands:N0} manos en {sw.Elapsed.TotalSeconds:F2}s => {hands / sw.Elapsed.TotalSeconds:N0} manos/s (6-max)");
        var rng = new Random(1); var deck = new Deck(); int acc = 0;
        var h7 = new Card[100_000][];
        for (int i = 0; i < h7.Length; i++) { deck.Shuffle(rng); h7[i] = Enumerable.Range(0, 7).Select(_ => deck.Draw()).ToArray(); }
        sw.Restart();
        for (int rep = 0; rep < 50; rep++) foreach (var h in h7) acc ^= HandEvaluator.Evaluate(h);
        Console.WriteLine($"Evaluador: {sw.Elapsed.TotalMilliseconds * 1e6 / (50.0 * h7.Length):F1} ns / mano de 7 cartas (chk {acc & 1})");
    }
}
