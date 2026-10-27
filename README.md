# jackpot — guía del equipo: cómo levantar, probar y entrenar el agente

Repositorio del equipo para desarrollar, probar y entrenar el agente **`jackpot`** (Limit Hold'em, Proyecto Final Fundamentos)
con el kit **AgentsPoker SDK v0.3.0**.
El nombre `jackpot` está registrado en el formulario del curso y **no se puede cambiar**: la clase y el archivo deben llamarse así.

**Este archivo es el nuestro** (lo mantenemos nosotros). Los demás documentos son del curso o del kit y no se editan:

| Documento | De quién | Para qué |
|---|---|---|
| `README.md` (este) | Equipo | Levantar el kit, probar y comparar agentes, estado del entrenamiento |
| `INFORME_ENTREGA1_plantilla.md` | Equipo | Borrador del informe (Entrega 1) con los datos medidos |
| `Kit_AgentsPoker_SDK/Agents/jackpot.cs` | Equipo | **El agente que se entrega** (Entrega 2) |
| `CURSO_LEEME.txt` | Profesor | Índice original de la carpeta del curso (menciona PDFs y un .zip que no subimos: los .md tienen lo mismo) |
| `INSTRUCTIVO_ENTREGAS.md`, `REGLAS_POKER.md`, `PREGUNTAS_FRECUENTES.md`, `CONTENIDO_CARPETA.md` | Profesor | Entregas, reglas del juego, preguntas frecuentes, mapa de la carpeta |
| `Kit_AgentsPoker_SDK/LEEME.md`, `GUIA_AGENTES.md`, `REGLAS.md` | Kit | Documentación oficial del SDK, API y reglas finas |

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

**Decisión:** se entrega la v1 como `jackpot.cs` (la más simple entre las que rinden mejor).

**Bench finales de `jackpot.cs`** (200 mesas cada uno, todos con 0 timeouts y 0 errores):

| Mesa | Clasif% | Gana% | RankProm | StackProm | ms/dec | ms máx | Referencia |
|---|---|---|---|---|---|---|---|
| vs Heuristic×5 (obligatorio) | 69,5 | 17,0 | 2,85 | 10.244 | 5,84 | 32,8 | Heuristic 43,0–51,0 % · MiAgente 53,0 % |
| Heads-up vs Heuristic | 74,0 | 74,0 | 1,26 | 10.235 | 3,78 | 23,9 | Heuristic 26,0 % |
| Mixta (Heuristic×2, Station, Raiser, Random) | 78,5 | 30,0 | 2,44 | 11.218 | 6,20 | 55,5 | mejor Heuristic 74,5 % (sin diferencia demostrada) |

Margen aproximado con 200 mesas: ±6 pts. Checklist de entrega del kit: **cumplido** (`check` OK, 0 timeouts, 0 errores, ms máx < 300).

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
