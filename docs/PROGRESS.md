# Progreso por fases

Estados: `NOT STARTED` · `IN PROGRESS` · `BLOCKED` · `DONE` (solo con criterio de salida cumplido y evidencia de pruebas).

> **Validación de la interfaz:** el desarrollo se hace en un contenedor Linux sin escritorio. Desde la fase de QA automatizado, la app publicada **se usa de verdad a través de su interfaz** en el escritorio interactivo de `windows-latest` (Windows Server 2025) con la suite `tests/PhotosWindows.UIAutomation.Tests` (FlaUI/UIA): en la última ejecución (#17) 26 de 28 escenarios superados, 2 fallidos, 0 bloqueados. Ver [`QA_AUTOMATION.md`](QA_AUTOMATION.md). Sigue pendiente la prueba manual en Windows 11 con archivos reales de cámaras y teléfonos, Narrador, escalado 200 % y contraste alto (`QA_CHECKLIST.md`), por eso las fases siguen en `IN PROGRESS`.

| Fase | Estado | Evidencia / pendiente |
|---|---|---|
| 0 — Descubrimiento y especificación | DONE | `PRODUCT_SPEC.md`, `ARCHITECTURE.md`, `DATA_MODEL.md`, ADR 0001–0009, `research/REA_FEASIBILITY.md`, `research/OPEN_SOURCE_REVIEW.md` |
| 1 — Esqueleto ejecutable | IN PROGRESS | Solución, DI, logging local, SQLite + migraciones (`LibraryTests`), ventana con navegación, temas, recursos en-US/es-ES, CI Windows. La app publicada arranca y se usa en Windows Server 2025 (UI S01–S03, smoke de arranque en CI). Pendiente: Windows 11 |
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

UI automatizada (Windows Server 2025, `windows-latest`): en la última ejecución (#17) 26 de 28 escenarios superados, 2 fallidos, 0 bloqueados. 18 errores de la app encontrados y corregidos con prueba de regresión (lista en `QA_AUTOMATION.md`).
