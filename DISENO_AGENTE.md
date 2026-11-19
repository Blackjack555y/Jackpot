# jackpot: diseño del agente (v2) y sistema de entrenamiento

Agente de Limit Hold'em para AgentsPoker SDK v0.3.0. Archivo de entrega: `Kit_AgentsPoker_SDK/Agents/jackpot.cs`.

Este documento replantea el diseño en tres partes:
1. **El agente:** cómo decide en la mesa. Usa matrices de preflop y postflop, equity Monte Carlo, faroles con frecuencia teórica y
   un modelo de rivales.
2. **El sistema de recompensas:** qué se maximiza.
3. **El entrenamiento:** una liga contra versiones pasadas del propio agente, con una puerta de ascenso que exige ganar un número
   mínimo de veces antes de cambiar los pesos.

La parte 4 explica cómo se evita el sobreajuste, y el anexo resume los experimentos que llevaron a este diseño.

---

## 1. El problema

| Elemento | En el torneo |
|---|---|
| **Entorno** | Parcialmente observable (POMDP): no vemos las cartas rivales. Es estocástico, multiagente y secuencial. |
| **Acciones** | FOLD, CALL (check) y RAISE (bet). Es *Limit*: la apuesta vale 1 BB en preflop y flop, y 2 BB en turn y river. El tope es de 4 apuestas por calle. |
| **Objetivo** | **Clasificar**: quedar en el top 3 de la mesa tras 30 manos. Las fichas solo importan en cuanto deciden el puesto. |
| **Rivales** | Agentes entrenados de otros compañeros, desconocidos de antemano. |
| **Restricciones** | 1000 ms por decisión (la Arena corre en Mono, que es más lenta). Una sola clase, sin archivos, sin hilos, sin `static` modificable y sin redes neuronales. |

---

## 2. Arquitectura del agente

```
                ┌──────────────────────────────────────────────────────────────┐
 Observación ──►│ 1. Modelo de rivales (VPIP, PFR, agresión, fold, faroles vistos)│
                └──────────────┬───────────────────────────────────────────────┘
                               ▼
        Preflop ──► 2. Matriz 13×13 de manos iniciales × situación ──► acción
        Postflop ─► 3. Equity Monte Carlo contra rangos (fuerza + potencial)
                               ▼
                    4. Matriz de decisión postflop: categoría de mano × situación ──► acción base
                               ▼
                    5. Capa de faroles (c-bet, semifarol, farol de river con frecuencia α)
                               ▼
                    6. Ajuste por puesto (recompensa de clasificar) ──► acción final
```

Todos los números de estas matrices (umbrales, márgenes y frecuencias) forman el **vector de pesos θ**. Ese vector es lo que se
entrena en la liga (parte 3), y en el archivo entregado queda como `static readonly double[]`.

### 2.1 Modelo de rivales (aprendizaje durante la mesa)

- **Qué mide de cada rival**, con `OnHandEnd`:
  - **VPIP:** cuánto juega;
  - **PFR:** cuánto sube preflop;
  - **agresión:** proporción de subidas;
  - **fold ante apuesta:** cuánto se retira;
  - **faroles mostrados** en showdown.
- **Suavizado bayesiano:** cada estadística parte de un *prior* equivalente a k manos ficticias, (aciertos + p₀·k) / (manos + k),
  para no sobrerreaccionar a 3 o 4 manos.
- **Clasificación del rival en 4 estilos:**

| Estilo | Señal | Cómo lo explotamos |
|---|---|---|
| **Roca** (*tight-passive*) | VPIP bajo, poca agresión | Le robamos ciegas y c-bets. Si sube, le creemos y nos retiramos. |
| **Calling station** | VPIP alto, casi nunca se retira | **Cero faroles.** Apostamos por valor más fino (pareja media). |
| **Maníaco** (*loose-aggressive*) | Agresión muy alta | Pagamos más (su rango de farol es amplio). Dejamos que apueste por nosotros (check-call). |
| **Normal** (TAG) | Valores intermedios | Jugamos la estrategia base equilibrada. |

### 2.2 Preflop: matriz 13×13 de manos iniciales

- **Las 169 manos:** las 1.326 combinaciones de dos cartas se agrupan en 169 clases:
  - 13 parejas en la diagonal;
  - 78 *suited* sobre la diagonal;
  - 78 *offsuit* debajo.
- **Fuerza de cada clase:** su percentil se calcula una sola vez (fórmula de Chen) y queda en una tabla de solo lectura.
- **La decisión** es una matriz **clase de mano × situación**:

| Situación \ franja de la mano | Top 5 % (AA–QQ, AKs) | 5–15 % | 15–30 % | 30–50 % | Resto |
|---|---|---|---|---|---|
| Nadie entró (*open*) | Subir | Subir | Subir si posición tardía, si no pasar/foldear | Robo solo en botón/CO contra ciegas tight | Fold |
| Alguien solo pagó (*limp*) | Subir | Subir | Pagar | Pagar si posición tardía | Fold |
| Alguien subió | 3-bet | 3-bet / pagar | Pagar si tenemos odds | Fold | Fold |
| Hubo 3-bet o más | 4-bet | Pagar | Fold | Fold | Fold |

- **Qué se ajusta:** los cortes de cada franja dependen del número de rivales y de la posición. Con menos rivales, se abre el rango.
  Esos cortes son pesos de θ.
- **Posición:** el botón y la posición anterior (CO) actúan últimos postflop, así que pueden jugar más manos.

### 2.3 Postflop: equity Monte Carlo + matriz de decisión

1. **Equity contra rangos, no contra manos al azar:**
   - en cada simulación se sortea una mano para cada rival **dentro de su rango estimado**;
   - el rango sale de su VPIP y de si subió;
   - si apostó postflop, se descartan con cierta probabilidad sus manos sin pareja.
   Con eso se completa el board y se cuenta cuántas veces ganamos. Son unas 3.000 a 6.000 simulaciones en menos de 150 ms.
2. **Fuerza efectiva** (idea del *Effective Hand Strength* de la Universidad de Alberta): se combina la fuerza actual (HS) con el
   potencial de mejorar (PPOT) o de ser superado (NPOT). Así un proyecto de color con poca fuerza actual se trata como mano jugable.
3. **Matriz de decisión postflop:** categoría de mano × situación.

| Categoría (por equity / fuerza efectiva) | Nadie apostó | Nos apostaron | Nos subieron |
|---|---|---|---|
| **Monstruo** (equity > 0,80) | Apostar; si nos pagan, check-raise en turn | Subir | Resubir |
| **Fuerte** (0,60–0,80) | Apostar por valor | Subir o pagar según textura | Pagar |
| **Media** (pareja media, 0,40–0,60) | Apostar si hay 1–2 rivales, pasar si hay más | Pagar si equity ≥ pot odds | Fold salvo odds |
| **Proyecto** (color/escalera abierta, PPOT alto) | **Semifarol** (apostar) | Pagar con odds implícitas o semifarol-subida | Pagar con odds directas |
| **Aire** (equity < 0,25, sin proyecto) | Capa de faroles (2.4) | Fold (o farol de river si α lo permite) | Fold |

- **Regla de pago:** pagar si equity + odds implícitas ≥ pot odds = ToCall / (bote + ToCall).
- **Los cortes** (0,25, 0,40, 0,60 y 0,80) son valores iniciales y forman parte de θ.

### 2.4 Faroles con base teórica

**a) Frecuencia de farol de river: α = apuesta / (bote + apuesta).** α es la proporción de veces que el rival tiene que retirarse
para que un farol salga a cuenta. Del lado del que defiende está la **MDF** (frecuencia mínima de defensa) = bote / (bote + apuesta).
En Limit la apuesta es pequeña frente al bote, así que los faroles salen baratos.

| Bote en el river | Apuesta | α (fold necesario) | Faroles por cada apuesta de valor (equilibrado) |
|---|---|---|---|
| 6 BB | 2 BB | 25 % | 1 : 3 |
| 10 BB | 2 BB | 17 % | 1 : 5 |
| 16 BB | 2 BB | 11 % | 1 : 8 |

- Con un rango equilibrado, la proporción de faroles dentro de las apuestas es α.
- El agente farolea en el river con sus peores manos (aire) con probabilidad tal que los faroles sean α de su rango de apuesta. Así
  queda **inexplotable**.

**b) Explotación:** el equilibrio es la base, y el modelo de rivales lo ajusta.
- Si la tasa de fold observada del rival es **mayor** que α, farolear más.
- Si es **menor**, contra una calling station, faroles = 0.
- Con la defensa pasa lo mismo: contra quien casi no farolea nos retiramos más que la MDF, y contra un maníaco defendemos más.

**c) Apuesta de continuación (c-bet):** el que subió preflop apuesta el flop.
- Solo contra 1 o 2 rivales que suelen retirarse.
- Más en boards secos (A-7-2 de palos distintos) y menos en boards coordinados.

**d) Semifarol:** apostar o subir con proyectos.
- Gana de dos formas: el rival se retira ahora, o completamos la mano después.
- Se prioriza con PPOT alto: proyecto de color, de escalera abierta o de doble *gutshot*.

**e) Check-raise:** con manos monstruo cuando el rival es agresivo, para ganar una apuesta más. También ocasionalmente como semifarol.

**f) Robo de ciegas:** desde posición tardía, cuando nadie entró y las ciegas son tight.

Todas las frecuencias (probabilidad de c-bet, multiplicador sobre α, umbral de PPOT para semifarol) son pesos de θ.

### 2.5 Seguridad

- Cada decisión tiene un `Stopwatch`: el Monte Carlo se corta a los 150–200 ms, y el tope está muy lejos de los 1000 ms.
- Todo va dentro de `try/catch`; ante un error, CALL si pasar es gratis y FOLD si no.
- Sin estado `static` modificable: cada mesa crea una instancia nueva.

---

## 3. Sistema de recompensas

### 3.1 Dentro de la mano (lo que usa el agente para decidir)

| Momento de la mesa | Utilidad | Por qué |
|---|---|---|
| Manos 1–24 | **Fichas esperadas (en BB)** | Al principio, más fichas significa más probabilidad de clasificar. Es una señal densa. |
| Manos 25–30 | **Probabilidad de quedar en el top N** | Coincide con la métrica real. Si vamos dentro con margen, jugamos más apretado; si vamos fuera, arriesgamos más. |

- **Top N** = mín(3, jugadores con fichas − 1).
- En el tramo final el umbral de equity se desplaza según el margen sobre el primero que queda fuera. Ese desplazamiento es un peso
  de θ.

### 3.2 En el entrenamiento (lo que decide si los pesos mejoran)

- **Recompensa por mesa:** R = 1 si el candidato clasificó y 0 si no.
- **Por qué clasificar y no fichas:** la clasificación no es lineal en las fichas.
  - Un agente que maximiza fichas acepta riesgos que hacen perder el puesto.
  - Entrenar con 1/0 alinea el aprendizaje con el objetivo del torneo.
- **Desempate:** + 0,001 × fichas finales, para distinguir entre candidatos igual de buenos.

---

## 4. Entrenamiento: liga contra versiones pasadas

### 4.1 La idea

En vez de entrenar contra los bots del profesor, el agente entrena contra **su propia historia**: cada versión aceptada se congela y
se agrega al grupo de rivales. Es la idea del *fictitious self-play*: responder bien a la **mezcla** de todas las estrategias
pasadas, no solo a la última. Así se evita olvidar cómo ganarle a estilos anteriores.

```
 θ₀ (versión inicial) ──► liga = {θ₀}
 repetir:
   1. proponer θ' = θ_campeón + perturbación pequeña (o un paso de mejora)
   2. jugar M mesas: θ' contra rivales sorteados de la liga
   3. PUERTA: ¿θ' ganó al campeón en ≥ X % de las mesas pareadas, con significancia?
        sí → θ' es el nuevo campeón; se congela y entra a la liga
        no → se descarta θ'
```

### 4.2 Quién juega en cada mesa de entrenamiento

| Fracción de asientos | Rivales | Para qué |
|---|---|---|
| ~35 % | **Campeón actual** | No empeorar contra la mejor versión. |
| ~35 % | **Versiones pasadas**, con prioridad a las que más nos ganan (*prioritized fictitious self-play*, como en AlphaStar) | Atacar nuestras debilidades conocidas. |
| ~20 % | **Versiones muy antiguas** (θ₀, θ₁…) | No olvidar cómo ganar a estilos simples. |
| ~10 % | **Anclas de estilo fijo**: un maníaco, una calling station y una roca | Mantener diversidad de estilos en la mesa. |

### 4.3 La puerta de ascenso ("ganar sí o sí un número de veces")

- **Inspiración:** en AlphaGo Zero, una red nueva solo reemplazaba a la mejor si le ganaba el **55 %** de las partidas de evaluación.
  Esa cifra viene de fuentes secundarias; ver la sección de fuentes.
- **Versión para póker:**
  - θ' y el campeón juegan **las mismas mesas** (misma semilla, mismos rivales, mismo asiento). Es una comparación pareada que
    elimina la suerte de las cartas.
  - **Condición 1:** θ' clasifica más que el campeón en esas mesas, con z > 2 (unas 600 mesas).
  - **Condición 2, confirmación:** se repite en **600 mesas nuevas** y debe volver a ganar. Así una racha de suerte no basta.
  - **Condición 3, no olvidar:** contra las versiones muy antiguas, θ' no puede caer más de 1 punto.

### 4.4 Cómo se cambian los pesos θ

Sin redes neuronales se usan métodos de búsqueda directa, que encajan con una evaluación ruidosa:
- **Estrategia evolutiva (1+1)-ES:** se perturban todos los pesos con ruido gaussiano. Si θ' pasa la puerta, el paso crece un poco; si
  no, se reduce (regla del 1/5).
- **Alternativa por coordenadas:** se mueve un solo peso a la vez (por ejemplo, el multiplicador de α), lo que da cambios más fáciles
  de explicar en la presentación.

---

## 5. Cómo se evita el sobreajuste

1. **Rivales que cambian:** la liga crece con cada versión, así que los pesos no pueden ajustarse a un rival fijo.
2. **Versiones de reserva (*hold-out*):** 1 de cada 3 versiones congeladas **nunca** se usa para entrenar, solo para medir. Si el
   agente mejora contra la liga pero no contra la reserva, está sobreajustando a la liga y se detiene.
3. **Semillas nuevas en cada confirmación:** la mesa que decide nunca es la misma con la que se eligió el cambio.
4. **El veredicto final usa rivales no vistos:** la última evaluación se hace contra agentes que no participaron en el entrenamiento
   (versiones de reserva y agentes de otros autores), con la regla de decisión escrita **antes** de correrla.
5. **No se mide "ganarle a los bots del profesor":** como métrica principal sería sobreajuste, porque esos bots son fijos y conocidos.
   Se usan solo como control de que nada se rompe.

---

## 6. Plan de implementación

1. Reescribir la capa de decisión como matrices parametrizadas por θ: preflop 13×13 × situación, y postflop categoría × situación.
   Agregar la capa de faroles con α.
2. `Entrenamiento/Program.cs`, modo `liga-versiones`:
   - carpeta de versiones congeladas;
   - sorteo de rivales según la tabla 4.2;
   - puerta 4.3 y (1+1)-ES.
3. Correr la liga en segundo plano y guardar la curva del campeón contra la reserva.
4. Evaluación final pareada contra rivales no vistos, con la regla fijada antes. `check` y bench con 0 timeouts.

---

## Anexo A. Lo que ya se probó y por qué llevó a este diseño

| Enfoque | Resultado | Lección |
|---|---|---|
| Q-learning + UCB por self-play | Sin campeón confirmado | El espacio de estados es enorme y 30 manos dan una señal ruidosa: mejor ajustar pocos pesos interpretables (θ). |
| CFR (MCCFR, 300k it.) | Entre −8 y −12 pts contra la v1 | El equilibrio no explota a rivales débiles. Por eso aquí el equilibrio (α, MDF) es solo la **base** y se explota con el modelo de rivales. |
| v1 + rollouts (simular la mano) | +3 pts contra bots explotables y **−6 pts contra agentes entrenados** | Un modelo de oponente simplista engaña al planificador. |
| jackpot reentrenado por el equipo (E1) | El mejor contra rivales entrenados no vistos: 72 % frente a 61 % (z = 9,8) | Ajustar los pesos de una regla sólida funciona. Este diseño generaliza esa idea con la liga. |

**Protocolo de evaluación usado (y que se mantiene):**
- comparación pareada en las mismas mesas;
- grupos de 1.000 mesas con semillas nuevas;
- regla de decisión fijada antes de ver resultados;
- corrección de Holm cuando se comparan varios candidatos a la vez.

Resultados completos: `README.md`, sección 5.

---

## Fuentes

- **Liga y *prioritized fictitious self-play*:** DeepMind, *AlphaStar: Grandmaster level in StarCraft II using multi-agent
  reinforcement learning* (2019). [Blog de DeepMind](https://deepmind.google/discover/blog/alphastar-grandmaster-level-in-starcraft-ii-using-multi-agent-reinforcement-learning/)
  · [Resumen técnico, TStarBot-X](https://arxiv.org/pdf/2011.13729).
- **Fictitious self-play en Limit Hold'em:** Heinrich y Silver, *Deep Reinforcement Learning from Self-Play in Imperfect-Information
  Games* (2016). [arXiv:1603.01121](https://arxiv.org/pdf/1603.01121).
- **Puerta de ascenso del 55 %:** descrita para AlphaGo Zero en [Survey on Self-play Methods in RL](https://arxiv.org/pdf/2408.01072) y
  en el [preprint de AlphaZero](https://discovery-pp.ucl.ac.uk/id/eprint/10069050/1/alphazero_preprint.pdf). Son fuentes secundarias:
  conviene contrastarla con Silver et al. (2017).
- **Fuerza y potencial de la mano (HS, PPOT, NPOT, EHS):** Billings, Davidson, Schaeffer y Szafron, *The Challenge of Poker*,
  Artificial Intelligence 134 (2002). [Universidad de Alberta](https://papersdb.cs.ualberta.ca/~papersdb/view_publication.php?pub_id=578)
  · [Resumen de EHS](https://en.wikipedia.org/wiki/Effective_hand_strength_algorithm).
- **Limit Hold'em resuelto (CFR+):** Bowling, Burch, Johanson y Tammelin, *Heads-up limit hold'em poker is solved*, Science 347 (2015).
  [PDF](https://www.cs.ualberta.ca/~games/poker/publications/heads-up_limit_poker_is_solved.acm2017.pdf).
- **α, MDF y frecuencias de farol:** [Frequencies and balance (DeucesCracked)](https://www.deucescracked.com/learn/frequencies-and-balance)
  · [MDF (PokerSkill)](https://www.pokerskill.com/poker-glossary/minimum-defense-frequency/)
  · [Alpha (PokerSkill)](https://www.pokerskill.com/poker-glossary/alpha/)
  · [Steve Zolotow, *Thoughts on Game Theory*, Card Player](https://cardplayer.com/cardplayer-poker-magazines/66418-bryn-kenney-32-19/articles/23789-thoughts-on-game-theory-part-6).
- **Rollouts y planificación online:** Sutton y Barto, *Reinforcement Learning: An Introduction*, 2.ª ed., §8.10–8.11 (clase 12).

---

## Anexo B. Segundo reentrenamiento del equipo (E2) y liga nocturna (2026-11-17)

**Versión nueva (E2):** tiene el mismo código que E1; solo cambian los 22 pesos. El equipo la entrenó contra el ecosistema
"FI + jackpot + Random×2".

**Revisión previa, con los benches del kit en las mismas semillas para los dos:**

| Bench (200 mesas) | E1 | E2 |
|---|---|---|
| Contra 5 Heuristic | 71,0 % | **61,0 %** |
| Heads-up contra Heuristic | 78,0 % | **66,5 %** |
| Reserva de la liga (rivales nunca vistos, 800 mesas) | 67,8 % | 65,9 % |

- Es una señal de sobreajuste al ecosistema de FI, pero todavía no es una prueba: falta la evaluación pareada.
- En el log de ese entrenamiento **todos** los agentes tienen 9 a 15 timeouts, incluso Random, que tarda 0 ms. Eso indica que esos timeouts
  vienen de la máquina (12 hilos compitiendo por la CPU), no del agente. En nuestros benches, E2 tiene 0 timeouts.

**Evaluación de E2** (modo `decision2`, semillas 9.900.001+): candidatos E2, E1 y v1. Se usa la misma regla de la
sección 7, fijada antes de correr:
1. gana el mejor en H;
2. si hay empate (z > −2), decide G;
3. el elegido no puede ser peor que E1 (z < −2) en G.

**Liga nocturna** (modo `liga`, parte 4 de este documento):
- **Pesos iniciales:** θ₀ = los de E1, el ganador de la sección 7.
- **Rivales:** el campeón (35 %), versiones pasadas con PFSP (35 %, empieza con v0 y E2), antiguas (20 %: v1, v2, FI, MiAgente
  y Heuristic) y anclas (10 %: Station, Raiser y Random).
- **Puerta de ascenso:**
  - z > 2 en 400 mesas pareadas;
  - z > 2 en 600 mesas nuevas;
  - no caer más de 1 punto contra antiguas y anclas.
- **Reserva (solo mide):** jackpot_v3, jackpot_q, Rival y jackpot_v1. 1 de cada 3 campeones queda fuera de la liga.
- **Early stopping:** se detiene tras 3 ascensos seguidos sin mejorar la reserva, y entonces se restaura el mejor.
- **Después:** la reserva se usó para detener, así que el veredicto final se hará con semillas nuevas y la regla de la sección 7.

### B.1 Resultado de la evaluación de E2 (2026-11-18)

Se usaron 1000 mesas por grupo, pareadas (mismas semillas, rivales y asientos para los tres).

| Agente | H1 | H2 | G1 (con FI) | G2 |
|---|---|---|---|---|
| E2 | 68,0 % | 67,3 % | 78,4 % | 80,6 % |
| **E1** | **69,6 %** | **71,8 %** | 76,6 % | 81,0 % |
| v1 | 62,3 % | 64,0 % | 74,4 % | 79,3 % |

| Comparación | z en H (rivales entrenados) | z en G (mezcla) |
|---|---|---|
| E2 − E1 | **−3,10** (peor, significativo) | +0,72 (empate) |
| E2 − v1 | +4,19 | +2,68 |
| E1 − v1 | +6,84 | +1,85 |

**Veredicto (regla de la sección 7):**
- Gana **E1**.
- E2 es significativamente peor contra rivales entrenados que nunca vio. Eso confirma la sospecha de sobreajuste al
  ecosistema "FI + jackpot + Random×2" con el que se entrenó.
- Lección para el informe: **entrenar contra pocos rivales fijos sube el número en ese ecosistema y lo baja fuera de él.** Por
  eso la liga mezcla versiones pasadas, agentes antiguos y anclas, y mide en una reserva que nunca entrena.

### B.2 Estado de la liga y prueba continua en torneos

- **Primeras 3 h 15 min:** 39 rondas, **0 ascensos**.
  - Ningún candidato pasó la primera puerta (z > 2 en 400 mesas). El mejor llegó a z = 1,83.
  - El tamaño de paso sigma bajó a su mínimo (0,15). Ahora busca cambios pequeños alrededor de E1.
- **Cómo se interpreta:**
  - La puerta hace su trabajo: un empate o un golpe de suerte no se acepta como mejora.
  - E1 está cerca del óptimo local de esta lógica. **Cada ascenso que salga es una mejora real, no ruido.**
- **Regla de mejora:** los pesos solo cambian si el candidato gana. Si la liga termina sin ascensos, se entrega E1, que ya está
  validado.
- **Prueba continua en torneos (modo `vigilar`):**
  - Corre en un proceso aparte con solo 2 hilos, para no cargar la PC.
  - Cada vez que la liga saca un campeón, lo prueba en 600 mesas de torneo: 30 manos, clasifican 3, con 4 a 6 jugadores.
  - Los rivales son solo **agentes entrenados**: FI, Rival, MiAgente, Heuristic y jackpot v1, v2, v3 y q.
  - Usa una semilla propia (7.700.000), distinta de las de la liga.
  - Compara de forma pareada contra E1 y contra v1, y además mide **cuánto se alejaron los pesos de los de E1**, en "pasos"
    de la escala de mutación.
  - La salida queda en `LabCFR/salida/liga/torneos_campeones.txt`.
- **Veredicto final:** el campeón que salga se compara con E1 en semillas nuevas, con la regla de la sección 7. **El ganador es
  el que se entrega.**

### B.3 Resultado final de la liga de 8 horas y agente entregado (2026-11-18)

**La liga:**
- Duró 480 min: 79 rondas de 400 mesas pareadas por candidato. Hubo **0 ascensos**.
- Solo un candidato pasó la primera puerta: la ronda 63, con +4,0 puntos y z = 2,28. En las 600 mesas nuevas de confirmación no
  se sostuvo. Era ruido, y la puerta lo rechazó.
- Otros 4 candidatos se acercaron (z entre 1,55 y 1,83) sin llegar a 2.
- La reserva queda en 67,8 % y no cambió.
- **Conclusión:** con esta lógica, θ₀ ya es un óptimo local. Ningún cambio de pesos dio una mejora confirmada, y por eso **no se
  aceptó ninguno**. Así es como debe actuar un sistema que evita el sobreajuste.

**Prueba en torneos simulados** (modo `vigilar`, 600 mesas, solo rivales entrenados, semilla 7.700.000): θ₀ clasifica **71,2 %**
y v1 clasifica 66,8 %.

**Pesos finales entregados** (`ParametrosBase` de `Agents/jackpot.cs`), iguales a θ₀:

| # | Peso | Valor | # | Peso | Valor |
|---|---|---|---|---|---|
| 0 | Margen para apostar | −0,1970 | 11 | Ajuste de clasificación (a) | 0,2160 |
| 1 | Margen para subir | 0,1113 | 12 | Ajuste de clasificación (b) | −0,3790 |
| 2 | Margen por apuesta | 0,0572 | 13 | Ajuste de clasificación (c) | 400,2586 |
| 3 | Prior VPIP | 0,3996 | 14 | Umbral de fold para c-bet | −0,2077 |
| 4 | Prior agresión | 0,2478 | 15 | Rango (a) | 0,7452 |
| 5 | Prior fold | −0,1242 | 16 | Rango (b) | 0,3520 |
| 6 | Peso del prior | 8,2535 | 17 | Rango (c) | 0,4198 |
| 7 | Probabilidad de c-bet | 0,5072 | 18 | Rango (d) | −0,2356 |
| 8 | Odds implícitas (a) | −0,4239 | 19 | Rango (e) | 0,5116 |
| 9 | Odds implícitas (b) | −0,0791 | 20 | Rechazo en el muestreo (a) | 0,6256 |
| 10 | Odds implícitas (c) | −0,2715 | 21 | Rechazo en el muestreo (b) | 0,5591 |

**Comprobaciones del agente entregado** (kit oficial, 200 mesas):

| Prueba | Clasifica | ms/dec | ms máx | Timeouts | Errores |
|---|---|---|---|---|---|
| `check` | OK | — | — | — | — |
| Contra 5 Heuristic | 66,5 % (los Heuristic: 44 a 49,5 %) | 3,95 | 36,6 | 0 | 0 |
| Heads-up contra Heuristic | 73,0 % | 2,39 | 48,9 | 0 | 0 |

La versión anterior con rollouts quedó descartada: contra rivales entrenados perdió frente a θ₀ (z 9,8 en H, sección 7).

---

## Anexo D. jackpot y FI: por qué son agentes distintos

FI y jackpot son dos agentes distintos. Los dos estarán en el torneo, así que conviene dejar claro que **no** son el
mismo diseño. Coinciden en lo básico que hace cualquier agente razonable: Monte Carlo y estadísticas de rivales. La forma de usar
cada pieza es diferente.

| Pieza | FI | jackpot |
|---|---|---|
| **Preflop** | Tabla fija 13×13 (169 valores escritos a mano) + "concepto de brecha", sin open-limp | Percentil de las 1326 combinaciones ordenadas con la **fórmula de Chen**; el umbral (rango) es un peso entrenable |
| **Modelo del rival** | Belief state bayesiano: VPIP, agresión y fold con priors fijos (1,68/6, 2,56/8, 2,28/6) | VPIP, PFR, agresión y fold con priors **entrenables** (pesos 3 a 6); el rango de cada rival sale de su VPIP y de lo que hizo en la mano |
| **Cómo se usa el rival en el Monte Carlo** | Pondera las manos simuladas por la "selectividad" del rival | **Muestreo por rechazo**: descarta manos que el rival no jugaría (fuera de rango; sin pareja si apostó), con probabilidad entrenable |
| **Decisión postflop** | Equity + pot odds con **búsqueda de quietud** (Russell y Norvig) + equity backdoor fija (4,2 %) y luego una **red neuronal MLP 16→12→3** que corrige fold/call/raise | Regla directa: equity contra la parte justa del bote (1/(n+1)) + margen por número de apuestas para subir, y pot odds + odds implícitas para pagar. **Sin red neuronal**: cada número tiene un significado explicable |
| **Faroles** | Los decide la red (neurona de semifarol / fold equity) | C-bet selectiva: solo si fuimos el último agresor, hay pocos rivales y esos rivales foldean más que un umbral entrenable. **Nunca contra calling stations** |
| **Fin de mesa (top 3)** | Controlador de supervivencia desde la mano 22 (ICM simplificado) | `AjusteClasificacion`: más apretado si vamos cómodos dentro del top, más suelto si vamos fuera, según el margen con el primero que quedaría fuera |
| **Tiempo por decisión** | ~175 ms (tope 220 ms) | Corte a 150 ms |
| **Cómo se entrenó** | Pesos de la red con Double Q-learning + TD(λ) y self-play | **Liga contra versiones pasadas** con puerta de ascenso pareada y reserva que nunca entrena (partes 4 y 5) |

**Origen del código entregable:**
- La lógica de jackpot es la v1 de este repositorio (`jackpot_base`).
- El equipo convirtió sus constantes en 22 pesos (`ParametrosBase`) y los reentrenó (E1). La lógica no cambió.
- La liga ajusta esos mismos 22 pesos. Por eso el agente final conserva el diseño propio de jackpot y queda alejado de FI tanto en
  arquitectura como en entrenamiento.
- El modo `vigilar` mide en cada campeón cuánto se alejaron sus pesos de los de E1.

**Consecuencia práctica:**
- FI depende de una red con pesos fijos. Si un rival juega distinto a lo que vio en su self-play, la red no se adapta dentro de la
  mesa.
- jackpot se adapta mano a mano: el rango y el rechazo de cada rival cambian con lo que observa.
- jackpot tampoco depende de una tabla de 169 valores escritos a mano. Su orden preflop sale de una fórmula conocida (Chen) y
  solo el corte se entrena.

---

## Anexo C. Conceptos de póker sin matrices: reglas con pesos entrenables

Por decisión del equipo **no se implementan las matrices de 2.2 y 2.3**. Los mismos conceptos se cubren con **reglas cortas sobre lo
que el agente ya calcula** (equity Monte Carlo, percentil de Chen, perfil de cada rival). Cada regla tiene **un peso** que se suma
al vector θ y que entrena la liga, igual que los 22 pesos actuales. El código queda fijo y solo cambian los números.

| Concepto de póker | Cómo se cubre sin matriz | Peso nuevo (lo entrena la liga) |
|---|---|---|
| **Selección de manos preflop** | Ya existe: el percentil de Chen frente a un umbral (el rango). Se agrega que el umbral se abra en posición tardía (botón y CO) y con menos rivales. | Apertura extra por posición |
| **Robo de ciegas** | Si nadie entró, estamos en el botón o CO y las ciegas tienen tasa de fold alta → subir con el percentil algo peor. | Umbral de robo |
| **Semifarol** | Detectar proyecto de color (4 del mismo palo) o de escalera abierta en flop y turn → sumar un bono a la equity para la decisión de subir. Así se aproxima el potencial (PPOT) sin calcularlo aparte. | Bono por proyecto |
| **Farol de river con α** | En el river, sin pareja y con nadie apostando: farolear con probabilidad = peso × α, con α = apuesta / (bote + apuesta). Solo contra rivales cuya tasa de fold supere α (nunca contra calling stations). | Multiplicador de α |
| **Defensa con MDF** | Ante apuesta en el river: si el rival casi no farolea (agresión baja), exigir más equity para pagar; si es maníaco, menos. | Ajuste de defensa por agresión |
| **Check-raise** | Con equity muy alta en flop o turn, contra un rival agresivo y fuera de posición: pasar para subir después. | Umbral de check-raise |
| **C-bet** | Ya existe (`DebeContinuar`). Su probabilidad y su umbral de fold ya son pesos. | (ya está) |

**Por qué así:**
- Son 6 pesos nuevos frente a cientos de celdas de una matriz, así que la liga puede ajustarlos con pocas mesas y con menos riesgo
  de sobreajuste.
- Cada regla se explica en una frase en la presentación y tiene base teórica (α, MDF, semifarol).
- El valor inicial de cada peso es **neutro** (la regla apagada o casi apagada). Así la versión de partida juega igual que la actual,
  y la liga solo los enciende si demuestran mejora en la puerta de ascenso.

**Orden de trabajo:**
1. Termina la liga de esta noche, que ajusta solo los 22 pesos actuales.
2. Prueba final con semillas nuevas.
3. Se agregan estas 6 reglas al mismo código, con pesos neutros, y se corre otra liga sobre los 28 pesos.
4. Prueba final otra vez con semillas nuevas y la regla de la sección 7.

> **Estado:** por la fecha de entrega, el anexo C queda como **trabajo futuro**. La versión entregada solo ajusta los 22 pesos
> actuales con la liga (anexo B). La lógica del agente no se modifica.
