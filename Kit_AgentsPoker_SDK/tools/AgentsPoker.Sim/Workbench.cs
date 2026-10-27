using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using AgentsPoker.Core;

namespace AgentsPoker.Sim
{
    /// <summary>Per-agent accumulated statistics across matches.</summary>
    public sealed class AgentStats
    {
        public string Name;
        public int Tables, Wins, Qualified, RankSum;
        public long StackSum;
        public long Decisions;
        public double DecisionMsSum, DecisionMsMax;
        public int Timeouts, Errors;
    }

    /// <summary>Measures decision time and forwards end-of-hand info (if the inner agent wants it).</summary>
    public sealed class TimedAgent : IPokerAgent, IHandObserver
    {
        private readonly IPokerAgent _inner;
        private readonly AgentStats _stats;
        private readonly object _lock = new object();
        public TimedAgent(IPokerAgent inner, AgentStats stats) { _inner = inner; _stats = stats; }
        public void Initialize(string agentName) => _inner.Initialize(agentName);
        public ActionType GetAction(PlayerObservation o)
        {
            var sw = Stopwatch.StartNew();
            try { return _inner.GetAction(o); }
            finally
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                lock (_lock)
                {
                    _stats.Decisions++;
                    _stats.DecisionMsSum += ms;
                    if (ms > _stats.DecisionMsMax) _stats.DecisionMsMax = ms;
                }
            }
        }
        public void OnHandEnd(HandSummary s) { if (_inner is IHandObserver h) h.OnHandEnd(s); }
    }

    public sealed class RunOptions
    {
        public int Hands = 30;
        public int Qualify = 3;
        public int? Seed;
        public int TimeoutMs = 1000;
        public int StartingStack = 10000;
        public bool Verbose;
        public int Matches = 100;
        public int Tables; // tournament: 0 = auto
    }

    public static class Workbench
    {
        public sealed class TableOutcome
        {
            public List<Standing> Standings;
            public List<AgentSpec> Seats;
        }

        public static TableOutcome PlayTable(IReadOnlyList<AgentSpec> seats, RunOptions o, int? seed, Dictionary<string, AgentStats> stats, bool verbose)
        {
            var cfg = GameConfig.Tournament(seed);
            cfg.AgentTimeoutMs = o.TimeoutMs;
            cfg.StartingStack = o.StartingStack;
            var table = new PokerTable(cfg);
            foreach (var s in seats)
            {
                var st = GetStats(stats, s.Name);
                IPokerAgent agent;
                try { agent = s.Create(); }
                catch (Exception e) { throw new AgentLoadException($"{s.Name}: el constructor lanzó {e.GetType().Name}: {e.Message}"); }
                table.Seat(new TimedAgent(agent, st), s.Name);
            }
            int q = Math.Max(1, Math.Min(o.Qualify, seats.Count - 1));
            var match = new TableMatch(table, o.Hands, q);
            HandResult r;
            while ((r = match.PlayNext()) != null)
            {
                foreach (var e in r.Errors)
                {
                    var st = GetStats(stats, seats[e.Seat].Name);
                    if (e.IsTimeout) st.Timeouts++; else st.Errors++;
                    if (verbose || o.Verbose) Console.WriteLine("  !! " + e);
                }
                if (verbose) PrintHand(table, r);
            }
            var standings = match.GetStandings();
            foreach (var s in standings)
            {
                var st = GetStats(stats, seats[s.Seat].Name);
                st.Tables++;
                st.StackSum += s.Stack;
                st.RankSum += s.Rank;
                if (s.Rank == 1) st.Wins++;
                if (s.Qualifies) st.Qualified++;
            }
            return new TableOutcome { Standings = standings, Seats = seats.ToList() };
        }

        private static AgentStats GetStats(Dictionary<string, AgentStats> stats, string name)
        {
            if (!stats.TryGetValue(name, out var s)) stats[name] = s = new AgentStats { Name = name };
            return s;
        }

        public static void PrintHand(PokerTable t, HandResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Mano {r.HandNumber} (botón {t.GetName(r.ButtonSeat)}) ===");
            sb.AppendLine("  [vista espectador: todas las cartas]");
            for (int i = 0; i < t.SeatCount; i++)
                if (r.HoleCards[i] != null) sb.AppendLine($"  {t.GetName(i),-14} {r.HoleCards[i][0]} {r.HoleCards[i][1]}   stack {r.StacksBefore[i]}");
            Street? last = null;
            foreach (var a in r.Actions)
            {
                if (last != a.Street && a.Street != Street.Preflop)
                {
                    int count = a.Street == Street.Flop ? 3 : a.Street == Street.Turn ? 4 : 5;
                    sb.AppendLine($"  -- {a.Street}: {string.Join(" ", r.Board.Take(count))}");
                }
                last = a.Street;
                sb.AppendLine("    " + a);
            }
            sb.AppendLine("  Board: " + string.Join(" ", r.Board));
            if (r.WentToShowdown)
            {
                sb.AppendLine("  SHOWDOWN (cartas reveladas a TODOS los agentes vía IHandObserver.OnHandEnd):");
                for (int i = 0; i < t.SeatCount; i++)
                    if (r.HandValues[i] != 0)
                        sb.AppendLine($"    {t.GetName(i),-14} {r.HoleCards[i][0]} {r.HoleCards[i][1]}  {HandEvaluator.DescribeDetailed(r.HandValues[i])}");
            }
            else sb.AppendLine("  Sin showdown: el ganador NO muestra sus cartas.");
            foreach (var p in r.Pots)
                sb.AppendLine($"  Pot {p.Amount} -> {string.Join(", ", p.WinnerSeats.Select(w => t.GetName(w)))}{(p.IsUncontested ? " (sin disputa)" : "")}");
            sb.AppendLine("  Neto: " + string.Join("  ", Enumerable.Range(0, t.SeatCount).Select(i => $"{t.GetName(i)} {r.Net[i]:+#;-#;0}")));
            Console.Write(sb.ToString());
        }

        public static void PrintStats(IEnumerable<AgentStats> stats, int stack)
        {
            Console.WriteLine();
            Console.WriteLine($"{"Agente",-16}{"Mesas",6}{"Gana%",8}{"Clasif%",9}{"RankProm",9}{"StackProm",11}{"ms/dec",9}{"ms máx",9}{"Timeouts",9}{"Errores",8}");
            foreach (var s in stats.OrderByDescending(x => x.Tables == 0 ? 0 : x.Qualified / (double)x.Tables).ThenByDescending(x => x.StackSum / (double)Math.Max(1, x.Tables)))
            {
                double n = Math.Max(1, s.Tables);
                Console.WriteLine($"{s.Name,-16}{s.Tables,6}{100.0 * s.Wins / n,8:F1}{100.0 * s.Qualified / n,9:F1}{s.RankSum / n,9:F2}{s.StackSum / n,11:N0}" +
                                  $"{(s.Decisions == 0 ? 0 : s.DecisionMsSum / s.Decisions),9:F2}{s.DecisionMsMax,9:F1}{s.Timeouts,9}{s.Errors,8}");
            }
            Console.WriteLine($"(stack inicial {stack:N0}; Clasif% = quedó en el top N de su mesa)");
        }

        // ------------------------------------------------------------------ tournament (same rules as the Unity bracket)
        public static int[] Distribute(int n, int tables)
        {
            var sizes = new int[tables];
            for (int i = 0; i < tables; i++) sizes[i] = n / tables + (i < n % tables ? 1 : 0);
            return sizes;
        }

        public static void Tournament(List<AgentSpec> entrants, RunOptions o, Dictionary<string, AgentStats> stats)
        {
            int n = entrants.Count;
            if (n < 2) throw new AgentLoadException("El torneo necesita al menos 2 agentes.");
            int tables = o.Tables > 0 ? o.Tables : (n <= 6 ? 1 : (int)Math.Ceiling(n / 6.0));
            if (tables > n / 2) throw new AgentLoadException($"{tables} mesas para {n} agentes: alguna quedaría con menos de 2.");
            if (n > tables * 6) throw new AgentLoadException($"{n} agentes no caben en {tables} mesas (máx 6).");
            var rng = o.Seed.HasValue ? new Random(o.Seed.Value) : new Random();
            var current = entrants.OrderBy(_ => rng.Next()).ToList(); // random draw for round 1
            int round = 1, seedBase = o.Seed ?? rng.Next();
            while (true)
            {
                bool final = tables == 1 && current.Count <= 6;
                Console.WriteLine();
                Console.WriteLine(final ? "########## MESA FINAL ##########" : $"########## RONDA {round}: {current.Count} agentes, {tables} mesa(s) ({string.Join(" + ", Distribute(current.Count, tables))}) ##########");
                var buckets = Enumerable.Range(0, tables).Select(_ => new List<AgentSpec>()).ToList();
                for (int k = 0; k < current.Count; k++) buckets[k % tables].Add(current[k]);
                var qualifiersPerTable = new List<List<AgentSpec>>();
                for (int t = 0; t < tables; t++)
                {
                    var seats = buckets[t];
                    var opts = new RunOptions { Hands = o.Hands, Qualify = final ? 1 : Math.Max(1, Math.Min(o.Qualify, seats.Count - 1)), TimeoutMs = o.TimeoutMs, StartingStack = o.StartingStack, Verbose = o.Verbose };
                    var outcome = PlayTable(seats, opts, seedBase + round * 100 + t, stats, o.Verbose);
                    Console.WriteLine(final ? "  Mesa final:" : $"  Mesa {t + 1}:");
                    foreach (var s in outcome.Standings)
                        Console.WriteLine($"    #{s.Rank} {seats[s.Seat].Name,-16} {s.Stack,7:N0}{(s.Qualifies ? (final ? "  CAMPEÓN" : "  -> avanza") : "")}{(s.TiedOnStack ? "  [desempate: " + s.TieBreak + "]" : "")}");
                    qualifiersPerTable.Add(outcome.Standings.Where(s => s.Qualifies).Select(s => seats[s.Seat]).ToList());
                }
                if (final)
                {
                    Console.WriteLine();
                    Console.WriteLine($"*** CAMPEÓN: {qualifiersPerTable[0][0].Name} ***");
                    return;
                }
                var next = new List<AgentSpec>();
                for (int r = 0; r < 6; r++)
                    foreach (var q in qualifiersPerTable) if (r < q.Count) next.Add(q[r]);
                current = next;
                tables = current.Count <= 6 ? 1 : (int)Math.Ceiling(current.Count / 6.0);
                round++;
            }
        }
    }
}
