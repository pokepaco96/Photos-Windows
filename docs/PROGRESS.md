# Progreso por fases

Estados: `NOT STARTED` · `IN PROGRESS` · `BLOCKED` · `DONE` (solo con criterio de salida cumplido y evidencia de pruebas).

> **Cómo leer la evidencia.** Hay cinco niveles distintos y ninguno sustituye a otro:
> 1. **Linux (local y CI):** pruebas unitarias y de integración del núcleo.
> 2. **CI en Windows:** las mismas pruebas, más compilación, publicación y arranque de la app WinUI 3 x64.
> 3. **UI Automation en Windows Server 2025** (`windows-latest`): la app publicada se usa a través de su interfaz con datos sintéticos. Cifras por ejecución en [`QA_AUTOMATION.md`](QA_AUTOMATION.md).
> 4. **Validación manual en Windows 11:** **no realizada** (`QA_CHECKLIST.md`). Incluye archivos reales de cámaras y teléfonos, Narrador, DPI 150/200 % y contraste alto.
> 5. **Rendimiento:** medido en Linux (`PERFORMANCE.md`); **no medido** en Windows con bibliotecas grandes.
>
> Mientras falten los niveles 4 y 5, ninguna fase de producto pasa a `DONE`.

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
| 8 — Funciones inteligentes | IN PROGRESS | **Personas** (ADR-0010, fusionada en #5, `3831c7c`): modelos locales descargados por el usuario, consentimiento, esquema v2, correcciones, borrado total. CI #63/#64 y UI #31 (37/37) sobre `fbe6516`. **Lugares** (ADR-0011, fusionada en #6, `f3d7fe1`): mapa offline Natural Earth, grupos, lista, filtros, antimeridiano, recentrado de bandeja. CI #69/#70 y UI #32 (47/47) sobre `1b587c8`. **Recuerdos** (ADR-0012): reglas deterministas, explicación, pase, ocultar/excluir, esquema v3. fusionada en #7, `400dfa3`. CI #77/#78 y UI #36 (54/54) sobre `dd04124`. Pendiente en las tres: Windows 11 real, Narrador, DPI 200 %, contraste alto, biblioteca real; revisión jurídica de los modelos de rostros; rendimiento en Windows |
| 9 — Copias, rendimiento y hardening | IN PROGRESS | Copias verificadas y restauración (`BackupMaintenanceTests`); **copia de originales** en AÑO/MES (ADR-0014, fusionada en #9, `b00ed5c`, UI #48 60/60); **importación desde iPhone/dispositivos** por USB (ADR-0013, fusionada en #10, `4a7134c`, UI #49 65/65, con dispositivo simulado; sin probar con un iPhone real), verificación/reparación, rendimiento del catálogo (`PERFORMANCE.md`). Pendiente: accesibilidad con Narrador, pruebas de interrupción en Windows (QA #11–13, #16) |
| 10 — Distribución | IN PROGRESS | ADR-0009; CI publica la carpeta autocontenida `PhotosWindows-win-x64`. Plan de MSIX y firma en `DISTRIBUTION.md`. Pendiente: probar en Windows limpio, decidir licencia, obtener certificado, firma/MSIX |

Pruebas automáticas del núcleo (Linux, .NET SDK 10.0.403, `9030ba7`, fusionado como `4a7134c`): Domain 90/90, Imaging 38/38, Integration 188/188. Las mismas pruebas se ejecutan en CI en Linux y Windows (CI #106/#107 en verde).

UI automatizada (Windows Server 2025, `windows-latest`): ver la tabla de ejecuciones de `QA_AUTOMATION.md`. 18 errores de la app encontrados y corregidos con prueba de regresión en la fase de QA; los fallos posteriores fueron del arnés salvo el diseño de la bandeja de Lugares en ventanas pequeñas (corregido).
