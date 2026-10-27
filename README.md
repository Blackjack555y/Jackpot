# jackpot — agente de Limit Hold'em (Proyecto Final Fundamentos)

Repositorio del equipo para desarrollar, probar y entrenar el agente **`jackpot`** con el kit **AgentsPoker SDK v0.3.0**.
El nombre `jackpot` está registrado en el formulario del curso y **no se puede cambiar**: la clase y el archivo deben llamarse así.

| Qué | Dónde |
|---|---|
| Agente que se entrega (Entrega 2) | `Kit_AgentsPoker_SDK/Agents/jackpot.cs` |
| Plantilla del informe (Entrega 1) | `INFORME_ENTREGA1_plantilla.md` |
| Reglas y entregas del curso | `INSTRUCTIVO_ENTREGAS.md`, `REGLAS_POKER.md`, `PREGUNTAS_FRECUENTES.md` |
| Documentación del kit | `Kit_AgentsPoker_SDK/LEEME.md`, `GUIA_AGENTES.md`, `REGLAS.md` |

Fechas y medio de entrega: **[a definir por el profesor]**.

---

## 1. Instalar (una sola vez)

1. Instala el **.NET SDK 10** (o 9): https://dotnet.microsoft.com/download (el SDK, no solo el *runtime*).
   En Windows también sirve: `winget install --id Microsoft.DotNet.SDK.10`
2. Abre una terminal **nueva** y comprueba: `dotnet --version`
   - Si en PowerShell dice que `dotnet` no se reconoce: `$env:Path = "C:\Program Files\dotnet;" + $env:Path`
3. Clona y entra al kit:
   ```bash
   git clone https://github.com/Blackjack555y/Jackpot.git
   cd Jackpot/Kit_AgentsPoker_SDK
   ```

Todos los comandos de abajo se corren **desde `Kit_AgentsPoker_SDK/`**. En Windows usa `.\run.cmd` / `.\entrenar.cmd`;
en macOS/Linux, `./run.sh` / `./entrenar.sh`. La primera vez tarda un poco porque compila.

---

## 2. Los agentes que hay en `Agents/`

| Archivo | Qué es | ¿Se entrega? |
|---|---|---|
| `jackpot.cs` | **Versión oficial** (= v1). Equity Monte Carlo contra rangos de rivales + pot odds + c-bet + ajuste de final de mesa. | **Sí** |
| `jackpot_v1.cs` | Copia congelada de la v1 (referencia para comparar). | No |
| `jackpot_v2.cs` | v1 + posición + tipos de rival (station/maníaco/roca) + parámetros ajustables. Rindió peor. | No |
| `jackpot_v3.cs` | v1 + contar "se retira" solo cuando había apuesta que pagar. Igual que v1 (sin diferencia demostrada). | No |
| `MiAgente.cs`, `Rival.cs` | Ejemplos del kit (línea base). | No |

Para probar una idea nueva: copia `jackpot.cs` a `jackpot_v4.cs`, cambia **todas** las apariciones del nombre de la clase
a `jackpot_v4`, haz el cambio y compáralo (sección 4). Solo si gana con margen se copia a `jackpot.cs` (con clase `jackpot`).

---

## 3. Probar un agente (banco de pruebas)

```text
.\run.cmd list                                                  # agentes que compilan
.\run.cmd check Agents\jackpot.cs                               # compila + APIs prohibidas  -> debe decir OK
.\run.cmd match --agent jackpot --builtin Heuristic*5 --verbose # una mesa, mano a mano en pantalla
.\run.cmd bench --agent jackpot --builtin Heuristic*5 --matches 200   # OBLIGATORIO antes de entregar
.\run.cmd bench --agent jackpot --builtin Heuristic --matches 200     # heads-up (1 rival)
.\run.cmd bench --agent jackpot --builtin Heuristic*2 --builtin Station --builtin Raiser --builtin Random --matches 200  # mesa mixta
.\run.cmd bench --agent jackpot --agent jackpot_v2 --builtin Heuristic*4 --matches 200 # dos versiones en la misma mesa
```

Cómo leer el `bench`:
- **Clasif%**: % de mesas en que quedó top 3 → es lo que cuenta en el torneo.
- **Gana%** (1.º lugar), **RankProm** (puesto promedio), **StackProm** (fichas al final; empiezan en 10.000).
- **ms/dec** y **ms máx**: tiempo por decisión. `ms máx` debe ser **< 300** (el torneo da 1000 ms, pero Unity es más lento).
- **Timeouts** y **Errores**: deben ser **0**.

Builtins disponibles: `Heuristic` (= Jefe Final), `Station` (paga todo), `Raiser` (sube todo), `Random`.

---

## 4. Comparar versiones y entrenar (laboratorio `Entrenamiento/`)

El laboratorio **no se entrega**: puede usar hilos, archivos, etc. Está en `Entrenamiento/Program.cs`.

```text
.\entrenar.cmd comparar 800            # todas las versiones en LAS MISMAS 800 mesas (semilla 1000)
.\entrenar.cmd comparar 800 900000     # igual pero con otras mesas (semilla 900000) -> para confirmar
.\entrenar.cmd ajustar 800 2           # búsqueda de parámetros de jackpot_v2 (lento: ~1,5 h por pasada)
.\entrenar.cmd 60                      # prueba original del kit con jackpot (60 s por prueba)
```

- `comparar` imprime, para cada agente, la diferencia **jackpot − este** con un margen **± (95 %)**.
  Si |diferencia| < ±, **no está demostrado** que una sea mejor: se reporta "sin diferencia demostrada".
- Para añadir una versión nueva a la comparación, agrégala a la lista `candidatos` de `Program.cs`
  (y, si quieres que aparezca como rival, a `Pool`).
- Las mesas son de 3, 4 y 6 jugadores con rivales al azar de: Heuristic ×2, Rival, Station, Raiser, Random, MiAgente, jackpot_v1.
- Duración aproximada en nuestro PC: 800 mesas × 6 agentes ≈ 12–15 min.

---

## 5. Estado del entrenamiento (mediciones reales)

**El entrenamiento ya terminó.** Resumen:

| Experimento | Resultado |
|---|---|
| Búsqueda de parámetros de v2 (11 parámetros, 800 mesas por candidato, 95,4 min) | **Ningún cambio aceptado** (ninguno se confirmó con mesas nuevas). Log: `Entrenamiento/ajuste_log.txt` |
| v2 − v1, 600 mesas (semilla 1000) | −3,3 ± 2,5 pts → v2 peor |
| v2 − v1, 800 mesas (semilla 900000) | −2,8 ± 2,2 pts → v2 peor (confirmado) |
| v3 − v1, 800 mesas (semilla 900000) | +0,9 ± 1,8 pts → sin diferencia demostrada |

Clasificación en las 800 mesas (semilla 900000): v3 81,9 % · v1 81,0 % · v2 78,6 % · MiAgente 74,5 % · Heuristic 69,2 % · Rival 68,6 %.

Bench contra 5 Heuristic (200 mesas), v1: Clasif 69,0 % · Gana 16,0 % · 4,60 ms/dec · ms máx 39,7 · 0 timeouts · 0 errores.
Plantilla MiAgente en el mismo bench: Clasif 53,0 %.

**Decisión:** se entrega la v1 como `jackpot.cs` (la más simple entre las que rinden mejor).
Pendiente: repetir los 3 bench de la sección 3 con el `jackpot.cs` final y pasarlos al informe.

---

## 6. Reglas del archivo que se entrega (no romperlas)

- Un solo archivo `jackpot.cs`, clase `public sealed class jackpot : IPokerAgent` con **constructor sin parámetros**.
- Una sola clase agente por archivo; clases auxiliares **dentro** de ella.
- Solo `using AgentsPoker.Core; System; System.Collections.Generic; System.Linq; System.Diagnostics;`
- C# 9 / .NET Standard 2.1 (nada de `record struct`, `Random.Shared`, `MaxBy`, `namespace X;`…).
- **Prohibido:** archivos, red, hilos/`Task`, `Thread.Sleep`, reflexión, variables `static` modificables.
  (`const` y `static readonly` que nunca cambian sí se permiten: así se pegan los parámetros entrenados.)
- `try/catch` en `GetAction` con respaldo: `CALL` si `ToCall == 0`, si no `FOLD`.
- Cortar el Monte Carlo con `Stopwatch` (hoy: 150 ms), nunca solo con un número fijo de iteraciones.
- Es **código**, no un modelo: no se entregan DLL ni ONNX.
- Antes de entregar: `check` → `OK` sin avisos, y `bench` contra `Heuristic*5` con 0 timeouts, 0 errores, `ms máx` < 300.
