# Progreso por fases

Estados: `NOT STARTED` · `IN PROGRESS` · `BLOCKED` · `DONE` (solo con criterio de salida cumplido y evidencia de pruebas).

| Fase | Estado | Evidencia / notas |
|---|---|---|
| 0 — Descubrimiento y especificación | DONE | `PRODUCT_SPEC.md`, `ARCHITECTURE.md`, `DATA_MODEL.md`, ADR 0001–0008, `research/REA_FEASIBILITY.md`, `research/OPEN_SOURCE_REVIEW.md` |
| 1 — Esqueleto ejecutable | IN PROGRESS | Solución, DI, logging, SQLite y migración inicial con pruebas. Pendiente: proyecto WinUI 3 compilando en CI y verificación manual de arranque |
| 2 — Biblioteca e importación | IN PROGRESS | Motor completo con pruebas de integración (`ImportTests`, `LibraryTests`). Falta la UI de importación |
| 3 — Cuadrícula, cronología y visor | NOT STARTED | Consultas por ámbito/orden/agrupación probadas; UI pendiente |
| 4 — Organización | IN PROGRESS | Servicios probados (`OrganizationTests`); UI pendiente |
| 5 — Búsqueda y metadatos | IN PROGRESS | Servicios probados (`SearchTests`); UI pendiente |
| 6 — Editor no destructivo | IN PROGRESS | Pipeline y persistencia probados (`RendererTests`, `EditExportTests`); UI pendiente |
| 7 — Duplicados avanzados, RAW y formatos | IN PROGRESS | dHash y grupos similares probados. WIC para HEIF/RAW pendiente |
| 8 — Funciones inteligentes | NOT STARTED | ADR-0008 |
| 9 — Copias, rendimiento y hardening | IN PROGRESS | Copias/restauración/verificación probadas (`BackupMaintenanceTests`). Rendimiento y accesibilidad pendientes |
| 10 — Distribución | NOT STARTED | |

Última ejecución local de pruebas (Linux, .NET SDK 10.0.403): Domain 23/23, Imaging 30/30, Integration 85/85.
