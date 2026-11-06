# Arquitectura

```
┌──────────────────────── PhotosWindows.App (WinUI 3, MVVM) ────────────────────────┐
│ Views (XAML)  ←binding→  ViewModels (CommunityToolkit.Mvvm)  →  Application services │
└────────────────────────────────────────────┬───────────────────────────────────────┘
                                             │ DI (Microsoft.Extensions.DependencyInjection)
┌──────────────── PhotosWindows.Application (net10.0) ────────────────┐   ┌── PhotosWindows.Platform.Windows ──┐
│ LibraryManager · ImportService · ThumbnailService · Organization    │   │ WicRasterDecoder (HEIF/AVIF/TIFF/RAW)│
│ SearchService · EditingService/EditSession · ExportService          │◄──│ WindowsVideoFrameExtractor          │
│ DuplicateService · BackupService · LibraryMaintenanceService        │   └─────────────────────────────────────┘
│ Abstractions (ports): repositories, IImageRenderer, IRasterDecoder,  │
│ IMetadataReader, ICatalogStore, IClock, IFileDeleter, ISettingsStore │
└───────────────▲─────────────────────────────────────▲───────────────┘
                │                                     │
┌───────────────┴──── Infrastructure ────────┐  ┌─────┴────────── Imaging ─────────────┐
│ SQLite catalog (Dapper, FTS5, migrations)  │  │ SkiaImageRenderer (geometry 1 pass)  │
│ MetadataExtractorReader · JSON settings    │  │ ToneProcessor · PerceptualHasher     │
│ RollingFileLogger · FileDeleter (Recycle)  │  └──────────────────────────────────────┘
└────────────────────────────────────────────┘
                ▲
        PhotosWindows.Domain (entidades, reglas, sin dependencias)
```

## Reglas
- Dependencias hacia dentro: `Domain` ← `Application` ← (`Infrastructure`, `Imaging`, `Platform.Windows`) ← `App`.
- Las vistas no contienen lógica de negocio; el *code-behind* solo hace interacción visual (p. ej. arrastrar el recorte).
- Toda E/S y procesamiento pesado se ejecuta fuera del hilo de UI (`Task.Run` solo para trabajo de CPU: decodificar/renderizar).
- Concurrencia limitada por `AppSettings.MaxBackgroundWorkers` (importación y miniaturas).
- Cancelación con `CancellationToken` en importación, miniaturas, exportación, copias y verificación.
- Errores esperables (archivo corrupto, disco desconectado, permisos) se capturan por tipo y se informan; nunca `catch {}` silencioso.

## Flujos clave
**Importación** (`ImportService`): escaneo (omite ocultos/sistema, enlaces, caché y copias de la biblioteca) → trabajo persistido con un `import_item` por archivo → planificación por foto (`ImportPlanner`: primario + RAW + clip + XMP) → por unidad, en paralelo limitado: firma real, ¿ya catalogado?, SHA-256 (en modo copia se calcula mientras se copia a `*.pwpartial`), ¿duplicado?, metadatos, renombrado atómico sin sobrescribir, inserción transaccional (asset+archivos+palabras clave+FTS). Si la inserción falla se elimina la copia; si la app se cierra entre copia y catálogo, la verificación detecta la copia huérfana y permite adoptarla. Trabajos `Running` al abrir → `Interrupted` → reanudables.

**Render** (`SkiaImageRenderer`): decodificar (escalado si es posible) → matriz única (EXIF → giros → volteos → enderezado+zoom → recorte → escala) → `ToneProcessor` → codificar. Miniaturas en `cache/thumbs/xx/<uid>_<tamaño>_v<versiónEdición>.jpg`: editar invalida por nombre, sin borrar la caché completa.

**Edición**: `EditSession` mantiene la receta actual y el historial deshacer/rehacer (agrupando arrastres de un mismo control). La vista previa decodifica una vez a resolución de pantalla y solo re-aplica la receta. Guardar escribe la receta en `edit_recipes` y añade una entrada a `edit_history`; el original nunca se toca.

## Rendimiento
- La cuadrícula carga filas ligeras (`AssetSummary`, ~100 B) para toda la consulta; los bitmaps se piden por contenedor visible y se cancelan al reciclarse.
- Índices por fecha, tipo, visibilidad, favoritos, miniaturas pendientes, ubicación, cámara, hash.
- Decodificación JPEG escalada para miniaturas; caché en disco; dHash a 64 px.
- Medidas en `docs/PERFORMANCE.md` (se completan en la Fase 9).

## Registro
Logs locales en `%LOCALAPPDATA%\PhotosWindows\logs` (7 días, 10 MB/día). Solo nombres de archivo, no rutas completas, salvo errores de verificación que el usuario solicita.
