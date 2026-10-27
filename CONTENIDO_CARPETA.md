# Contenido de esta carpeta — Proyecto Final Fundamentos

Carpeta **para estudiantes**. No hace falta Unity ni el repositorio completo del curso: aquí está lo necesario para desarrollar, medir y entregar el agente, más las reglas del juego y el instructivo de entregas.

## Mapa

```
Proyecto Final Fundamentos/
├── CONTENIDO_CARPETA.md          ← este archivo (mapa)
├── README.txt                    ← misma orientación, texto plano
├── INSTRUCTIVO_ENTREGAS.md       ← entregas 1 y 2 (fechas/medio: [a definir por el profesor])
├── INSTRUCTIVO_ENTREGAS.pdf      ← misma información, tamaño carta
├── REGLAS_POKER.md               ← reglas del juego en español claro
├── REGLAS_POKER.pdf
├── PREGUNTAS_FRECUENTES.md       ← instalación, comandos, errores, nombre, entrega
├── PREGUNTAS_FRECUENTES.pdf
├── AgentsPoker-SDK-v0.3.0.zip    ← kit comprimido (copia de seguridad / redistribución)
└── Kit_AgentsPoker_SDK/          ← kit ya extraído, listo para usar
    ├── LEEME.md                  ← EMPIEZA AQUÍ (instalación y comandos)
    ├── GUIA_AGENTES.md           ← API, técnicas, formato del .cs
    ├── REGLAS.md                 ← reglas exactas del motor
    ├── run.cmd / run.sh          ← banco de pruebas (list, match, bench, tournament)
    ├── entrenar.cmd / .sh        ← laboratorio de medición / entrenamiento
    ├── Agents/                   ← pon aquí TuAgente.cs (plantilla: MiAgente.cs)
    ├── Entrenamiento/            ← laboratorio editable (no se entrega)
    ├── tools/                    ← workbench (código del banco)
    └── lib/                      ← AgentsPoker.Core.dll
```

## Por dónde empezar

1. Abre **`Kit_AgentsPoker_SDK/LEEME.md`** e instala .NET 8 SDK si aún no lo tienes.
2. En una terminal, dentro de `Kit_AgentsPoker_SDK/`:
   - Windows: `.\run.cmd list`
   - macOS / Linux: `bash run.sh list`
3. Copia `Agents/MiAgente.cs` → `Agents/<TuNombre>.cs`, cambia el nombre de la clase y mejora el agente.
4. Lee **`REGLAS_POKER.pdf`** (o `.md`) para las reglas del torneo.
5. Si algo falla, consulta **`PREGUNTAS_FRECUENTES.pdf`**.
6. Lee **`INSTRUCTIVO_ENTREGAS.pdf`** para el informe (Entrega 1) y el código + registro del nombre (Entrega 2).
7. Registra el nombre de tu agente en: https://forms.gle/2smLqbx2b69zWn7W9  
   (debe coincidir con clase y archivo; **no se puede cambiar** después).

## Notas

- Versión del kit: **AgentsPoker SDK v0.3.0**.
- Fechas y medio de entrega del curso: ver placeholders **[a definir por el profesor]** en el instructivo.
- Si el ZIP y la carpeta `Kit_AgentsPoker_SDK/` divergen, confía en la carpeta extraída que ya verificamos, o vuelve a descomprimir el ZIP.