# Reglas del juego — Limit Texas Hold'em (AgentsPoker)

Instructivo en español para estudiantes. Resume las reglas que usa el motor **AgentsPoker.Core v0.3.0** (el mismo de la Arena de Unity el día del torneo y del kit `run.cmd` / `entrenar.cmd`). La referencia técnica completa está en `Kit_AgentsPoker_SDK/REGLAS.md`.

---

## 1. Modalidad

- **Limit Texas Hold'em**, mesas de **2 a 6 jugadores** (6-max).
- Cada jugador recibe **2 cartas privadas** (hole cards). Hay un **tablero comunitario** de hasta 5 cartas.
- Gana el bote quien tenga la mejor mano de **5 cartas** (combinando hole cards y tablero), o quien quede solo porque el resto se retiró.

---

## 2. Ranking de manos (de mejor a peor)

| # | Mano | Ejemplo |
|---|---|---|
| 1 | Escalera de color (Straight Flush) | A♥ K♥ Q♥ J♥ T♥ |
| 2 | Póker (Four of a Kind) | 9♠ 9♥ 9♦ 9♣ K♠ |
| 3 | Full house | Q♠ Q♥ Q♦ 7♣ 7♥ |
| 4 | Color (Flush) | A♦ J♦ 8♦ 4♦ 2♦ |
| 5 | Escalera (Straight) | 9♣ 8♥ 7♦ 6♠ 5♥ |
| 6 | Trío (Three of a Kind) | 5♠ 5♥ 5♦ A♣ K♠ |
| 7 | Doble pareja (Two Pair) | J♠ J♥ 4♦ 4♣ 9♠ |
| 8 | Pareja (One Pair) | T♠ T♥ A♦ 8♣ 3♥ |
| 9 | Carta alta (High Card) | A♠ K♦ 9♣ 7♥ 2♠ |

Empate exacto de valor: se reparte el bote. El evaluador del kit (`HandEvaluator`) usa un entero: **mayor = mejor; igual = empate**.

---

## 2b. Palos clásicos y elementos de la Arena

| Palo clásico (código) | Símbolo | `Suit` | Letra en `Card.ToString()` | Elemento en la Arena |
|---|---|---|---|---|
| Corazones (Hearts) | ♥ | `Suit.Hearts` | `h` (ej. `Ah`) | **Fuego** |
| Diamantes (Diamonds) | ♦ | `Suit.Diamonds` | `d` (ej. `Td`) | **Agua** |
| Tréboles (Clubs) | ♣ | `Suit.Clubs` | `c` (ej. `7c`) | **Tierra** |
| Picas (Spades) | ♠ | `Suit.Spades` | `s` (ej. `Ks`) | **Aire** |

Los palos no tienen jerarquía en póker, así que las reglas no cambian: el elemento es solo arte. Tu agente **solo ve los palos clásicos** (`Suit` / letra); nunca recibe el nombre del elemento. La notación es rango + letra: `2..9`, `T`, `J`, `Q`, `K`, `A` seguido de `c`, `d`, `h`, `s`.

## 3. Calles (orden de la mano)

1. **Preflop** — se reparte hole cards; hablan tras las ciegas.
2. **Flop** — 3 cartas al tablero.
3. **Turn** — 4.ª carta.
4. **River** — 5.ª carta.
5. **Showdown** — si quedan ≥ 2 jugadores, se comparan manos.

---

## 4. Ciegas, apuestas y tope

| Concepto | Valor |
|---|---|
| Ciega pequeña (SB) | **10** |
| Ciega grande (BB) | **20** |
| Incremento preflop / flop | **20** |
| Incremento turn / river | **40** |
| Stack inicial (cada mesa) | **10 000** (se reinicia al empezar la mesa) |
| Tope por calle | **4 apuestas** (1 bet + 3 raises). En preflop la BB cuenta como apuesta #1 |

Una apuesta o subida siempre lleva el compromiso al **siguiente nivel de la rejilla** (p. ej. 20 / 40 / 60 / 80 en preflop-flop; 40 / 80 / 120 / 160 en turn-river).

---

## 5. Acciones que devuelve tu agente

Tu agente solo elige una **intención**; el motor calcula las fichas.

| Devuelves | Si no debes nada | Si debes fichas |
|---|---|---|
| `CALL` | CHECK | CALL (all-in si no te alcanza) |
| `RAISE` | BET de un incremento | RAISE de un incremento |
| `FOLD` | **FOLD real** (pierdes la mano; para pasar gratis usa `CALL`) | FOLD |

### Conversiones automáticas (no son errores de compilación)

| Situación | Se aplica | Motivo |
|---|---|---|
| `RAISE` con la calle ya en 4 apuestas | CALL / CHECK | tope |
| `RAISE` cuando no se reabrió (solo all-in incompletos) | CALL | no reabre |
| `RAISE` con stack = lo que debes | CALL all-in | fichas insuficientes |
| `RAISE` si todos los rivales están all-in | CALL / CHECK | nadie puede responder |
| Valor inválido | CHECK gratis, si no FOLD | acción inválida |
| **Excepción** en `GetAction` / `OnHandEnd` | CHECK gratis, si no FOLD | castigo |
| **Más de 1000 ms** (torneo) | CHECK gratis, si no FOLD | timeout |

Consulta siempre `CanRaise`, `LegalActions` o `IsLegal` antes de decidir.

---

## 6. Showdown: qué cartas se revelan

Al terminar la mano (callback opcional `OnHandEnd`):

- Se muestran las hole cards de **todos los que llegaron al showdown** (nadie puede esconderlas).
- Si ganas porque **todos se retiraron** ante tu apuesta, **no** muestras.
- Las cartas de quien hizo **FOLD** **nunca** se revelan.

Durante la mano solo ves **tus** 2 cartas, el tablero y la información pública (stacks, apuestas, folds, historial).

---

## 7. Tiempo y errores

- En torneo: **1000 ms reales** por `GetAction` y por `OnHandEnd`.
- Timeout o excepción → CHECK si es gratis, si no FOLD, y se registra el error.
- La Arena de Unity (Mono) es más lenta que .NET en tu PC: deja margen.

---

## 8. Clasificación de una mesa (30 manos)

- Se juegan **30 manos** (o hasta que queden menos de 2 jugadores con fichas).
- Stacks **reinician a 10 000** al empezar cada mesa.
- Orden final:
  1. Más fichas al final.
  2. Empate: se mira el stack tras la mano anterior, y así hacia atrás; la primera diferencia decide (quien tuvo más fichas **más recientemente** queda arriba → “quebrar más tarde” también ayuda).
  3. Si siempre fueron iguales: **asiento más bajo**.
- Avanzan los **top N** de la mesa (típicamente 3; siempre se elimina al menos uno).

---

## 9. Formato del torneo (bracket)

- Mesas de 2–6. Con ≤ 6 agentes hay 1 mesa; si no, `ceil(n/6)` mesas **balanceadas** (tamaños que difieren como máximo en 1).
- Sorteo al azar en la ronda 1.
- Siguiente ronda: clasificados **intercalados por puesto** (todos los 1.º, luego los 2.º, …) y repartidos en mesas nuevas; stacks otra vez a 10 000.
- **Mesa final** cuando quedan ≤ 6. Puede jugar el **Jefe Final “Archimago”** (agente builtin `Heuristic` / `SimpleHeuristicAgent`).
- Cada mesa crea una **instancia nueva** de tu agente: lo aprendido con `OnHandEnd` solo dura esa mesa.

El organizador puede ajustar manos por mesa, clasificados y si juega el Jefe. `run.cmd tournament` simula este formato.

---

## 10. Dónde profundizar

| Tema | Documento |
|---|---|
| Instalar y usar el kit | `Kit_AgentsPoker_SDK/LEEME.md` |
| API, técnicas, entrega del `.cs` | `Kit_AgentsPoker_SDK/GUIA_AGENTES.md` |
| Detalle fino (all-in, botes laterales) | `Kit_AgentsPoker_SDK/REGLAS.md` |
| Qué entregar en el curso | `INSTRUCTIVO_ENTREGAS.md` / `.pdf` |