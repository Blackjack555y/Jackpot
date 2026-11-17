# jackpot: diseño del agente y sistema de recompensas

Agente de Limit Hold'em para AgentsPoker SDK v0.3.0. Archivo de entrega: `Kit_AgentsPoker_SDK/Agents/jackpot.cs`.

## 1. El problema, en términos del curso

| Elemento | En el torneo |
|---|---|
| **Entorno** | Parcialmente observable (POMDP): no vemos las cartas de los rivales. Es estocástico (mazo), multiagente y secuencial. |
| **Estado observable** | Nuestras cartas, el board, el bote, lo que cuesta pagar (`ToCall`), los stacks y el historial de acciones. |
| **Acciones** | FOLD, CALL (check) y RAISE (bet). Es Limit, así que el tamaño de la apuesta es fijo. |
| **Objetivo real** | **Clasificar**: terminar la mesa de 30 manos en el top 3. No importa ganar muchas fichas, solo quedar dentro. |
| **Restricciones** | 1000 ms por decisión (la Arena corre en Mono, que es más lenta). Una sola clase, sin archivos, sin hilos y sin estado `static` modificable. |

## 2. Sistema de recompensas (función de utilidad)

La utilidad cambia según el momento de la mesa, porque lo que de verdad cuenta es el puesto final.

| Momento de la mesa | Utilidad que maximiza | Por qué |
|---|---|---|
| Manos 1 a 27 | **Fichas ganadas, en ciegas grandes (BB)** | Al principio, más fichas equivale a más probabilidad de clasificar. Las fichas son una señal densa: cada simulación da un número y no solo un 0/1. |
| Últimas 3 manos | **U = 1 si quedamos en el top N con los stacks finales, 0 si no** (+ 0,001 × fichas para desempatar) | Coincide exactamente con la métrica del torneo. Si vamos 3.º con margen, no conviene arriesgar el puesto por unas fichas más. |
| Últimas 6 manos (capa v1) | **Ajuste de riesgo por puesto**: si estamos dentro con más de 400 fichas de margen, se juega más apretado (+0,05 de equity exigida). Si estamos fuera, más suelto (−0,03). | Es la misma idea aplicada a la regla de la v1. |

- **Top N** es el mínimo entre 3 y (jugadores con fichas − 1). Es la misma regla del torneo: siempre se elimina al menos uno.
- **Por qué no usar solo fichas:** con recompensa = fichas, un agente que maximiza el valor esperado acepta arriesgar mucho para ganar
  botes grandes, aunque eso le haga perder el puesto (la clasificación no es lineal en las fichas). Por eso los experimentos de self-play (Q-learning + UCB) usaron
  **recompensa = clasificar (1/0)**, y el agente final usa la utilidad de clasificar justo donde decide el puesto.

## 3. Arquitectura: dos capas

```
Observación ──► Capa 1: v1 (regla de decisión con equity) ──► acción propuesta
                     │
                     └──► Capa 2: planificación online (rollouts) ──► ¿hay una acción claramente mejor?
                                                                       sí → la usa · no → se queda la de la v1
```

### Capa 1: v1, decisión por equity contra rangos (máxima utilidad esperada en un paso)

1. **Modelo de cada rival** (aprendido durante la mesa con `OnHandEnd`):
   - mide VPIP (cuánto juega), agresión (cuánto sube) y tasa de fold;
   - las estadísticas se suavizan con un *prior* de 8 manos ficticias, para no sobrerreaccionar a pocas manos (estimación bayesiana simple).
2. **Rango de cada rival:**
   - preflop, el rango sale de su VPIP y de si ya subió;
   - postflop, si apostó, se descartan con cierta probabilidad sus manos sin pareja, según lo agresivo que haya sido.
3. **Equity Monte Carlo:** hasta 6000 simulaciones (unos 150 ms). En cada una se sortean las manos rivales **dentro de su rango**
   (muestreo por rechazo) y el board que falta.
4. **Regla de decisión:**
   - **RAISE** si equity ≥ parte justa del bote (1/(n+1)) + margen. El margen crece en guerras de subidas.
   - **CALL** si equity + odds implícitas ≥ pot odds.
   - **FOLD** en otro caso. Nunca hace fold si pasar es gratis.
   - **Apuesta de continuación selectiva:** solo contra 1 o 2 rivales que suelen retirarse. Nunca contra *calling stations*.

### Capa 2: planificación online por rollouts (Sutton y Barto 8.10, clase 12)

La v1 decide mirando un solo paso. La capa 2 **simula el resto de la mano** para cada acción legal y estima su utilidad esperada.

1. **Determinización** (la forma de tratar el POMDP): en cada simulación se sortea una mano para cada rival, usando los rangos de la
   capa 1, y el board que falta.
2. **Rollout:** se juega la mano hasta el showdown en memoria.
   - Cada jugador usa una **política rápida** según la fuerza de su mano y **su perfil observado** (agresión, fold, VPIP).
   - Se respetan las reglas de Limit: tope de 4 apuestas y tamaño de 1 BB, o 2 BB desde el turn.
3. **Números aleatorios comunes:** la misma mano simulada se juega con cada acción (FOLD, CALL y RAISE). Así la comparación entre
   acciones es **pareada** y tiene mucha menos varianza.
4. **Mejora de política conservadora:** solo se reemplaza la acción de la v1 si otra es mejor por **más de 1,5 errores estándar**.
   Si no hay evidencia clara, se queda la v1. La v1 es la "política por defecto", que el planificador solo puede mejorar.
5. **Cuándo se usa:**
   - **solo en mesas de 5 o 6 jugadores;**
   - con 3 o 4 jugadores el modelo de rivales tiene muy pocos datos y las simulaciones empeoraban el juego (−4,1 pts en mesas de 3),
     así que en esas mesas decide la v1.
6. **Seguridad de tiempo:**
   - las simulaciones se cortan a los 200 ms, con un tope duro de 250 ms;
   - si no alcanzan 150 simulaciones, decide la v1, porque con tan pocas no hay evidencia suficiente;
   - todo va dentro de `try/catch`; ante cualquier error, CALL si es gratis y FOLD si no.

## 4. Por qué este diseño (lo que probamos y descartamos)

| Enfoque | Resultado | Lección |
|---|---|---|
| Q-learning + UCB por self-play (recompensa = clasificar) | Sin campeón confirmado | Con 30 manos por mesa la señal es muy ruidosa y el espacio de estados es enorme. |
| Self-play con FI en el pool | Sin campeón confirmado | |
| CFR (MCCFR, 300k iteraciones) | Entre −8 y −12 pts contra la v1 | El equilibrio de Nash no explota a rivales débiles, y este torneo está lleno de ellos. |
| Reentrenamiento del compañero | Mejora contra FI y empeora contra el resto | Es sobreajuste a un rival. |
| **Rollouts sobre la v1** | **+2,5 a +3,5 pts en los 4 grupos** | Explota a cada rival según su perfil y planifica la mano completa. |

## 5. Cómo se demostró que es mejor (evaluación)

- **Comparación pareada:** todos los agentes juegan **las mismas mesas** (misma semilla, mismos rivales, mismo asiento). Así la
  diferencia no depende de la suerte de las cartas.
- **4 grupos de 1.000 mesas nuevas:** 2 con FI en la mesa y 2 sin FI.
- **Criterio estricto:**
  - ganar en **los 4 grupos**;
  - z combinado con **p < 0,01** y corrección de **Holm** (varias comparaciones a la vez);
  - **0** decisiones de más de 1000 ms.
- **Semillas nuevas para el veredicto:** el cambio "v1 en mesas de 3–4" se decidió con una medición, y el veredicto se tomó con otras
  semillas, para no elegir mirando el examen.

| % que clasifica | A con FI | B con FI | C sin FI | D sin FI |
|---|---|---|---|---|
| v1 | 76,4 | 74,0 | 78,6 | 80,2 |
| **jackpot (final)** | **78,9** | **76,6** | **82,1** | **83,0** |
| FI (compañero) | 72,8 | 74,6 | 76,4 | 76,6 |

| Comparación | z combinado | Veredicto |
|---|---|---|
| jackpot − v1 | 4,68 (p ≈ 1,4·10⁻⁶) | Mejor, demostrado |
| jackpot − FI | 7,29 | Mejor, demostrado |

- **Gana contra todos los tipos de rival:** Raiser +5,0, Random +5,4, Heuristic +4,0, Rival +3,0, FI +2,6 y Station +2,9.
- **Bench final:** contra 5 Heuristic clasifica el 70,5 %, y heads-up el 72 %. La decisión más lenta tardó 200 ms, con 0 timeouts.

## 6. Limitaciones (honestas)

- **Un torneo individual tiene mucha varianza:** son 2 o 3 mesas de 30 manos, y se puede perder por mala suerte. La ventaja se ve
  en promedio, sobre muchas mesas.
- **En formato torneo no se demostró ventaja:**
  - en 30 torneos simulados de 14 agentes (`run.cmd tournament`, semillas 3001–3030) jackpot llegó a la final 20 veces y fue
    campeón 5;
  - FI y MiAgente tuvieron cifras parecidas, y el jackpot del compañero llegó a la final 25 veces;
  - con unas 70 mesas por agente, el margen es de ±11 pts, así que esas diferencias no son significativas;
  - desde la ronda 2 las mesas son solo de sobrevivientes, más fuertes que el pool de la comparación pareada.
- **El modelo de rivales necesita manos:** en las primeras manos de cada mesa depende del *prior*.
- **La política de los rollouts es simple:** una política de simulación mejor, o un árbol MCTS completo (UCT), podría mejorar más,
  a cambio de más tiempo por decisión.
