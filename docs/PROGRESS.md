# Progreso por fases

Estados: `NOT STARTED` · `IN PROGRESS` · `BLOCKED` · `DONE` (solo con criterio de salida cumplido y evidencia de pruebas).

> **Limitación del entorno de desarrollo:** el trabajo se ha hecho en un contenedor Linux sin escritorio Windows. El núcleo se prueba en Linux y en `windows-latest` (CI); la app WinUI 3 se compila y publica en CI, pero **ningún flujo de la interfaz se ha ejecutado todavía de forma interactiva**. Por eso las fases cuyo criterio de salida exige usar la app siguen en `IN PROGRESS` hasta completar `QA_CHECKLIST.md` en un Windows real.

| Fase | Estado | Evidencia / pendiente |
|---|---|---|
| 0 — Descubrimiento y especificación | DONE | `PRODUCT_SPEC.md`, `ARCHITECTURE.md`, `DATA_MODEL.md`, ADR 0001–0009, `research/REA_FEASIBILITY.md`, `research/OPEN_SOURCE_REVIEW.md` |
| 1 — Esqueleto ejecutable | IN PROGRESS | Solución, DI, logging local, SQLite + migraciones (`LibraryTests`), ventana con navegación, temas, recursos en-US/es-ES, CI Windows. Pendiente: comprobar arranque en Windows 11 (QA #1–2) |
| 2 — Biblioteca e importación | IN PROGRESS | Motor completo y probado (`ImportTests`, `LibraryTests`): añadir/copiar, SHA-256, duplicados, RAW+JPEG, clips, XMP, cancelación/reanudación, archivos corruptos. UI de importación implementada. Pendiente: QA #3–6 |
| 3 — Cuadrícula, cronología y visor | IN PROGRESS | Consultas por ámbito/orden (`OrganizationTests`, `PerformanceTests` 50k), cuadrícula virtualizada agrupada, visor con zoom/vídeo/inspector implementados. Pendiente: QA #7 |
| 4 — Organización | IN PROGRESS | Servicios probados (`OrganizationTests`: álbumes, carpetas, inteligentes, palabras clave, ocultos, papelera, borrado con plan). UI implementada. Pendiente: QA #9, #14 |
| 5 — Búsqueda y metadatos | IN PROGRESS | `SearchTests` (FTS, fechas, tipo, cámara, álbum, entradas hostiles), edición de título/descripción/valoración/ubicación. Pendiente: QA #10 |
| 6 — Editor no destructivo | IN PROGRESS | Pipeline determinista (`RendererTests`), persistencia y reapertura (`EditExportTests`), exportación con metadatos. UI implementada. Pendiente: QA #8 |
| 7 — Duplicados avanzados, RAW y formatos | IN PROGRESS | dHash y agrupación (`PerceptualHashTests`, `BackupMaintenanceTests`); WIC para HEIF/AVIF/TIFF/RAW compilado. Pendiente: probar con archivos reales de cámaras/teléfonos en Windows |
| 8 — Funciones inteligentes | NOT STARTED | ADR-0008: personas, mapa y recuerdos aplazados; GPS ya se lee, muestra, edita y filtra |
| 9 — Copias, rendimiento y hardening | IN PROGRESS | Copias verificadas y restauración (`BackupMaintenanceTests`), verificación/reparación, rendimiento del catálogo (`PERFORMANCE.md`). Pendiente: accesibilidad con Narrador, pruebas de interrupción en Windows (QA #11–13, #16) |
| 10 — Distribución | IN PROGRESS | ADR-0009; CI publica la carpeta autocontenida `PhotosWindows-win-x64`. Pendiente: probar en Windows limpio, decidir licencia, firma/MSIX |

Pruebas automáticas (última ejecución local, Linux, .NET SDK 10.0.403): Domain 36/36, Imaging 30/30, Integration 87/87 (incluye 2 de rendimiento).
