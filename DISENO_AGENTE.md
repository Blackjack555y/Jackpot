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
| jackpot reentrenado por el compañero | El mejor contra rivales entrenados no vistos: 72 % frente a 61 % (z = 9,8) | Ajustar los pesos de una regla sólida funciona. Este diseño generaliza esa idea con la liga. |

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
