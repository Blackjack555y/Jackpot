# Instructivo de entregas — Proyecto Final Fundamentos

Documento para estudiantes. El kit de desarrollo que acompaña este instructivo es **AgentsPoker SDK v0.3.0** (carpeta `Kit_AgentsPoker_SDK/` o el archivo `AgentsPoker-SDK-v0.3.0.zip`). Las reglas del juego están en `REGLAS_POKER.md` / `.pdf` y, con más detalle técnico, en `Kit_AgentsPoker_SDK/REGLAS.md` y `GUIA_AGENTES.md`.

> **Fechas y medio de entrega:** **[a definir por el profesor]**  
> Marca estos campos cuando el profesor los anuncie. Hasta entonces no asumas una fecha ni un canal concretos.

---

## Resumen de lo que se entrega

| # | Nombre | Individual / grupo | Formato | Fecha | Medio |
|---|---|---|---|---|---|
| 1 | Informe de proyecto | Individual | PDF, máximo 5 páginas tamaño carta | **[a definir por el profesor]** | **[a definir por el profesor]** |
| 2 | Código del agente | Individual (un agente por estudiante) | Un archivo `.cs` listo para competir + registro del nombre | **[a definir por el profesor]** | **[a definir por el profesor]** |

---

## Entrega 1 — Informe de proyecto (individual)

- **Formato:** PDF, **máximo 5 páginas**, tamaño **carta** (Letter, 8.5 × 11 pulgadas).
- **Quién:** cada estudiante entrega su propio informe.
- **Fecha y medio:** **[a definir por el profesor]**.

El informe debe incluir **todas** las secciones siguientes, en este orden. Los títulos pueden adaptarse ligeramente, pero el contenido requerido no.

### 1. Resumen (Abstract)

150–250 palabras. Debe incluir, de forma condensada:

- el **propósito** del proyecto,
- la **metodología** usada,
- los **resultados principales**,
- la **conclusión**.

*Pista para este proyecto:* resume en un párrafo qué problema resuelves (un agente de Limit Hold'em que clasifique en mesas de 6), cómo lo construiste (heurística, Monte Carlo, ISMCTS, etc.), qué midiste con el kit (`bench` / laboratorio) y qué concluyes sobre su desempeño.

### 2. Introducción

- Tema del proyecto.
- Planteamiento del problema.
- Antecedentes.
- Justificación.

*Pista:* explica por qué un agente autónomo de póker es un buen vehículo para aplicar lo visto en el curso (toma de decisiones bajo incertidumbre, evaluación empírica, límites de tiempo).

### 3. Objetivos

- Objetivo **general**.
- Objetivos **específicos** (numerados, medibles cuando sea posible).

### 4. Marco teórico / Antecedentes

Conceptos necesarios para entender tu solución (reglas del Limit Hold'em, equity, pot odds, técnicas que uses — Monte Carlo, ISMCTS, redes, etc.). Cita fuentes cuando corresponda.

### 5. Metodología

Paso a paso: materiales, herramientas, instrumentos, tipo de diseño y procedimientos.

*Pista para este proyecto (no sustituye tu descripción):*

- Kit AgentsPoker SDK v0.3.0 (`run.cmd`, `entrenar.cmd`, plantilla `Agents/MiAgente.cs`).
- Compilación C# 9 / .NET Standard 2.1 (igual que la Arena del torneo).
- Cómo mediste: por ejemplo  
  `.\run.cmd bench --agent TuAgente --builtin Heuristic*5 --matches 200`  
  o el laboratorio `.\entrenar.cmd` / `.\entrenar.cmd 300`.  
  Reporta **porcentaje de victoria / clasificación**, **stack promedio**, **ms/decisión**, **timeouts** y el **margen de error (±)** que imprime el laboratorio: si dos versiones difieren menos que ese margen, aún no sabes cuál es mejor.
- Criterio de corte de tiempo: el torneo da **1000 ms** reales por decisión; en el bench conviene `ms máx` &lt; 300 ms.

### 6. Resultados

Tablas, gráficos o figuras **numeradas** (Tabla 1, Figura 1, …) con sus títulos. Incluye al menos una tabla de desempeño contra builtins o rivales de compañeros.

### 7. Discusión y análisis

Interpretación de los resultados, validación (o no) de tus hipótesis y comparación con la teoría o con baselines (p. ej. `Heuristic`, `Station`).

### 8. Conclusiones

Cumplimiento de los objetivos y aporte principal de tu trabajo. Sé concreto: qué logró el agente y qué no.

---

## Entrega 2 — Código del agente listo para competir

- **Qué entregas:** **un solo archivo** `TuNombreDeAgente.cs` con tu agente, según las reglas del kit (`Kit_AgentsPoker_SDK/LEEME.md` §8 y `GUIA_AGENTES.md` §3).
- **Fecha y medio:** **[a definir por el profesor]**.

### Requisitos técnicos (obligatorios)

- Lenguaje / plataforma: **C# 9**, **.NET Standard 2.1** (lo que usa el kit y la Arena).
- Una clase **pública**, no abstracta, con **constructor sin parámetros**, que implemente `IPokerAgent` (opcionalmente también `IHandObserver`).
- **Una sola clase agente por archivo**; clases auxiliares **dentro** de esa clase.
- Límite de tiempo en el torneo: **1000 ms reales** por `GetAction` y por `OnHandEnd`. Excepción o timeout → CHECK si es gratis, si no FOLD.
- Antes de entregar, verifica en el kit (desde `Kit_AgentsPoker_SDK/`):

```text
.\run.cmd check Agents\TuAgente.cs
.\run.cmd bench --agent TuAgente --builtin Heuristic*5 --matches 200
```

Checklist del kit: `OK`, `Timeouts 0`, `Errores 0`, `ms máx` &lt; 300, y sin APIs sospechosas.

### Nombre único e inmutable (obligatorio)

1. Elige un **nombre único** para tu agente (sin espacios ni tildes; letras, dígitos y `_`).
2. Ese nombre debe coincidir con:
   - el **nombre de la clase** C#, y
   - el **nombre del archivo** (ej. clase `NeuroBluff` → archivo `NeuroBluff.cs`).
3. **Registra ese nombre** en el formulario de la clase:  
   **https://forms.gle/2smLqbx2b69zWn7W9**
4. El nombre es **inmutable** después del registro: **no está permitido cambiarlo**. Si entregas un archivo o una clase con otro nombre, tu agente puede no identificarse correctamente en el torneo.

### Qué no se entrega

- El laboratorio (`Entrenamiento/`), notebooks, pesos sueltos o scripts de entrenamiento. Si entrenaste parámetros, **pégalos como constantes** dentro de tu clase (ver kit).
- DLLs ajenas, ONNX u otros formatos no contemplados en las reglas actuales del kit, salvo que el profesor lo autorice por escrito.

---

## Dónde empezar

1. Lee `CONTENIDO_CARPETA.md` (mapa de esta carpeta).
2. Extrae o abre `Kit_AgentsPoker_SDK/` y sigue `LEEME.md`.
3. Consulta `REGLAS_POKER.pdf` para las reglas de juego del torneo.
4. Trabaja tu agente; mide con `bench` / `entrenar.cmd`; escribe el informe (Entrega 1); registra el nombre y entrega el `.cs` (Entrega 2) en la fecha y por el medio **[a definir por el profesor]**.