using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AgentsPoker.Sim
{
    /// <summary>A way to create fresh agent instances (one per table) + a label for reports.</summary>
    public sealed class AgentSpec
    {
        public string Name;          // display name (unique in a run)
        public string Source;        // "builtin", file path...
        public Func<IPokerAgent> Create;
        public override string ToString() => $"{Name} ({Source})";
    }

    public sealed class AgentLoadException : Exception
    {
        public AgentLoadException(string msg) : base(msg) { }
    }

    /// <summary>Loads student agents from .cs (compiled at runtime with Roslyn) or .dll, plus the SDK built-ins.</summary>
    public static class AgentLoader
    {
        public static readonly Dictionary<string, Func<IPokerAgent>> Builtins = new Dictionary<string, Func<IPokerAgent>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fold"] = () => new FoldAgent(),
            ["CheckFold"] = () => new CheckFoldAgent(),
            ["Station"] = () => new CallingStationAgent(),
            ["Raiser"] = () => new AlwaysRaiseAgent(),
            ["Random"] = () => new RandomAgent(),
            ["Heuristic"] = () => new SimpleHeuristicAgent(),
        };

        private static readonly Dictionary<string, Assembly> Cache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] Suspicious =
        {
            "System.IO", "File.", "Directory.", "System.Net", "HttpClient", "Socket", "new Thread", "Thread.Sleep",
            "Task.Run", "Parallel.", "Process.", "Environment.Exit", "System.Reflection", "Console.ReadLine",
        };

        /// <summary>Finds the Agents folder: --agents-dir, ./Agents, or Sdk/Agents found by walking up from the tool.</summary>
        public static string FindAgentsDir(string explicitDir)
        {
            if (!string.IsNullOrEmpty(explicitDir)) return Path.GetFullPath(explicitDir);
            var local = Path.Combine(Directory.GetCurrentDirectory(), "Agents");
            if (Directory.Exists(local)) return local;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentsPoker.Sdk.sln"))) return Path.Combine(dir.FullName, "Agents");
                dir = dir.Parent;
            }
            return local;
        }

        public static IEnumerable<string> AgentFiles(string agentsDir) =>
            Directory.Exists(agentsDir)
                ? Directory.GetFiles(agentsDir, "*.cs").Concat(Directory.GetFiles(agentsDir, "*.dll")).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>();

        /// <summary>Resolves "path.cs", "path.dll", "Name" (file in Agents/), with optional ":TypeName".</summary>
        public static AgentSpec Resolve(string arg, string agentsDir)
        {
            string typeName = null;
            string target = arg;
            int colon = arg.LastIndexOf(':');
            if (colon > 1) { typeName = arg.Substring(colon + 1); target = arg.Substring(0, colon); } // keep "C:\..." drive letters

            string path = null;
            if (File.Exists(target)) path = Path.GetFullPath(target);
            else
            {
                foreach (var ext in new[] { "", ".cs", ".dll" })
                {
                    var p = Path.Combine(agentsDir, target + ext);
                    if (File.Exists(p)) { path = p; break; }
                }
            }
            if (path == null)
            {
                // maybe it is a class name inside some file of Agents/
                foreach (var f in AgentFiles(agentsDir))
                {
                    try
                    {
                        var t = AgentTypes(LoadAssembly(f)).FirstOrDefault(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                        if (t != null) return SpecFor(t, f);
                    }
                    catch (AgentLoadException) { }
                }
                throw new AgentLoadException($"Agente '{arg}' no encontrado (ni como archivo ni en {agentsDir}). Usa 'list' para ver los disponibles.");
            }

            var asm = LoadAssembly(path);
            var types = AgentTypes(asm).ToList();
            if (types.Count == 0)
                throw new AgentLoadException($"{Path.GetFileName(path)}: no hay ninguna clase pública, no abstracta, con constructor sin parámetros que implemente IPokerAgent.");
            Type chosen = typeName == null ? types[0] : types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (chosen == null)
                throw new AgentLoadException($"{Path.GetFileName(path)}: no contiene el tipo '{typeName}'. Tipos: {string.Join(", ", types.Select(t => t.Name))}");
            return SpecFor(chosen, path);
        }

        private static AgentSpec SpecFor(Type t, string path) => new AgentSpec
        {
            Name = t.Name,
            Source = Path.GetFileName(path),
            Create = () => (IPokerAgent)Activator.CreateInstance(t),
        };

        public static AgentSpec Builtin(string name)
        {
            if (!Builtins.TryGetValue(name, out var f))
                throw new AgentLoadException($"Builtin '{name}' no existe. Opciones: {string.Join(", ", Builtins.Keys)}");
            var key = Builtins.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            return new AgentSpec { Name = key, Source = "builtin", Create = f };
        }

        public static IEnumerable<Type> AgentTypes(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            return types.Where(t => t.IsPublic && t.IsClass && !t.IsAbstract && typeof(IPokerAgent).IsAssignableFrom(t)
                                    && t.GetConstructor(Type.EmptyTypes) != null);
        }

        public static Assembly LoadAssembly(string path)
        {
            path = Path.GetFullPath(path);
            if (Cache.TryGetValue(path, out var cached)) return cached;
            Assembly asm;
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try { asm = Assembly.LoadFrom(path); }
                catch (Exception e) { throw new AgentLoadException($"{Path.GetFileName(path)}: no se pudo cargar la DLL: {e.Message}"); }
            }
            else asm = Compile(path);
            Cache[path] = asm;
            return asm;
        }

        public static List<string> Warnings(string path)
        {
            var list = new List<string>();
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // The Arena runs on Unity's Mono: a DLL built for .NET 5+ loads here but not there.
                var fw = LoadAssembly(path).GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName ?? "";
                if (fw.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
                    list.Add($"compilada para {fw}; la Arena de Unity solo carga DLLs netstandard2.1 (mejor entrega el .cs)");
                return list;
            }
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return list;
            var code = File.ReadAllText(path);
            foreach (var s in Suspicious)
                if (code.Contains(s)) list.Add($"usa '{s}' (no permitido en el torneo: sin archivos, red, hilos ni reflexión)");
            var types = AgentTypes(LoadAssembly(path)).ToList();
            if (types.Count > 1)
                list.Add($"tiene {types.Count} clases agente; la Arena usa solo la PRIMERA ({types[0].Name}). Deja una por archivo.");
            return list;
        }

        /// <summary>netstandard 2.1 reference assembly copied next to the tool by the csproj (ref/netstandard.dll).</summary>
        private static readonly string NetStandardRef = Path.Combine(AppContext.BaseDirectory, "ref", "netstandard.dll");
        private static bool _warnedNoNetStandard;

        /// <summary>
        /// Same rules as the Unity Arena (Assets/Scripts/Arena/BrainLoader.cs): C# 9 and only the .NET Standard 2.1 API (Mono).
        /// Code that compiles here compiles in the Arena; newer syntax (file-scoped namespaces, records structs, [1, 2] ...) or
        /// .NET 5+ APIs (Random.Shared, MaxBy, PriorityQueue ...) fail here too instead of failing on tournament day.
        /// </summary>
        private static Assembly Compile(string path)
        {
            var code = File.ReadAllText(path);
            var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp9), path);
            var refs = new List<MetadataReference>();
            if (File.Exists(NetStandardRef)) refs.Add(MetadataReference.CreateFromFile(NetStandardRef));
            else
            {
                if (!_warnedNoNetStandard)
                    Console.WriteLine($"  aviso: falta {NetStandardRef}; se compila contra todo .NET (la Arena es más estricta). Recompila el workbench.");
                _warnedNoNetStandard = true;
                var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator);
                foreach (var r in tpa) if (!string.IsNullOrEmpty(r) && File.Exists(r)) refs.Add(MetadataReference.CreateFromFile(r));
            }
            refs.Add(MetadataReference.CreateFromFile(typeof(IPokerAgent).Assembly.Location));
            var name = "StudentAgent_" + Path.GetFileNameWithoutExtension(path) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var comp = CSharpCompilation.Create(name, new[] { tree }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Disable));
            using var ms = new MemoryStream();
            var result = comp.Emit(ms);
            if (!result.Success)
            {
                var errs = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(15)
                    .Select(d =>
                    {
                        var span = d.Location.GetLineSpan();
                        return $"  {Path.GetFileName(path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): {d.Id}: {d.GetMessage()}";
                    });
                throw new AgentLoadException($"{Path.GetFileName(path)} no compila:\n{string.Join("\n", errs)}");
            }
            return Assembly.Load(ms.ToArray());
        }
    }
}
