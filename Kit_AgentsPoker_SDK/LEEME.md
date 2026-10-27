# AgentsPoker — Kit para crear y entrenar tu agente (SDK v0.3.0)

Tu misión: programar un **agente** (una clase C#) que juegue **Limit Texas Hold'em** contra los agentes de tus compañeros en un
torneo en vivo. Este kit trae:

- **El motor del torneo** (`lib/AgentsPoker.Core.dll`): el mismo que usa la Arena de Unity el día del evento.
- **El banco de pruebas** (`run.cmd`): juega partidas, mesas y torneos con las reglas y el límite de tiempo reales.
- **El laboratorio** (`entrenar.cmd`): mide y entrena tu agente con miles de manos y te deja depurarlo con breakpoints.
- **Dos agentes de ejemplo** en `Agents/` para que empieces.

**Lo que entregas al final es un solo archivo `.cs`** con tu agente.

```
AgentsPoker-SDK/
  LEEME.md            ← estás aquí: instalación y uso paso a paso
  GUIA_AGENTES.md     API completa, técnicas (Monte Carlo, ISMCTS, RL) y reglas de entrega
  REGLAS.md           reglas exactas del motor (all-in, botes, empates, formato del torneo)
  Agents/             TUS agentes (.cs). Trae MiAgente.cs (plantilla) y Rival.cs (ejemplo simple)
  Entrenamiento/      laboratorio: Program.cs es tuyo, edítalo como quieras
  run.cmd / run.sh    banco de pruebas (workbench)
  entrenar.cmd / .sh  laboratorio
  lib/                el motor (DLL + documentación para IntelliSense)
  tools/              código del banco de pruebas (no necesitas tocarlo)
  AgentsPoker.Sdk.sln abre esto en Visual Studio / Rider (en VS Code abre la carpeta)
```

---

## 1. Instalación (una sola vez)

1. Instala el **.NET SDK 10** (o 9) desde <https://dotnet.microsoft.com/download>. Comprueba en una terminal nueva:
   ```
   dotnet --version        → 10.x.x (o 9.x.x)
   ```
2. Instala un editor: **VS Code** con la extensión **C# Dev Kit**, **Visual Studio 2022** (o más nuevo) o **Rider**.
3. Descomprime el kit en una carpeta tuya (por ejemplo `C:\poker\AgentsPoker-SDK`), abre una terminal **en esa carpeta**
   (la que contiene este `LEEME.md`) y ejecuta:

   | Windows (PowerShell o cmd) | macOS / Linux |
   |---|---|
   | `.\run.cmd list` | `bash run.sh list` |

   La primera vez tarda 1–2 minutos porque descarga el compilador (necesitas internet). Debes ver:
   ```
   AgentsPoker.Core v0.3.0 | compilación como la Arena: C# 9 + .NET Standard 2.1
   Agentes en ...\Agents:
     MiAgente.cs              OK   MiAgente [OnHandEnd]
     Rival.cs                 OK   Rival
   Builtins: Fold, CheckFold, Station, Raiser, Random, Heuristic
   ```

> En el resto del documento los comandos aparecen en su forma de Windows (`.\run.cmd …`). En macOS/Linux usa `bash run.sh …`
> y `bash entrenar.sh …` con los mismos argumentos. Si algo falla, mira la sección [9. Problemas frecuentes](#9-problemas-frecuentes).

## 2. Tu primera partida (2 minutos)

```
.\run.cmd match --agent MiAgente --builtin Heuristic*3 --hands 5 --verbose
```
Verás cada mano como la vería un espectador (todas las cartas), las acciones, el showdown y al final la clasificación de la mesa
y una tabla de estadísticas. Quita `--verbose` y `--hands 5` para jugar una mesa completa de 30 manos.

Los **builtins** son rivales que trae el motor: `Fold`, `CheckFold`, `Station` (siempre paga), `Raiser` (siempre sube),
`Random` y `Heuristic` (heurística simple; es el **Jefe Final** del torneo). `Heuristic*3` = tres copias.

## 3. Crea tu agente

1. Copia `Agents/MiAgente.cs` como `Agents/<NombreDeTuAgente>.cs`, por ejemplo `Agents/NeuroBluff.cs`.
2. Dentro del archivo cambia el nombre de la clase por el mismo nombre (sin espacios ni tildes):
   ```csharp
   public sealed class NeuroBluff : IPokerAgent, IHandObserver
   ```
3. Comprueba que compila: `.\run.cmd check Agents/NeuroBluff.cs`
4. En `Entrenamiento/Program.cs` cambia `new MiAgente()` por `new NeuroBluff()`.

### Lo mínimo que necesita un agente
```csharp
using AgentsPoker.Core;

public sealed class NeuroBluff : IPokerAgent
{
    public void Initialize(string agentName) { }      // una vez, al sentarse en la mesa

    public ActionType GetAction(PlayerObservation o)  // cada vez que es tu turno
    {
        // o.HoleCards (tus 2 cartas), o.Board, o.Pot, o.ToCall, o.CanRaise, o.PotOdds, o.Players, o.ActionHistory...
        if (o.ToCall == 0) return ActionType.CALL;    // check gratis
        return ActionType.FOLD;
    }
}
```
Solo devuelves una intención: `FOLD`, `CALL` (o check si no debes nada) o `RAISE` (o bet si nadie apostó). **El motor calcula
las fichas.** Todo lo que recibe tu agente está en [GUIA_AGENTES.md §2](GUIA_AGENTES.md);
en el editor, pasa el mouse sobre cualquier propiedad para ver su descripción.

### Reglas para que la Arena acepte tu archivo (`check` las verifica)
- Clase **pública**, no abstracta, con **constructor sin parámetros**, que implemente `IPokerAgent`.
- **Una sola clase agente por archivo** (la Arena usa la primera). Pon tus clases auxiliares **dentro** de tu clase agente
  (así no chocan con las de tus compañeros).
- **C# 9 y .NET Standard 2.1**, que es lo que compila Unity. No funcionan, por ejemplo: `namespace X;` (usa `namespace X { … }`),
  `record struct`, `[1, 2, 3]` (usa `new[] { 1, 2, 3 }`), `Random.Shared` (usa un campo `new Random()`), `MaxBy`/`MinBy`,
  `PriorityQueue`. El editor y `check` los marcan como error.
- Sin archivos, red, hilos/`Task` propios, `Thread.Sleep`, reflexión ni estado `static` modificable.

## 4. Pruébalo con las reglas del torneo: `run.cmd`

| Comando | Para qué |
|---|---|
| `.\run.cmd list` | lista y compila todos los agentes de `Agents/` |
| `.\run.cmd check Agents/NeuroBluff.cs` | solo compilar; muestra errores con línea y columna |
| `.\run.cmd match --agent NeuroBluff --builtin Heuristic*5 --verbose` | una mesa de 30 manos, mano por mano |
| `.\run.cmd bench --agent NeuroBluff --builtin Heuristic*5 --matches 200` | 200 mesas con asientos rotados → estadísticas |
| `.\run.cmd tournament --all --builtin Heuristic*4 --builtin Station*2` | mini torneo con el formato real (rondas, mesa final) |
| `.\run.cmd help` | todas las opciones |

Opciones útiles: `--hands 30`, `--qualify 3`, `--seed 42` (misma semilla = misma partida, ideal para depurar),
`--timeout 1000` (límite por decisión; el del torneo es 1000), `--fast` (sin límite de tiempo, más rápido).

Cómo leer la tabla de estadísticas:

| Columna | Significado | Qué buscas |
|---|---|---|
| `Clasif%` | % de mesas en que quedaste en el top N | **lo que importa en el torneo** |
| `Gana%`, `RankProm`, `StackProm` | 1º de la mesa, puesto promedio, fichas promedio | más es mejor (rank: menos) |
| `ms/dec`, `ms máx` | tiempo por decisión (promedio y peor caso) | `ms máx` **< 300** en tu PC |
| `Timeouts`, `Errores` | decisiones que tardaron > 1000 ms / excepciones | **0 y 0** |

## 5. Entrénalo y depúralo: `entrenar.cmd`

```
.\entrenar.cmd          (cada prueba corre 30 s)
.\entrenar.cmd 300      (cada prueba corre 300 s: resultados más precisos)
```
```
A) Largo plazo: 2,241 manos (75 manos/s)
   Resultado: +195.6 ± 60.4 bb/100
   Decisión: 5.08 ms prom, 14.6 ms máx   Errores: 0

B) Formato torneo: 68 mesas de 30 manos, 6 jugadores, top 3 clasifica
   Clasif% 69.1 ± 11.0   Gana% 25.0   Puesto prom 2.81   Stack prom 10,926
```
- **A) bb/100**: ciegas grandes ganadas cada 100 manos. **B) Clasif%**: lo que cuenta en el torneo.
- El **±** es el margen de error. Si dos versiones de tu agente difieren menos que ese margen, todavía no sabes cuál es mejor:
  corre más tiempo. El póker tiene mucha varianza; no te fíes de una sola mesa.
- `Entrenamiento/Program.cs` es **tu laboratorio**: cambia los rivales (añade `() => new Rival()` o agentes de compañeros),
  prueba combinaciones de parámetros en un bucle, guarda estadísticas en archivos, entrena una red… Nada de eso se entrega.
- **Depurar con breakpoints:** abre la carpeta del kit en VS Code (o `AgentsPoker.Sdk.sln` en Visual Studio), pon un breakpoint
  en tu agente y ejecuta el proyecto **Entrenamiento** con F5 (en Visual Studio: clic derecho → *Establecer como proyecto de
  inicio*). En modo entrenamiento el motor captura las excepciones de tu agente y las imprime como `ERROR`; para que el depurador
  se detenga en ellas activa "romper en todas las excepciones".
- **Lo que aprendas fuera del torneo debe ir dentro del `.cs`:** en el torneo no se pueden leer archivos. Si ajustas parámetros
  o entrenas pesos/tablas, pégalos en tu clase como constantes, p. ej.
  `private static readonly double[] Pesos = { 0.12, -0.5, 0.33 };` (datos `static readonly` que nunca modificas están bien).

## 6. Cómo se juega el torneo (lo que tu agente debe saber)

- Mesas de **2 a 6** agentes, **30 manos** por mesa, **10 000 fichas** que se reinician en cada mesa.
- Avanzan los **3 mejores** de cada mesa (o lo que fije el organizador). No gana quien acumula más en total: gana quien queda en
  zona de clasificación mesa tras mesa. La mesa final decide el campeón; puede jugar el **Jefe Final** (= builtin `Heuristic`).
- **1000 ms por decisión** (y por `OnHandEnd`). Si tardas más o lanzas una excepción, el motor juega por ti: CHECK si es gratis,
  si no **FOLD**, y el error sale en la pantalla del evento.
- **La Arena corre en Unity (Mono), que es más lenta que .NET en tu PC.** Usa un presupuesto de tiempo con `Stopwatch`
  (la plantilla corta a los 600 ms), nunca un número fijo de iteraciones, y mantén `ms máx` por debajo de 300 ms en el `bench`.
- **Cada mesa crea una instancia nueva de tu agente**: lo que aprendas de los rivales con `OnHandEnd` dura las 30 manos de esa mesa.
  Identifica a los rivales por **nombre** (`PlayerNames`), no por asiento.
- Tu agente debe funcionar con cualquier número de rivales (1 a 5).

Detalles en [GUIA_AGENTES.md](GUIA_AGENTES.md) y [REGLAS.md](REGLAS.md).

### Palos y elementos

| Palo clásico (código) | Símbolo | `Suit` | Letra en `Card.ToString()` | Elemento en la Arena |
|---|---|---|---|---|
| Corazones (Hearts) | ♥ | `Suit.Hearts` | `h` (ej. `Ah`) | **Fuego** |
| Diamantes (Diamonds) | ♦ | `Suit.Diamonds` | `d` (ej. `Td`) | **Agua** |
| Tréboles (Clubs) | ♣ | `Suit.Clubs` | `c` (ej. `7c`) | **Tierra** |
| Picas (Spades) | ♠ | `Suit.Spades` | `s` (ej. `Ks`) | **Aire** |

Los palos no tienen jerarquía en póker, así que las reglas no cambian: el elemento es solo arte. Tu agente **solo ve los palos
clásicos** (`Suit` / letra); nunca recibe el nombre del elemento. La notación es rango + letra: `2..9`, `T`, `J`, `Q`, `K`, `A`
seguido de `c`, `d`, `h`, `s`.

## 7. Probar contra agentes de compañeros

1. Copia su archivo (`Pirata.cs`) en `Agents/`.
2. `.\run.cmd list` para comprobar que compila.
3. `.\run.cmd match --agent NeuroBluff --agent Pirata --builtin Heuristic*2` o `.\run.cmd tournament --all`.

El banco de pruebas compila cada archivo por separado, así que siempre funciona. El editor, en cambio, compila **todos** los
`.cs` de `Agents/` juntos: si dos archivos declaran la misma clase de primer nivel, el editor marcará error. Mueve el archivo del
compañero a una subcarpeta (por ejemplo `Agents/otros/`; las subcarpetas no se compilan) y usa
`--agent Agents/otros/Pirata.cs`.

## 8. Entrega

Antes de entregar, comprueba:
- [ ] `.\run.cmd check Agents/TuAgente.cs` → `OK`, sin avisos.
- [ ] `.\run.cmd bench --agent TuAgente --builtin Heuristic*5 --matches 200` → `Timeouts 0`, `Errores 0`, `ms máx` < 300.
- [ ] Probaste con mesas pequeñas (`--builtin Heuristic` = heads-up) y grandes (5 rivales).
- [ ] El nombre de la clase es el nombre de tu agente y coincide con el nombre del archivo.
- [ ] No lee archivos, no usa red, hilos, reflexión ni estado `static` modificable.

**Entrega solo el archivo `Agents/TuAgente.cs`** por el medio y en la fecha que indique tu profesor.

## 9. Problemas frecuentes

| Síntoma | Solución |
|---|---|
| `dotnet` no se reconoce | Instala el .NET SDK (no solo el *runtime*) y abre una terminal nueva. |
| `run.cmd` no se reconoce | Estás en otra carpeta: entra a la del kit (`cd C:\poker\AgentsPoker-SDK`). En PowerShell escribe `.\run.cmd`, no `run.cmd`. Sin scripts también funciona: `dotnet run -c Release --project tools/AgentsPoker.Sim -- list` (y `--project Entrenamiento`). |
| Visual Studio avisa que el proyecto viene de internet | Antes de descomprimir: clic derecho en el `.zip` → *Propiedades* → **Desbloquear**. O acepta el aviso: el kit no tiene nada que se ejecute al abrirlo. |
| `NU1301` / no puede descargar paquetes | La primera compilación necesita internet (descarga el compilador de NuGet). |
| `NETSDK1045` (la versión de .NET no admite net9.0) | Tienes un SDK antiguo: instala el .NET SDK 10 (o 9). |
| `CS8773 … no está disponible en C# 9.0` | Usas sintaxis más nueva que la de Unity. Reescríbela en C# 9 (ver §3). |
| `'Random' no contiene una definición para 'Shared'` (u otra API que "no existe") | Es de .NET 5+; no existe en Unity. Busca la alternativa de .NET Standard 2.1. |
| `no hay ninguna clase pública … que implemente IPokerAgent` | La clase debe ser `public`, no abstracta, con constructor sin parámetros. |
| El editor marca errores en todos lados | Abre la carpeta del kit completa (no solo el `.cs`) y espera a que cargue el proyecto. Si es por un archivo de un compañero, ver §7. |
| `Timeouts` > 0 en el bench | Tu agente se pasa de 1000 ms: usa `Stopwatch` y corta antes (ver la plantilla). |
| Mi agente foldea cuando podía pasar gratis | `FOLD` sin nada que pagar es un fold real. Devuelve `CALL` cuando `o.ToCall == 0`. |
| Mi `RAISE` se convirtió en `CALL` | La calle llegó al tope de 4 apuestas, o no tenías fichas, o no se reabrieron las subidas: mira `o.CanRaise` ([REGLAS.md §2](REGLAS.md)). |

## 10. ¿Y Python / redes neuronales?

El formato `.onnx` (red neuronal entrenada en Python) **todavía no está disponible en la Arena**: hoy solo se aceptan agentes
`.cs`. Puedes entrenar una red o cualquier modelo donde quieras y luego escribir la inferencia en C# dentro de tu `.cs`, con los
pesos pegados como constantes (§5). La propuesta de formato ONNX está en [GUIA_AGENTES.md §6](GUIA_AGENTES.md).
