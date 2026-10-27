# Reglas exactas del motor (AgentsPoker.Core v0.3.0)

Referencia detallada de cómo el motor aplica las reglas de **Limit Texas Hold'em**. Es el mismo motor que usa la Arena de Unity
el día del torneo y el que usan `run.cmd` y `entrenar.cmd`. Para empezar a programar lee primero [LEEME.md](LEEME.md) y
[GUIA_AGENTES.md](GUIA_AGENTES.md); vuelve aquí cuando necesites el detalle fino (all-in, botes laterales, empates).

## 1. Estructura de la mano

- Ciegas: **SB 10 / BB 20**. Stack inicial **10 000** (se reinicia en cada mesa).
- Incremento fijo: **20** en preflop y flop, **40** en turn y river (`o.BetSize`).
- **Tope: 4 apuestas por calle** (1 bet + 3 raises). En preflop la ciega grande cuenta como la apuesta #1.
- Una apuesta o subida siempre lleva el compromiso al siguiente nivel de la rejilla: 20/40/60/80 en preflop y flop,
  40/80/120/160 en turn y river.
- **Heads-up (2 jugadores):** el botón pone la ciega pequeña, habla primero en preflop y último después del flop. Si una mesa de
  3+ queda con 2 jugadores, se aplican las reglas heads-up desde la mano siguiente.
- Los asientos sin fichas se saltan para repartir, ciegas y botón (el botón simplemente avanza; no hay "botón muerto").

## 2. Qué significa cada acción

Tu agente devuelve una **intención**; el motor calcula las fichas.

| Devuelves | Si no debes nada | Si debes fichas |
|---|---|---|
| `CALL` | CHECK | CALL (all-in si no te alcanza) |
| `RAISE` | BET de un incremento | RAISE de un incremento |
| `FOLD` | **FOLD real** (pierdes la mano: devuelve `CALL` para pasar) | FOLD |

### Conversiones (no son errores)
| Devuelves | Situación | Se aplica | `ActionRecord.Coercion` |
|---|---|---|---|
| `RAISE` | la calle ya tiene 4 apuestas | CALL/CHECK | `RaiseCapped` |
| `RAISE` | ya actuaste y después solo hubo all-in incompletos | CALL | `RaiseNotReopened` |
| `RAISE` | tu stack ≤ lo que debes | CALL all-in | `RaiseInsufficientChips` |
| `RAISE` | todos los rivales están all-in | CALL/CHECK | `RaiseNoOpponentCanAct` |
| valor fuera de 0/1/2 | — | CHECK si es gratis, si no FOLD | `InvalidAction` |
| lanzas una excepción | — | CHECK si es gratis, si no FOLD + `AgentError` | `AgentException` |
| tardas más de 1000 ms | torneo | CHECK si es gratis, si no FOLD + `AgentError` | `Timeout` |

Antes de decidir: `o.CanRaise`, `o.LegalActions`, `o.IsLegal(a)` y `o.RaiseCost` te dicen exactamente qué pasará.
Una excepción en `Initialize` también se captura: el agente sigue sentado.

## 3. All-in y subidas incompletas

- Si no te alcanzan las fichas puedes igual pagar o subir: vas all-in con lo que tengas.
- Una subida all-in que **no llega** al siguiente nivel de la rejilla es una *subida incompleta* (`ActionRecord.IsIncompleteRaise`):
  - los que vienen detrás deben pagar la diferencia (o retirarse);
  - **no cuenta** para el tope de 4 apuestas;
  - **no reabre las subidas**: quien ya actuó en esta calle (un check cuenta como actuar) solo puede pagar o retirarse
    (`CanRaise == false`). Quien aún no actuó conserva todas sus opciones.
- Las subidas se reabren para todos solo con una apuesta **completa** (cuando el compromiso de alguien alcanza el siguiente nivel,
  con una subida normal o con varios all-in cortos que juntos lo alcanzan).
- En preflop la ciega grande conserva su opción tras un all-in corto (aún no ha actuado).
- Si todos tus rivales están all-in, solo se te pide igualar el compromiso más alto de ellos y no puedes subir.

## 4. Botes

- Bote principal + botes laterales según los niveles de compromiso de quienes siguen en la mano. Las fichas de quien se retiró
  quedan como dinero muerto en los botes que alcanzaron.
- Las fichas que nadie pagó se devuelven como un bote de un solo jugador (`PotResult.IsUncontested`).
- Bote dividido: partes iguales; **las fichas impares van de una en una a los ganadores en orden de asiento empezando a la
  izquierda del botón**.
- Evaluador: `HandEvaluator.Evaluate(...)` devuelve un entero, **mayor = mejor, igual = empate**.

## 5. Qué información recibe tu agente

- **Durante la mano** (`PlayerObservation`): tus 2 cartas, el board y la información pública (stacks, apuestas, quién se retiró,
  historial de acciones de esta mano). Nunca las cartas de los demás.
- **Al terminar la mano** (`IHandObserver.OnHandEnd`, opcional): historial completo, board, botes, neto por asiento y las cartas
  de **todos los que llegaron al showdown** (nadie puede esconder su mano en el showdown). Las cartas de quien se retiró nunca se
  revelan, y si todos se retiran ante una apuesta el ganador **no** muestra.
- Todos los objetos son inmutables y se crean nuevos para cada decisión: puedes guardarlos.

## 6. Clasificación de una mesa (30 manos)

1. Más fichas al final.
2. Empate en fichas: se mira el stack después de la mano anterior, y la anterior, … hacia atrás; la primera mano en que eran
   distintos decide (quien tuvo más fichas más recientemente queda arriba; por eso "quebrar más tarde" también queda arriba).
3. Si siempre fueron iguales: el asiento más bajo.

La mesa termina tras 30 manos o cuando quedan menos de 2 jugadores con fichas.

## 7. Formato del torneo

- Mesas de 2 a 6 agentes. 1 mesa si hay ≤ 6 agentes; si no, `ceil(n / 6)` mesas **balanceadas** (tamaños que difieren como
  mucho en 1: 9 → 5 + 4, 13 → 5 + 4 + 4). Sorteo al azar en la ronda 1.
- Por mesa avanzan los **top N** (normalmente 3; siempre se elimina al menos uno: N ≤ tamaño − 1).
- Siguiente ronda: los clasificados se intercalan por puesto (todos los 1º, luego los 2º, …) y se reparten en mesas nuevas.
  Las fichas vuelven a 10 000.
- **Mesa final** cuando quedan ≤ 6. Puede jugar el **Jefe Final "Archimago"** (= `SimpleHeuristicAgent`, el builtin `Heuristic`).
- Cada mesa crea una **instancia nueva** de tu agente: lo que aprendas con `OnHandEnd` dura las manos de esa mesa.
- Límite: **1000 ms reales** por `GetAction` y por `OnHandEnd`. La Arena corre en Unity (Mono), más lenta que .NET en tu PC.

El organizador puede cambiar manos por mesa, clasificados por mesa y si juega el Jefe Final; el comando
`run.cmd tournament` simula este formato.

## 8. Palos y elementos de la Arena

| Palo clásico (código) | Símbolo | `Suit` | Letra en `Card.ToString()` | Elemento en la Arena |
|---|---|---|---|---|
| Corazones (Hearts) | ♥ | `Suit.Hearts` | `h` (ej. `Ah`) | **Fuego** |
| Diamantes (Diamonds) | ♦ | `Suit.Diamonds` | `d` (ej. `Td`) | **Agua** |
| Tréboles (Clubs) | ♣ | `Suit.Clubs` | `c` (ej. `7c`) | **Tierra** |
| Picas (Spades) | ♠ | `Suit.Spades` | `s` (ej. `Ks`) | **Aire** |

Los palos no tienen jerarquía en póker, así que las reglas no cambian: el elemento es solo arte. Tu agente **solo ve los palos
clásicos** (`Suit` / letra); nunca recibe el nombre del elemento. La notación es rango + letra: `2..9`, `T`, `J`, `Q`, `K`, `A`
seguido de `c`, `d`, `h`, `s`.

