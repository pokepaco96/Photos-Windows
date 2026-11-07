# Funciones inteligentes: auditoría y plan

## 1. Auditoría del estado real (2026-11-07)

| Elemento | Estado comprobado en GitHub |
|---|---|
| Rama por defecto | `claude/new-session-dxhwoz` en `4eddd87` |
| PR #2 (QA de interfaz) | **Abierto, sin fusionar**, sin revisiones ni comentarios, `mergeable: clean`. Rama `claude/ui-automation-qa` en `498d78a` |
| Issue #1 (QA end-to-end) | Abierta (se cerrará con el PR #2) |
| CI más reciente | #31 y #32 (PR) en `498d78a`: verdes |
| UI Automation más reciente | #22 en `f2f3315`: 28/28 (`498d78a` solo añade documentación y retira el disparador `push`) |
| Otras ramas | Ninguna |

**Rama de trabajo:** `claude/smart-people`, creada desde `498d78a` (cabeza del PR #2). Motivo: la rama por defecto no contiene todavía la suite de UI Automation ni las 18 correcciones del PR #2, y el trabajo nuevo debe reutilizarlas. No se modifica la rama del PR #2; el PR nuevo se abre **apilado** contra `claude/ui-automation-qa` y se redirigirá a la rama por defecto cuando el PR #2 se fusione.

**Línea base** (`498d78a`, Linux, .NET SDK 10.0.403): `dotnet restore`, build Release del núcleo (0 avisos, 0 errores), build de UI Automation, pruebas Domain 36/36, Imaging 30/30, Integration 87/87. En Windows: CI #32 (build, pruebas Windows, publish x64, smoke del ejecutable) y UI #22.

### Arquitectura actual
`Domain` ← `Application` (casos de uso y puertos) ← `Infrastructure` (SQLite/Dapper con migraciones, metadatos, ajustes, logs) / `Imaging` (SkiaSharp) / `Platform.Windows` (WIC, Media Foundation) ← `App` (WinUI 3, MVVM). Catálogo `catalog.db` por biblioteca con migraciones en `Migrations.cs` (esquema v1). Trabajo en segundo plano limitado por `AppSettings.MaxBackgroundWorkers`; `ActivityCenter` muestra progreso y cancelación.

### Ya implementado
Bibliotecas, importación reanudable (copiar / añadir sin mover), metadatos EXIF/IPTC/XMP/GPS/QuickTime, miniaturas, cuadrícula, visor, vídeo, edición no destructiva, exportación, álbumes y carpetas, álbumes inteligentes, palabras clave, favoritos, ocultos, papelera, búsqueda FTS, duplicados exactos y similares, copias verificadas y restauración, verificación/reparación. GPS: lectura, edición y borrado, `LibraryScope.WithLocation`, `AssetQuery.Area`.

### Pendiente
Personas y mascotas, Lugares (mapa), Recuerdos (ADR-0008). QA en Windows 11 real: Narrador, DPI 125/150/200 %, contraste alto, HEIC/AVIF/RAW reales, disco externo, importación interrumpida, instalador.

### Regresiones y defectos detectados en la auditoría
- `AssetQuery.Area` usa `longitude BETWEEN MinLon AND MaxLon`: un área que cruza el antimeridiano (MinLon > MaxLon) no devuelve nada. Se corregirá en la fase de Lugares con prueba.
- `docs/ROADMAP.md` está desactualizado (marca la app WinUI y la capa Windows como pendientes).
- `ui-automation.yml` solo existe en ramas que no son la por defecto: no se puede lanzar con `workflow_dispatch` hasta fusionar el PR #2. Las ramas nuevas necesitan un disparador `push` temporal para ejecutarla.

### Riesgos
- **Licencias de modelos:** InsightFace (RetinaFace, SCRFD, ArcFace) es solo para investigación no comercial; los pesos MIT/Apache de YuNet/SFace se entrenaron con datos de procedencia restringida o retirada. Ver [`research/FACE_MODELS_REVIEW.md`](research/FACE_MODELS_REVIEW.md).
- **Datos biométricos:** consentimiento explícito, borrado completo, nada en logs, copias de seguridad que lo declaren.
- **Mapas:** los servidores públicos de teselas de OpenStreetMap no son un CDN gratuito para apps; hace falta cartografía offline con licencia clara.
- **Entorno:** el runner de UI es Windows Server 2025; nada de lo que se ejecute allí cuenta como prueba en Windows 11.

## 2. Plan por hitos (un PR por hito, sin fusión automática)

| Hito | Rama | Contenido | Criterio de salida |
|---|---|---|---|
| 1. Personas | `claude/smart-people` | Investigación de modelos; ONNX Runtime en `PhotosWindows.MachineLearning`; descarga explícita y verificada de YuNet + SFace; migración v2 (rostros, personas, estado de análisis); análisis en segundo plano cancelable y reanudable; agrupación con corrección manual (nombrar, renombrar, fusionar, separar, ignorar, excluir foto); sección «Personas» solo con modelo instalado y función activada; borrado total de datos derivados; ES/EN; pruebas unitarias, de integración (inferencia real en CI) y UI Automation | Función real, opt-in, local, con licencias documentadas, UX completa, borrado y regresión cubiertos |
| 1b. Mascotas | `claude/smart-pets` | Detección de perros y gatos con YOLOX-s (descarga explícita); agrupación por especie (sin identidad individual) | Igual que el hito 1 |
| 2. Lugares | `claude/smart-places` | Revisión de mapas offline; agrupación de coordenadas por zoom; vista de lista completa sin mapa; mapa offline con paquete regional descargable si la licencia lo permite; corrección del antimeridiano | Lista completa o mapa offline funcional, sin enviar coordenadas |
| 3. Recuerdos | `claude/smart-memories` | Motor determinista (en este día, viajes, eventos, temporadas, resumen anual) con explicación; presentación; guardar como álbum | Recuerdos explicables, locales y editables |
| 4. QA Windows 11 | `claude/qa-hardening-windows11` | Importación interrumpida y reanudada, ruta no disponible, decodificación fallida, biblioteca grande, sin red durante funciones inteligentes; procedimientos manuales Windows 11 | Escenarios automatizables en la suite; manuales documentados como pendientes |
| 5. Rendimiento | en cada hito | Medidas reales (consultas, inferencia por imagen, agrupación, mapa, recuerdos) | Línea base documentada en `PERFORMANCE.md` |

Orden: Personas primero (prioridad de la misión), salvo bloqueo legal. La investigación no encontró bloqueo para la vía «descarga explícita del usuario».
