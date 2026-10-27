# Preguntas frecuentes — Proyecto Final Fundamentos

Respuestas verificadas contra el kit **AgentsPoker SDK v0.3.0** (`Kit_AgentsPoker_SDK/`). Si algo no coincide, manda lo que diga el kit: `LEEME.md`, `GUIA_AGENTES.md` y `REGLAS.md`.

---

## 1. Instalación y primer arranque

### ¿Qué versión de .NET necesito?

El **.NET SDK 10** (o 9). No basta el *runtime*: necesitas el **SDK**. Las herramientas del kit compilan para `net9.0`; tu agente se compila como en la Arena (C# 9 + .NET Standard 2.1), eso lo hace el kit solo.

- **Windows:** instalador desde https://dotnet.microsoft.com/download (o `winget install Microsoft.DotNet.SDK.10`).
- **macOS:** instalador `.pkg` de la misma página (o `brew install --cask dotnet-sdk`).
- **Linux:** paquetes de tu distribución o el script `dotnet-install.sh` (instrucciones en la misma página).

### ¿Cómo verifico que quedó bien?

Abre una **terminal nueva** y escribe `dotnet --version`. Debe mostrar `10.x.x` o `9.x.x`. Si sale `8.x` o menos, instala el SDK 10.

### `dotnet` no se reconoce

Instalaste solo el runtime o la terminal estaba abierta antes de instalar. Instala el **SDK** y abre una terminal nueva.

### `run.cmd` / `run.sh` no abre o "no se reconoce"

- Estás en otra carpeta: entra a la del kit (la que contiene `LEEME.md`), por ejemplo `cd "Kit_AgentsPoker_SDK"`.
- En PowerShell escribe `.\run.cmd list` (con `.\`). En macOS/Linux: `bash run.sh list`.
- Sin scripts también funciona: `dotnet run -c Release --project tools/AgentsPoker.Sim -- list`.
- Si Windows bloquea el zip: clic derecho en el `.zip` → Propiedades → **Desbloquear** antes de descomprimir.

### La primera vez falla con `NU1301` o tarda mucho

La **primera compilación necesita internet**: descarga el compilador desde NuGet (1-2 minutos). Después funciona sin conexión.

### `NETSDK1045` (la versión de .NET no admite net9.0)

Tienes un SDK antiguo. Instala el .NET SDK 10 (o 9).

---

## 2. Comandos del kit

Desde la carpeta del kit (en macOS/Linux cambia `.\run.cmd` por `bash run.sh` y `.\entrenar.cmd` por `bash entrenar.sh`):

| Comando | Para qué |
|---|---|
| `.\run.cmd list` | compila y lista todos los agentes de `Agents/` |
| `.\run.cmd check Agents\TuAgente.cs` | solo compila; errores con línea y columna |
| `.\run.cmd match --agent TuAgente --builtin Heuristic*5 --verbose` | una mesa de 30 manos, mano por mano |
| `.\run.cmd bench --agent TuAgente --builtin Heuristic*5 --matches 200` | 200 mesas con asientos rotados → estadísticas |
| `.\run.cmd tournament --all --builtin Heuristic*4 --builtin Station*2` | mini torneo con el formato real |
| `.\run.cmd help` | todas las opciones |
| `.\entrenar.cmd` / `.\entrenar.cmd 300` | laboratorio: mide (y entrena) con miles de manos; 300 = segundos por prueba |

Opciones útiles: `--hands 30`, `--seed 42` (misma semilla = misma partida), `--timeout 1000`, `--fast` (sin límite de tiempo, solo para entrenar).

---

## 3. Errores de compilación

### Me compila en mi editor pero el kit dice error (o "no disponible en C# 9.0")

El torneo corre en Unity, que compila **C# 9** con la API de **.NET Standard 2.1**. El kit aplica exactamente esa regla. Ejemplos **no permitidos**:

- `namespace MiAgente;` (namespace de archivo) → usa `namespace MiAgente { ... }` o ninguno.
- `record struct`, colecciones `[1, 2, 3]`.
- `Random.Shared` → crea tu propio `new Random(semilla)`.
- `MaxBy`, `MinBy`, `PriorityQueue` → escríbelos a mano.

Usings permitidos: `AgentsPoker.Core`, `System`, `System.Collections.Generic`, `System.Linq`, `System.Diagnostics`.

### "No hay ninguna clase pública … que implemente IPokerAgent"

Tu clase debe ser `public`, no abstracta, con **constructor sin parámetros**, e implementar `IPokerAgent`. Una sola clase agente por archivo; las auxiliares, anidadas dentro.

---

## 4. Tiempo y errores en partida

### ¿Cuánto tiempo tengo por decisión?

**1000 ms reales** por `GetAction` y por `OnHandEnd`. La Arena (Unity/Mono) es más lenta que tu PC: corta tu búsqueda con un `Stopwatch` (≈600-700 ms) y mantén el `ms máx` del `bench` **por debajo de 300 ms**. `Initialize` no está cronometrado.

```text
var reloj = Stopwatch.StartNew();
while (reloj.ElapsedMilliseconds < 600) { /* una simulación más */ }
```

### ¿Qué pasa si me paso del tiempo o lanzo una excepción?

Se aplica **CHECK si es gratis, si no FOLD**, y queda registrado como error. Envuelve tu lógica en `try/catch` y devuelve `CALL` si `ToCall == 0` (o `FOLD`) como respaldo.

### Mi agente se retira cuando podía pasar gratis

`FOLD` sin nada que pagar es un fold real. Devuelve `CALL` cuando `o.ToCall == 0` (eso es CHECK).

---

## 5. Rivales, nombre y entrega

### ¿Cómo pruebo contra el agente de un compañero?

1. Copia su archivo (por ejemplo `Pirata.cs`) en `Agents/`.
2. `.\run.cmd list` para comprobar que compila.
3. `.\run.cmd match --agent TuAgente --agent Pirata --builtin Heuristic*2` o `.\run.cmd tournament --all`.

Si tu editor marca clases duplicadas, mueve el archivo a `Agents/otros/` y usa `--agent Agents/otros/Pirata.cs`.

### Reglas del nombre del agente

- **Único** en la clase y **sin espacios ni tildes** (letras, dígitos, `_`).
- **Nombre de la clase = nombre del archivo = nombre registrado** (ej. clase `NeuroBluff` → `NeuroBluff.cs`).
- Regístralo en el formulario: **https://forms.gle/2smLqbx2b69zWn7W9**
- Es **inmutable**: después de registrarlo no se puede cambiar.

### ¿Qué APIs están prohibidas?

Leer/escribir **archivos**, **red**, crear **hilos** o `Task` propios, `Thread.Sleep`, **reflexión** y variables `static` modificables (puede haber dos copias de tu agente). Sí se permiten datos `static readonly` que nunca cambias (tablas, pesos entrenados). El kit avisa si detecta APIs sospechosas.

### ¿Puedo usar Python u ONNX?

**No en esta edición: solo se aceptan agentes `.cs`.** Puedes entrenar un modelo donde quieras (incluso en Python) y escribir la inferencia en C# dentro de tu `.cs`, con los pesos pegados como constantes.

---

## 6. Cartas y resultados

### ¿Por qué en la Arena veo Fuego, Agua, Tierra y Aire?

Es solo arte. Tu agente ve los palos clásicos:

| Palo | Símbolo | Letra | Elemento |
|---|---|---|---|
| Corazones (`Suit.Hearts`) | ♥ | `h` | Fuego |
| Diamantes (`Suit.Diamonds`) | ♦ | `d` | Agua |
| Tréboles (`Suit.Clubs`) | ♣ | `c` | Tierra |
| Picas (`Suit.Spades`) | ♠ | `s` | Aire |

Los palos no tienen jerarquía en póker: las reglas no cambian.

### ¿Por qué mis resultados cambian de una corrida a otra?

El póker tiene mucha **varianza**: 30 manos son muy pocas para concluir. Cada corrida reparte cartas distintas salvo que fijes `--seed`. Para comparar versiones usa **muchas mesas** (`bench --matches 200` o más) o `entrenar.cmd 300`, y mira el **margen de error (±)**: si dos versiones difieren menos que ese margen, todavía no sabes cuál es mejor. Usa `--seed` solo para depurar una mano concreta.

---

## 7. Informe y dudas

### ¿Cómo debe ser el informe?

PDF, **máximo 5 páginas tamaño carta**, individual, con las secciones de `INSTRUCTIVO_ENTREGAS.pdf` (Resumen, Introducción, Objetivos, Marco teórico, Metodología, Resultados, Discusión, Conclusiones). Fecha y medio: **[a definir por el profesor]**.

### ¿Dónde pregunto?

**[a definir por el profesor]**. Antes de preguntar, revisa `Kit_AgentsPoker_SDK/LEEME.md` §9 (Problemas frecuentes) y este documento.
