<!--
PLANTILLA — Entrega 1: Informe de proyecto (individual)
Requisitos (INSTRUCTIVO_ENTREGAS.md): PDF, tamaño CARTA (8.5 × 11 in), MÁXIMO 5 PÁGINAS,
secciones en este orden: Resumen, Introducción, Objetivos, Marco teórico, Metodología, Resultados, Discusión, Conclusiones.
Fecha y medio de entrega: [a definir por el profesor].

Cómo usar esta plantilla:
- Lo que está entre [CORCHETES] lo completas tú.
- Los números marcados ✔ son mediciones reales hechas con el kit. Los marcados [PENDIENTE] se llenan con la medición final.
- Los textos sugeridos son un punto de partida: reescríbelos con tus palabras (el informe es individual).
- Presupuesto para no pasar de 5 páginas (letra 11 pt, márgenes 2,5 cm): ~2.000–2.300 palabras + 2–3 tablas.
- Borra estos comentarios antes de exportar a PDF.
-->

# Agente *jackpot*: Limit Texas Hold'em con equity Monte Carlo contra rangos estimados

**Autor:** [Nombre completo] · **Curso:** Fundamentos — Proyecto Final · **Profesor:** [Nombre]
**Agente registrado:** `jackpot` · **Kit:** AgentsPoker SDK v0.3.0 · **Fecha:** [fecha de entrega]

---

## 1. Resumen
<!-- 150–250 palabras. Debe tener: propósito, metodología, resultados principales, conclusión. Escríbelo AL FINAL. -->

[Propósito] Este proyecto desarrolla `jackpot`, un agente autónomo de Limit Texas Hold'em para mesas de 2 a 6 jugadores cuyo objetivo es clasificar (quedar en el top 3 por fichas tras 30 manos).

[Metodología] El agente estima su probabilidad de ganar (equity) mediante simulación Monte Carlo contra rangos de manos inferidos del comportamiento de cada rival, y la compara con las pot odds para decidir entre retirarse, pagar o subir. Se construyeron tres versiones y se compararon en las mismas mesas (números aleatorios comunes) con el laboratorio del kit, además de una búsqueda de parámetros por coordenadas.

[Resultados] La versión final (v1) clasificó en 69,5 % de 200 mesas contra cinco `Heuristic` ✔ (plantilla `MiAgente`: 53,0 % ✔; cada `Heuristic`: 43,0–51,0 %), ganó 74,0 % de los mano a mano contra `Heuristic` ✔, con 5,84 ms por decisión en promedio, 55,5 ms como máximo y 0 timeouts ✔. Agregar reglas de posición, tipos de rival y ajuste de final de mesa (v2) redujo la clasificación en ~3 puntos de forma consistente en dos experimentos independientes ✔.

[Conclusión] [Una frase: p. ej. "Un modelo simple bien calibrado superó a uno con más reglas; medir con margen de error fue decisivo para elegir."]

---

## 2. Introducción
<!-- ~250–300 palabras. Tema, planteamiento del problema, antecedentes, justificación. -->

**Tema.** Agentes autónomos que toman decisiones bajo incertidumbre, aplicados al póker Limit Hold'em.

**Planteamiento del problema.** El agente no ve las cartas de los rivales, juega contra oponentes desconocidos, dispone de 1000 ms por decisión y solo 30 manos por mesa para clasificar. [Explica por qué esto es difícil: información oculta, azar, poco tiempo para aprender de los rivales.]

**Antecedentes.** [Menciona brevemente el trabajo del grupo de la Universidad de Alberta sobre póker por computador (Billings et al., 2002) y el uso de simulación Monte Carlo para estimar equity.]

**Justificación.** [Por qué el póker es un buen vehículo para el curso: decisión bajo incertidumbre, evaluación empírica con varianza alta, límites de tiempo reales.]

---

## 3. Objetivos
<!-- General + específicos numerados y medibles. -->

**Objetivo general.** Desarrollar un agente de Limit Hold'em en C# que clasifique en mesas de hasta 6 jugadores cumpliendo las restricciones del torneo.

**Objetivos específicos.**
1. Implementar un estimador de equity Monte Carlo contra rangos de rivales que responda en menos de 300 ms por decisión (criterio del kit).
2. Modelar a los rivales durante la mesa (VPIP, PFR, agresión, retirada ante apuestas) para ajustar sus rangos.
3. Superar la clasificación de la plantilla `MiAgente` y del `Heuristic` en 200 mesas de `bench` contra cinco `Heuristic`.
4. Comparar versiones del agente con un método estadístico (diferencias pareadas con margen de error del 95 %) y elegir la versión final con base en datos.

---

## 4. Marco teórico
<!-- ~450–550 palabras. Solo lo necesario para entender tu solución. Cita fuentes. -->

**4.1 Limit Texas Hold'em en el torneo.** Ciegas 10/20; apuestas fijas de 20 (preflop y flop) y 40 (turn y river); máximo 4 apuestas por calle; 10.000 fichas por mesa; 30 manos; clasifican los 3 primeros. Timeout o excepción → CHECK si es gratis, si no FOLD. (Fuente: REGLAS_POKER.md y Kit_AgentsPoker_SDK/REGLAS.md.)

**4.2 Equity y pot odds.** *Equity*: probabilidad de ganar el bote (empates repartidos). *Pot odds*: `ToCall / (Pot + ToCall)`. Pagar es rentable a largo plazo si equity ≥ pot odds (Sklansky, 1999).

**4.3 Simulación Monte Carlo.** Estimar una probabilidad repitiendo muchas veces un experimento aleatorio (Metropolis y Ulam, 1949): se reparten al azar las cartas desconocidas, se evalúa quién gana y se promedia. El error disminuye como 1/√N.

**4.4 Rangos y modelado de rivales.** En vez de suponer que el rival tiene cualquier mano, se restringe a las manos coherentes con lo que ha hecho (Billings et al., 1998). Estadísticos usados: VPIP (% de manos en que pone fichas voluntariamente), PFR (% en que sube preflop), agresión y retirada ante apuestas. Se suavizan con un *prior* (8 manos ficticias) para no sacar conclusiones de 2–3 manos.

**4.5 Fórmula de Chen.** Puntaje de manos iniciales usado para ordenar las 1.326 combinaciones y definir "el mejor X % de manos". [Cita la fuente que uses — verifícala.]

**4.6 Evaluación experimental.** Por la alta varianza del póker, dos versiones solo se consideran distintas si su diferencia supera el margen de error. Se usan *números aleatorios comunes* (mismas mesas para todas las versiones) y diferencias pareadas, lo que reduce la varianza de la comparación.

---

## 5. Metodología
<!-- ~450–550 palabras. Materiales, herramientas, diseño, procedimiento. -->

**5.1 Herramientas.** Kit AgentsPoker SDK v0.3.0 (`run.cmd`, `entrenar.cmd`, plantilla `Agents/MiAgente.cs`); .NET SDK 10; compilación C# 9 / .NET Standard 2.1 (igual que la Arena). Equipo: [CPU, RAM, sistema operativo].

**5.2 Diseño del agente (versión final).** Por cada decisión:
1. Modela a cada rival activo: rango preflop según VPIP/PFR y lo que hizo en la mano; si apostó postflop, descarta con cierta probabilidad sus manos sin pareja.
2. Estima la equity con Monte Carlo (corte por `Stopwatch` a 150 ms, máximo 6.000 simulaciones).
3. Sube si equity ≥ 1/(n+1) + margen (0,08 si nadie apostó; 0,14 si hay apuesta; +0,05 por cada re-subida).
4. Paga si equity + odds implícitas ≥ pot odds; si no, se retira (o pasa si es gratis).
5. Apuesta de continuación en el flop si fue el último agresor preflop, hay ≤ 2 rivales y estos suelen retirarse.
6. En las últimas 6 manos ajusta el riesgo según su puesto (más prudente si clasifica con ventaja; más suelto si va fuera).
Todo dentro de `try/catch` con respaldo legal.

**5.3 Versiones comparadas.**
| Versión | Descripción |
|---|---|
| v1 | Diseño 5.2 con contador simple de "se retira". |
| v2 | v1 + ajuste por posición, clasificación de rivales (station / maníaco / roca) y parámetros ajustables. |
| v3 | v1 + contar "se retira" solo cuando el rival tenía una apuesta que pagar. |
| MiAgente | Plantilla del kit (línea base). |

**5.4 Procedimiento de medición.**
- `.\run.cmd check Agents\jackpot.cs` (compilación y APIs prohibidas).
- `.\run.cmd bench --agent jackpot --builtin Heuristic*5 --matches 200` (200 mesas de 30 manos, asientos rotados).
- Bench en heads-up (`--builtin Heuristic`) y en mesa mixta.
- Laboratorio `comparar`: todas las versiones en las MISMAS mesas (tamaños 3, 4 y 6; rivales al azar entre Heuristic, Rival, Station, Raiser, Random, MiAgente y jackpot_v1); diferencia pareada con ± = 1,96 × error estándar.
- Búsqueda de parámetros por coordenadas (v2): cada parámetro ± un paso, 800 mesas por candidato; un cambio se acepta solo si se confirma en 800 mesas nuevas (z combinado > 2).

---

## 6. Resultados
<!-- Tablas/figuras NUMERADAS con título. Al menos una tabla contra builtins. -->

**Tabla 1.** Bench contra cinco `Heuristic` (200 mesas de 30 manos).
| Agente | Clasif. % | Gana % | Puesto prom. | Stack prom. | ms/dec | ms máx | Timeouts | Errores |
|---|---|---|---|---|---|---|---|---|
| jackpot final (= v1), corrida final ✔ | 69,5 | 17,0 | 2,85 | 10.244 | 5,84 | 32,8 | 0 | 0 |
| jackpot v1, corrida inicial ✔ | 69,0 | 16,0 | 2,92 | 10.274 | 4,60 | 39,7 | 0 | 0 |
| MiAgente ✔ | 53,0 | 11,5 | 3,39 | 10.064 | 9,48 | 65,5 | 0 | 0 |
<!-- Nota: en cada bench los Heuristic quedaron entre 40,0 % y 54,0 % de clasificación.
     Margen aproximado del 95 % con 200 mesas: ±1,96·√(p(1−p)/200) ≈ ±6,4 pts para p = 69,5 %. -->
<!-- Las dos corridas de v1 (69,0 y 69,5) muestran la variación entre corridas: menor que el margen. -->

**Tabla 2.** Comparación en las mismas mesas (laboratorio `comparar`). Clasificación % (± 95 %).
| Agente | 600 mesas (semilla 1000) ✔ | 800 mesas (semilla 900000) ✔ |
|---|---|---|
| jackpot v1 | 82,7 ± 3,0 | 80,9 ± 2,7 |
| jackpot v2 | 79,3 ± 3,2 | 78,1 ± 2,9 |
| jackpot v3 | — | 81,9 ± 2,7 (corrida aparte en las mismas mesas; v1 dio 81,0) |
| MiAgente | 73,8 ± 3,5 | 74,2 ± 3,0 |
| Heuristic | 71,7 ± 3,6 | 69,5 ± 3,2 |
| Rival | 68,7 ± 3,7 | 68,1 ± 3,2 |

**Tabla 3.** Diferencia pareada v2 − otra versión (puntos de clasificación, ± 95 %).
| Comparación | 600 mesas ✔ | 800 mesas ✔ |
|---|---|---|
| v2 − v1 | −3,3 ± 2,5 | −2,8 ± 2,2 |
| v2 − MiAgente | +5,5 ± 3,3 | +3,9 ± 2,6 |
| v2 − Heuristic | +7,7 ± 4,0 | +8,6 ± 3,4 |
| v2 − Rival | +10,7 ± 4,2 | +10,0 ± 3,3 |

**Búsqueda de parámetros (v2) ✔.** 11 parámetros probados en 95,4 min; ningún cambio superó la confirmación (mayor z combinado: 1,88 en "Implícitas"). [Opcional: Figura 1 con las diferencias de cada candidato y sus barras de error.]

**Tabla 4.** Bench de la versión final en heads-up y en mesa mixta (200 mesas cada uno) ✔.
| Mesa | Agente | Clasif. % | Gana % | Puesto prom. | Stack prom. | ms/dec | ms máx | Timeouts | Errores |
|---|---|---|---|---|---|---|---|---|---|
| Heads-up (vs 1 Heuristic) | jackpot | 74,0 | 74,0 | 1,26 | 10.235 | 3,78 | 23,9 | 0 | 0 |
| | Heuristic | 26,0 | 26,0 | 1,74 | 9.765 | — | — | 0 | 0 |
| Mixta (Heuristic×2, Station, Raiser, Random) | jackpot | 78,5 | 30,0 | 2,44 | 11.218 | 6,20 | 55,5 | 0 | 0 |
| | Heuristic (mejor de los dos) | 74,5 | 24,5 | 2,62 | 11.006 | — | — | 0 | 0 |
| | Station | 36,5 | 11,5 | 4,00 | 9.326 | — | — | 0 | 0 |
| | Raiser | 34,5 | 10,0 | 4,13 | 9.186 | — | — | 0 | 0 |
| | Random | 3,5 | 0,0 | 5,17 | 8.290 | — | — | 0 | 0 |
<!-- En heads-up clasifica solo 1, por eso Clasif% = Gana%.
     En la mesa mixta, jackpot (78,5) vs Heuristic (74,5): diferencia de 4 pts, MENOR que el margen (~±6 con 200 mesas) →
     "sin diferencia demostrada" en clasificación; sí gana más primeros lugares (30,0 vs 24,5) y más fichas. -->

**Tiempo ✔.** En las tres mesas: ms/dec entre 3,78 y 6,20; ms máx 55,5 (< 300 del kit y < 1000 del torneo); 0 timeouts y 0 errores.

---

## 7. Discusión
<!-- ~350–450 palabras. Interpreta, valida o refuta hipótesis, compara con baselines y teoría. -->

- **Contra las líneas base.** [Todas las versiones de jackpot superan a MiAgente, Heuristic y Rival con diferencias mayores al margen de error (Tabla 3). Explica por qué: rangos en vez de manos al azar, umbral de subida que depende del número de rivales.]
- **Más reglas no fue mejor.** [La hipótesis era que posición + tipos de rival + ajuste final (v2) mejorarían. Se refutó: v2 rindió ~3 puntos menos que v1 en dos experimentos independientes; combinados, z ≈ 3,6. Posibles causas: con 30 manos hay pocos datos para clasificar a un rival, las etiquetas se equivocan y las reglas fijas por tipo sobre-reaccionan.]
- **Búsqueda de parámetros.** [Ningún cambio se confirmó: el agente está en una zona plana de los parámetros; las mejoras aparentes de la primera prueba (+1,5 a +2,3) eran ruido. Esto muestra por qué hace falta confirmar con mesas nuevas.]
- **v3.** Corregir el contador de retiradas dio +0,9 ± 1,8 pts sobre v1 (800 mesas ✔): sin diferencia demostrada; se conservó v1 por tener más mediciones. [Interpreta: el c-bet afecta pocas manos.]
- **Mesa mixta.** Contra rivales "explotables" (Station, Raiser, Random) jackpot y los Heuristic clasifican casi siempre; la ventaja de jackpot sobre Heuristic en clasificación (78,5 vs 74,5) no supera el margen, aunque sí gana más mesas (30,0 vs 24,5 %). [Interpreta.]
- **Tiempo.** ms máx 55,5 ms en el peor bench: muy por debajo de 300 ms y 1000 ms; margen para la Arena en Unity/Mono, que es más lenta.
- **Limitaciones.** Rivales de práctica distintos a los del torneo; Chen es una aproximación del valor de las manos; no se modelan farol del rival ni odds implícitas exactas.

---

## 8. Conclusiones
<!-- ~150–200 palabras. Cumplimiento de cada objetivo y aporte principal. Concreto: qué logró y qué no. -->

1. Objetivo 1: [cumplido/no — dato de ms/dec y ms máx].
2. Objetivo 2: [cumplido — qué estadísticos se modelan].
3. Objetivo 3: [cumplido/no — clasificación final vs MiAgente y Heuristic].
4. Objetivo 4: [cumplido — la versión final se eligió con diferencias pareadas].

**Aporte principal:** [p. ej. "evidencia de que, con solo 30 manos por mesa, un modelo de rivales simple y suavizado rinde más que uno con categorías y reglas especiales".]
**Trabajo futuro:** [p. ej. probar contra agentes de compañeros; reemplazar Chen por equity precalculada; adaptar la cantidad de simulaciones al tiempo disponible.]

---

## Referencias
<!-- Verifica cada referencia antes de entregar y usa el formato que pida el profesor (APA/IEEE). -->
- Billings, D., Papp, D., Schaeffer, J., & Szafron, D. (1998). Opponent modeling in poker. *AAAI-98*.
- Billings, D., Davidson, A., Schaeffer, J., & Szafron, D. (2002). The challenge of poker. *Artificial Intelligence, 134*(1–2), 201–240.
- Metropolis, N., & Ulam, S. (1949). The Monte Carlo method. *Journal of the American Statistical Association, 44*(247), 335–341.
- Sklansky, D. (1999). *The Theory of Poker*. Two Plus Two Publishing.
- Suffecool, K. Cactus Kev's Poker Hand Evaluator (evaluador usado por `HandEvaluator` del SDK).
- [Fuente de la fórmula de Chen — verificar.]
- AgentsPoker SDK v0.3.0: LEEME.md, GUIA_AGENTES.md, REGLAS.md (material del curso).
