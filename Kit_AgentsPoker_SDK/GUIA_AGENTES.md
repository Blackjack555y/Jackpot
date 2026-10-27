# Guía de desarrollo de agentes IA — Torneo Limit Texas Hold'em (ElementalPoker / AgentsPoker)

> Basada en el documento original del profesor y **verificada contra el código real del motor** (`AgentsPoker.Core` v0.3.0, el
> mismo que usa la Arena de Unity). Instalación y uso del kit: [LEEME.md](LEEME.md); reglas finas del motor: [REGLAS.md](REGLAS.md).
> Los cambios respecto al texto original están al final
> ([Correcciones](#9-correcciones-respecto-al-documento-original)).

## 1. Objetivo

Programar un agente **autónomo** de *Limit* Texas Hold'em para mesas de **hasta 6 jugadores** que compite contra los agentes de
tus compañeros en un torneo en vivo.

**Cómo se gana el torneo** (no es "acumular la mayor cantidad de fichas en total"):

- Cada mesa juega **30 manos** (`TableMatch`, valor por defecto `handsToPlay = 30`; el árbitro puede configurarlo).
- Al terminar, la mesa se ordena por **stack final**. Avanzan los **top N** (por defecto 3; configurable en el torneo).
  Desempate: el stack en manos anteriores (quien tuvo más fichas más recientemente) y luego el número de asiento.
- En la siguiente ronda **todos vuelven a empezar con 10 000 fichas**: lo que importa es **quedar en zona de clasificación en
  cada mesa**, no la cantidad absoluta. Ganar 3 000 fichas o 300 da lo mismo si quedas 2º.
- La mesa final decide el campeón (1º por stack); el organizador puede añadir un "Jefe final" del juego.

## 2. Reglas que ve tu agente (valores reales del SDK)

| Concepto | Valor (`GameConfig`) |
|---|---|
| Ciegas | `SmallBlind = 10`, `BigBlind = 20` |
| Incremento fijo | `SmallBet = 20` (preflop y flop), `BigBet = 40` (turn y river) |
| Cap por calle | `MaxBetsPerStreet = 4` → 1 bet + 3 raises. **Preflop la ciega grande cuenta como la apuesta #1** |
| Stack inicial | `StartingStack = 10000` (se reinicia en cada mesa) |
| Tiempo por decisión | `AgentTimeoutMs = 1000` en modo torneo (`GameConfig.Tournament()`); 0 = sin límite en entrenamiento |
| Asientos | el motor admite hasta 10, **el torneo usa 2–6** por mesa |

### La interfaz (obligatoria)
```csharp
using AgentsPoker.Core;

public interface IPokerAgent
{
    void Initialize(string agentName);                 // una vez al sentarse (no cronometrado; las excepciones se capturan)
    ActionType GetAction(PlayerObservation observation); // en cada turno
}

public enum ActionType { FOLD = 0, CALL = 1, RAISE = 2 }
```
- `CALL` sin nada que pagar = **CHECK**. Si no te alcanzan las fichas = call all-in.
- `RAISE` sin apuesta previa = **BET**; si ya hay apuesta = sube **exactamente un incremento**. Si no tienes suficiente = all-in.
- Tú nunca eliges la cantidad: el motor la calcula.

### Qué pasa si devuelves algo ilegal (no es un error: el motor lo **convierte**)
| Devuelves | Situación | Se aplica | `ActionRecord.Coercion` |
|---|---|---|---|
| RAISE | calle con cap alcanzado | CALL/CHECK | `RaiseCapped` |
| RAISE | ya actuaste y solo hubo all-in cortos después | CALL | `RaiseNotReopened` |
| RAISE | tu stack ≤ lo que debes | CALL all-in | `RaiseInsufficientChips` |
| RAISE | todos los rivales están all-in | CALL/CHECK | `RaiseNoOpponentCanAct` |
| FOLD | no debes nada | **FOLD real** (legal pero absurdo) | `None` |
| valor fuera de 0/1/2 | — | CHECK si es gratis, si no FOLD | `InvalidAction` |
| lanzas excepción | — | CHECK si es gratis, si no FOLD (+ `AgentError`) | `AgentException` |
| tardas > 1000 ms | modo torneo | CHECK si es gratis, si no FOLD (+ `AgentError`) | `Timeout` |

Antes de decidir puedes saber exactamente qué es legal: `o.CanRaise`, `o.LegalActions`, `o.IsLegal(a)`.

### `PlayerObservation` (lo que sabe tu agente) — nombres reales
- Mano: `HandNumber`, `Seat`, `ButtonSeat`, `SmallBlindSeat`, `BigBlindSeat`, `HoleCards` (tus 2 cartas), `Board` (0/3/4/5), `Street`.
- Apuestas: `Pot`, `CurrentBet`, `ToCall`, `RaiseCost` (fichas que pondrías con RAISE), `BetSize` (20 o 40 en esta calle),
  `BetsThisStreet`, `MaxBetsPerStreet`, `CanRaise`, `LegalActions`.
- Tú: `MyStack`, `MyCommittedThisStreet`, `MyCommittedThisHand`, `Me`.
- Mesa: `Players` (lista de `PublicPlayerState`: `Seat, Name, Stack, CommittedThisStreet, CommittedThisHand, IsInHand,
  HasFolded, IsAllIn, IsButton, IsActive`), `ActionHistory` (acciones públicas de la mano), `SmallBlind`, `BigBlind`.
- Atajos: `IsFacingBet`, `ActivePlayerCount`, `PotOdds` (= `ToCall / (Pot + ToCall)`).

**Visibilidad:**
- **Durante la mano** tu agente solo ve sus 2 cartas, el board y la información pública (stacks de todos, apuestas, historial
  `ActionHistory` de la mano actual).
- **Al terminar la mano** (si implementas `IHandObserver`, ver abajo) recibes el historial completo y **las cartas de todos los
  jugadores que llegaron al showdown**, aunque tú hayas foldeado. Las cartas de quien foldeó **nunca** se revelan, y si todos
  foldean ante una apuesta, el ganador **no** muestra sus cartas.
- En la Arena de Unity el **público** ve todas las cartas boca arriba; eso es solo para los espectadores.

### Información al final de la mano: `IHandObserver` (opcional, recomendado)
```csharp
public interface IHandObserver { void OnHandEnd(HandSummary summary); }
```
Si tu clase implementa también `IHandObserver`, el motor la llama una vez por mano, después de repartir el pozo, a **todos** los
agentes de la mesa. `HandSummary` (solo lectura):

| Propiedad | Contenido |
|---|---|
| `HandNumber`, `MySeat`, `ButtonSeat`, `SmallBlindSeat`, `BigBlindSeat` | identificación de la mano y tu asiento |
| `PlayerNames[seat]` | nombres (úsalos como clave: el asiento cambia entre mesas) |
| `Board` | cartas comunitarias finales (menos de 5 si la mano terminó antes) |
| `WentToShowdown` | true si 2+ jugadores llegaron al final |
| `RevealedCards[seat]` / `CardsOf(seat)` | 2 cartas si ese jugador llegó al showdown; `null` si foldeó, ganó sin disputa o no jugó |
| `HandValues[seat]` | valor `HandEvaluator` de las manos reveladas (0 si no) |
| `Actions` / `ActionsOn(street)` | todas las acciones (`Seat`, `PlayerName`, `Street`, `Kind`, `Amount`, `IsAllIn`…) incluidas ciegas |
| `Pots`, `Winners`, `Net`, `StacksBefore`, `StacksAfter` | botes y ganadores, neto de fichas por asiento, stacks |

Mismas reglas de seguridad que `GetAction`: si lanza excepción o tarda más de 1000 ms (torneo) se registra un error, pero **no**
afecta tus fichas. Mantenlo rápido (actualizar contadores, no simular). Los agentes que no implementan la interfaz siguen
funcionando igual.

### Palos clásicos vs. elementos de la Arena

| Palo clásico (código) | Símbolo | `Suit` | Letra en `Card.ToString()` | Elemento en la Arena |
|---|---|---|---|---|
| Corazones (Hearts) | ♥ | `Suit.Hearts` | `h` (ej. `Ah`) | **Fuego** |
| Diamantes (Diamonds) | ♦ | `Suit.Diamonds` | `d` (ej. `Td`) | **Agua** |
| Tréboles (Clubs) | ♣ | `Suit.Clubs` | `c` (ej. `7c`) | **Tierra** |
| Picas (Spades) | ♠ | `Suit.Spades` | `s` (ej. `Ks`) | **Aire** |

Los palos no tienen jerarquía en póker, así que las reglas no cambian: el elemento es solo arte. Tu agente **solo ve los palos
clásicos** (`Suit` / letra); nunca recibe el nombre del elemento. La notación es rango + letra: `2..9`, `T`, `J`, `Q`, `K`, `A`
seguido de `c`, `d`, `h`, `s`.

## 3. Formatos de entrega

**A) C# puro (`MiAgente.cs`)** — una clase que implementa `IPokerAgent`. Ideal para heurísticas, Monte Carlo, ISMCTS.

**B) Red neuronal (`MiModelo.onnx`)** — entrenada en Python y ejecutada en Unity con Sentis.
> ⚠️ **Aún no implementado en la Arena (previsto para la próxima iteración).** El `SentisWrapper` y la codificación oficial del tensor están pendientes. Abajo hay
> una **propuesta de contrato** (sección 6) para que puedas empezar a entrenar; puede cambiar antes del torneo.

## 4. Inicio rápido y banco de pruebas (workbench)

### 4.1 La plantilla `Agents/MiAgente.cs`
Verificada contra el SDK v0.3.0: equity Monte Carlo vs rivales activos + pot odds, y perfil de rivales con `OnHandEnd`
(VPIP, agresión, showdowns). Cópiala, cambia el nombre de la clase y mejórala.

```csharp
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
```

### 4.2 Workbench (`tools/AgentsPoker.Sim`)
Carga tu agente directamente desde el `.cs` (lo compila con Roslyn; no necesitas crear proyecto) o desde una `.dll`, y lo juega
con **las mismas reglas del torneo**: timeout real de 1000 ms por decisión, excepción/timeout → CHECK si es gratis o FOLD,
30 manos, top N clasifica, stack 10 000 que se reinicia en cada mesa. Compila tu `.cs` **igual que la Arena de Unity**
(C# 9 y API .NET Standard 2.1): si pasa aquí, compila el día del torneo.

Todos los comandos se ejecutan desde la carpeta del kit (la que tiene `LEEME.md`) con `.\run.cmd …` en Windows o
`bash run.sh …` en Linux/macOS, que equivalen a:
```
dotnet run -c Release --project tools/AgentsPoker.Sim -- <comando> [opciones]
```

| Comando | Para qué |
|---|---|
| `help` | ayuda |
| `list` | agentes en `Agents/` (compila cada uno, marca `[OnHandEnd]`, avisos) + builtins |
| `check Agents/MiAgente.cs` | solo compilar y mostrar errores con línea/columna |
| `match …` | una mesa (2–6 asientos); `--verbose` muestra cada mano, acciones, showdown y neto |
| `bench …` | muchas mesas con asientos rotados → estadísticas |
| `tournament …` | mini torneo con las reglas del bracket de Unity (mesas balanceadas, top N avanza, mesa final) |
| `speed 1000000` | velocidad del motor y del evaluador |

Opciones:
- `--agent <Nombre | ruta.cs | ruta.dll>[:Clase]` (repetible). `Nombre` = archivo o clase dentro de `Agents/`.
- `--builtin <Fold|CheckFold|Station|Raiser|Random|Heuristic>[*N]` (p. ej. `Heuristic*3`). `--all` = todos los de `Agents/`.
- `--hands 30` · `--qualify 3` · `--seed 42` (repetible) · `--stack 10000` · `--timeout 1000` · `--fast` (sin timeout, para entrenar)
- `--matches 100` (bench) · `--tables N` (torneo; 0 = automático) · `--verbose` · `--agents-dir <carpeta>`

Ejemplos (salida real):
```
> ... -- list
AgentsPoker.Core v0.3.0 | compilación como la Arena: C# 9 + .NET Standard 2.1
Agentes en ...\Agents:
  MiAgente.cs              OK   MiAgente [OnHandEnd]
  Rival.cs                 OK   Rival
Builtins: Fold, CheckFold, Station, Raiser, Random, Heuristic

> ... -- match --agent MiAgente --agent Rival --builtin Heuristic*2 --builtin Station --builtin Raiser --hands 30 --seed 42
Mesa terminada:
  #1 MiAgente          13,500  (clasifica)
  #2 Station            9,840  (clasifica)
  #3 Heuristic#2        9,780  (clasifica)  [desempate: StackHistory]
  #4 Heuristic          9,780  [desempate: StackHistory]
  #5 Rival              9,500
  #6 Raiser             7,600
Agente           Mesas   Gana%  Clasif% RankProm  StackProm   ms/dec   ms máx Timeouts Errores
MiAgente             1   100.0    100.0     1.00     13,500     7.12     16.5        0       0
...

> ... -- match --agent MiAgente --builtin Heuristic*2 --hands 3 --verbose
=== Mano 1 (botón MiAgente) ===
    [Preflop] Rival(s1) SmallBlind 10
    ...
  SHOWDOWN (cartas reveladas a TODOS los agentes vía IHandObserver.OnHandEnd):
    MiAgente       7d Ac  Pair of K (kickers A Q T)
    Station        9s 8h  Two Pair (K and 9, Q kicker)
  Pot 70 -> Station

> ... -- tournament --all --builtin Heuristic*3 --builtin Station*2 --builtin Raiser --builtin Random --seed 3
########## RONDA 1: 9 agentes, 2 mesa(s) (5 + 4) ##########
  ...
########## MESA FINAL ##########
*** CAMPEÓN: MiAgente ***
```
Columnas de estadísticas: `Gana%` (1º de la mesa), `Clasif%` (quedó en el top N), `RankProm`, `StackProm`, `ms/dec` y
`ms máx` (tiempo de decisión), `Timeouts`, `Errores` (excepciones en `GetAction`/`OnHandEnd`).

Si tu `.cs` no compila, verás el error exacto, p. ej.:
```
FALLA Roto.cs no compila:
  Roto.cs(4,62): CS1061: "PlayerObservation" no contiene una definición para "NoExiste" ...
```
El workbench también avisa si el código usa APIs prohibidas en el torneo (`System.IO`, red, `Thread.Sleep`, `Task.Run`, reflexión…).

### 4.3 Agregar el agente de un compañero
1. Copia su archivo (`NeuroBluff.cs` o `NeuroBluff.dll`) en `Agents/`.
2. `... -- list` para comprobar que compila.
3. `... -- match --agent MiAgente --agent NeuroBluff --builtin Heuristic*2` o `... -- tournament --all`.

Cada archivo `.cs` se compila por separado, así que dos compañeros pueden usar los mismos nombres de clases auxiliares.
Si un archivo tiene varias clases agente, elige una con `--agent Archivo:Clase` (la Arena usa siempre la **primera**).

### 4.4 Laboratorio con breakpoints: `Entrenamiento/`
Para depurar paso a paso o entrenar con tu propio código usa el proyecto `Entrenamiento/` del kit (`.\entrenar.cmd`, ver
[LEEME.md](LEEME.md) §5): compila todos los `.cs` de `Agents/` y usa `PokerTable` + `TableMatch` directamente.
Para jugar muchas manos rápido usa `GameConfig.Training(seed)` (sin timeout); `GameConfig.Tournament(seed)` aplica los 1000 ms.

## 5. Técnicas por nivel

### Nivel 1 — Reglas + probabilidad (teoría de decisión)
- No uses A\*, UCS ni búsqueda no informada: el póker no tiene un "estado objetivo" y hay información oculta; además el árbol
  explota combinatoriamente.
- **Equity por Monte Carlo:** reparte cartas aleatorias **a cada rival activo** (`ActivePlayerCount - 1`, no a uno solo) y completa
  el board miles de veces; equity = victorias + empates repartidos, dividido entre simulaciones. Tu equity contra 5 rivales es
  mucho menor que contra 1.
- **Pot odds:** `ToCall / (Pot + ToCall)` (ya disponible como `o.PotOdds`). Si `equity ≥ pot odds`, pagar es rentable a largo plazo.
  Es la regla para **CALL vs FOLD**; para **RAISE** exige más equity (o razones de posición/agresión), no basta superar las pot odds.
- Nunca hagas FOLD si `ToCall == 0`: el motor lo aplica como fold real.
- **Evaluador:** el SDK ya trae `HandEvaluator` (tablas de búsqueda, misma numeración que Cactus Kev):
  `HandEvaluator.Evaluate(h0, h1, board)` (5–7 cartas, mayor = mejor, igual = empate), `HandEvaluator.Describe(v)`,
  `HandEvaluator.ToEquivalenceClass(v)` (1 = escalera real … 7462 = 7-5-4-3-2). No necesitas implementar Cactus Kev.

### Nivel 2 — ISMCTS (Information Set MCTS)
- Los nodos son **conjuntos de información** (lo que tú sabes), no estados completos.
- **Determinización:** en cada iteración reparte cartas ocultas consistentes con lo observado y simula.
- Selección con **UCB1** (exploración vs explotación). El espacio de acciones ya es pequeño (3 acciones; con el cap, a veces 2).
- **Presupuesto de tiempo:** mide con `Stopwatch` y corta ~600–700 ms (deja margen: la Arena corre en Unity/Mono y la carga de la máquina en vivo varía).
  ```csharp
  var reloj = System.Diagnostics.Stopwatch.StartNew();
  while (reloj.ElapsedMilliseconds < 650) { /* una iteración */ }
  ```
  Tras 1000 ms el motor aplica CHECK/FOLD y tu hilo sigue corriendo en segundo plano (no se puede matar), restándote CPU.

### Nivel 3 — Deep RL (DQN, PPO…)
- Un MDP tabular no escala: codifica el estado en un vector (one-hot de cartas + números normalizados; ver sección 6).
- Salida de tamaño 3: `[FOLD, CALL, RAISE]`. **Enmascara acciones ilegales** antes del argmax/muestreo (ver sección 6).
- **Recompensa:** el cambio de fichas de la mano (`HandResult.Net[seat]`, o `StacksAfter - StacksBefore`). Las ciegas **ya** están
  incluidas en ese neto, así que no hace falta añadir penalizaciones artificiales. No castigues el FOLD por sí mismo: foldear
  una mano mala es correcto; el agente "cobarde" suele venir de rivales de entrenamiento demasiado agresivos o de poca
  exploración. Entrena con self-play + mezcla de agentes base (`CallingStation`, `AlwaysRaise`, `SimpleHeuristic`).
- Opcional: recompensa al final de la mesa según si **clasificas** (top N), que es el objetivo real del torneo.
- Entrena con el SDK en C# (rápido) o reimplementa el entorno en Python con **las mismas reglas** (sección 2).

## 6. Propuesta de contrato ONNX (NO implementado aún en Unity — próxima iteración)
> Borrador para que los equipos de Python puedan empezar. La versión final la fijará el `SentisWrapper`.

**Entrada:** tensor `float32` de forma `[1, 128]` llamado `obs`:

| Índices | Contenido |
|---|---|
| 0–51 | mis 2 cartas, one-hot por `Card.Index` (índice = `(rango-2)*4 + palo`) |
| 52–103 | board, one-hot por `Card.Index` |
| 104–107 | calle one-hot (Preflop, Flop, Turn, River) |
| 108 | `Pot / 10000` |
| 109 | `ToCall / 10000` |
| 110 | `MyStack / 10000` |
| 111 | `BetsThisStreet / MaxBetsPerStreet` |
| 112 | `CanRaise` (0/1) |
| 113 | `(ActivePlayerCount - 1) / 5` |
| 114 | posición relativa al botón `((Seat - ButtonSeat + n) % n) / (n - 1)` |
| 115–120 | stacks de los asientos empezando por **mí** y en orden de mesa, `/ 10000` (0 si no hay asiento) |
| 121–126 | 1 si ese asiento sigue activo (`IsActive`), mismo orden |
| 127 | `HandNumber / 30` |

**Salida:** tensor `[1, 3]` = logits/Q-values en orden `[FOLD, CALL, RAISE]`.
La Arena (propuesta) aplicará máscara: `RAISE` se anula si `CanRaise == false`, y `FOLD` se anula si `ToCall == 0`; luego argmax.
Haz la misma máscara durante el entrenamiento para que la red no aprenda valores de acciones imposibles.

## 7. Reglas de entrega
- **Un solo archivo `MiAgente.cs`** con una clase pública, no abstracta, con **constructor sin parámetros**, que implemente
  `IPokerAgent`. Nombre de clase = nombre de tu agente (sin espacios), p. ej. `public sealed class NeuroBluff : IPokerAgent`.
- Solo `using AgentsPoker.Core;` (+ `System`, `System.Collections.Generic`, `System.Linq`, `System.Diagnostics`).
- **C# 9 y API de .NET Standard 2.1** (lo que compila Unity): nada de `namespace X;`, `record struct`, `[1, 2, 3]`,
  `Random.Shared`, `MaxBy`, `PriorityQueue`… El workbench y el proyecto `Agents/` del kit aplican exactamente esta regla.
- Una sola clase agente por archivo (la Arena usa la primera); las clases auxiliares, anidadas dentro de tu clase.
- Puedes implementar además `IHandObserver` para aprender de los rivales (opcional).
- Antes de entregar: `.\run.cmd check Agents/TuAgente.cs` sin errores ni avisos y un `bench` sin `Timeouts` ni `Errores`.
- **Prohibido:** leer/escribir archivos, red, crear hilos/`Task` propios, `Thread.Sleep`, reflexión, variables estáticas
  modificables (compartidas entre instancias: puede haber dos copias de tu agente). Datos `static readonly` que nunca cambias
  (tablas, pesos entrenados) sí están permitidos: es la forma de incluir lo que entrenaste fuera del torneo.
- Sin excepciones: envuelve tu lógica en `try/catch` y devuelve `CALL` si `ToCall == 0` (o `FOLD`) como respaldo.
- Responde en < 1000 ms: corta tu búsqueda con un `Stopwatch` (≤ 600–700 ms) y mantén el `ms máx` del `bench` por debajo de
  300 ms en tu PC, porque la Arena corre en Unity (Mono), que es más lenta que .NET. `Initialize` no está cronometrado, pero sé razonable.
- Semilla fija de tu `Random` opcional (partidas reproducibles al depurar).
- Para ONNX: entrega `MiModelo.onnx` + una nota con la versión del contrato de la sección 6 que usaste.

## 8. Recursos
- Evaluador: `HandEvaluator` del SDK (numeración Cactus Kev). Referencia: Kevin Suffecool, "Cactus Kev's Poker Hand Evaluator".
- ISMCTS: Cowling, Powley & Whitehouse (2012), *Information Set Monte Carlo Tree Search*, IEEE TCIAIG.
- DQN: Mnih et al. (2015); PPO: Schulman et al. (2017).
- Exportar a ONNX: documentación de `torch.onnx.export` (PyTorch) y Unity Sentis (Inference Engine).
- Kit: [LEEME.md](LEEME.md) (instalación, comandos, entrega), [REGLAS.md](REGLAS.md) (reglas finas, all-in, botes laterales) y
  `Agents/Rival.cs` (otro agente de ejemplo, más simple).

## 9. Correcciones respecto al documento original
1. **"Gana quien acumule más fichas"** → se gana **clasificando en cada mesa** (top N por stack tras 30 manos); los stacks se
   reinician a 10 000 en cada mesa.
2. **"Timeout o error → FOLD automático"** → el motor aplica **CHECK si no debes nada**, y FOLD solo si te toca pagar
   (`AgentError.PenaltyAction`, documento de diseño del torneo: "FOLD o CHECK si es legal").
3. **"RAISE en el cap = error o ignorado"** → no es error ni se ignora: se **convierte en CALL/CHECK** con `CoercionReason.RaiseCapped`.
   Hay otros 3 casos de conversión (tabla sección 2).
4. **`RaisesThisRound` no existe** → usa `BetsThisStreet` / `MaxBetsPerStreet` (cuenta bet + raises; preflop la BB cuenta como 1)
   o simplemente `CanRaise`.
5. **Incrementos "20 o 40"** → precisado: 20 en preflop/flop, 40 en turn/river (`BetSize` te dice el de la calle actual);
   `RaiseCost` te dice cuántas fichas pondrías realmente.
6. **"Oponentes invisibles hasta el final"** → durante la mano son invisibles; al final, con `IHandObserver.OnHandEnd`, **todos**
   los agentes reciben las cartas de quienes llegaron al showdown y el historial completo. Nunca se revelan las cartas de quien
   foldeó ni las del ganador sin disputa.
7. **Equity vs pot odds**: el Monte Carlo debe simular **contra todos los rivales activos**, no contra uno; y superar las pot odds
   justifica **CALL**, no necesariamente RAISE.
8. **FOLD gratuito**: hacer FOLD sin nada que pagar es un fold real (no se convierte en check por defecto).
9. **Cactus Kev**: no hace falta implementarlo; el SDK ya expone `HandEvaluator` con la misma numeración (7462 clases).
10. **"Evitem A\* o búsqueda no informada"**: el `*` de A\* rompía el formato Markdown (cursiva); escapado y corregido "Evitem" → "Eviten".
11. **Recompensa RL**: las ciegas ya están en el neto de fichas; no hay que añadirlas aparte ni penalizar el fold artificialmente.
12. **ONNX/Sentis**: no está implementado aún; el formato del tensor era indefinido → se añade una **propuesta de contrato** marcada.
13. **6 jugadores**: el torneo usa mesas de 2 a 6 (puede haber mesas de menos de 6); tu agente debe funcionar con cualquier número.
