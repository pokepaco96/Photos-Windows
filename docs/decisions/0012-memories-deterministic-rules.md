# ADR-0012 · Recuerdos: reglas deterministas, explicables y solo con datos del catálogo

Reemplaza la parte de Recuerdos de [ADR-0008](0008-smart-features-deferred.md).

**Contexto.** La especificación pide recuerdos con reglas transparentes. La app es local: sin nube, sin IA remota y sin geocodificación.

**Decisión.**
- Generación pura en el dominio (`MemoryCandidateGenerator`) a partir de fecha, GPS, favoritas/editadas y, con Personas activada, grupos con nombre. Tipos: tal día como hoy, evento, viaje, mes, estación, año y persona. Reglas y umbrales en [`MEMORIES.md`](../MEMORIES.md).
- Cada recuerdo lleva sus hechos (`MemoryExplanation`); la interfaz explica por qué aparece sin inventar nombres de lugares, eventos ni personas.
- Ranking determinista (`MemoryRanker`) con máximos por tipo, eliminación de solapamientos y rotación semanal por hash estable.
- Solo se persisten **decisiones del usuario** (esquema v3, aditivo: `memory_hidden`, `memory_exclusions`). Los recuerdos se recalculan bajo demanda y se guardan en memoria por biblioteca y día.
- Personas se usa únicamente si está activada en la biblioteca; Recuerdos no activa ni ejecuta análisis facial.
- Pase de diapositivas propio (`SlideshowPage`) con estado comprobable (`SlideshowState`).

**Alternativas.** Clasificación de escenas u objetos con modelos locales (descartada: añade modelos con problemas de licencia de datos y no es explicable); guardar los recuerdos generados en disco (innecesario: generar 100 000 elementos tarda menos de un segundo, ver `PERFORMANCE.md`).

**Consecuencias.** Los títulos son prudentes (fechas y coordenadas aproximadas). Los viajes solo se sugieren cuando hay evidencia de una zona habitual. Deshacer la migración 3 es borrar dos tablas que nada más referencia.
