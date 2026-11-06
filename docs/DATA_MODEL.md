# Modelo de datos (esquema v1)

Fuente de verdad: `src/PhotosWindows.Infrastructure/Persistence/Migrations.cs`.

| Tabla | Propósito | Notas |
|---|---|---|
| `library_info` | Clave/valor de la biblioteca | Reservado para ajustes por biblioteca |
| `schema_migrations` | Historial de migraciones | + `PRAGMA user_version` |
| `assets` | Elemento lógico (foto/vídeo) | Fecha `capture_local` + `capture_offset_min` + `date_source`; dimensiones y `orientation` del original; cámara/objetivo/exposición; GPS; título, descripción, valoración; `is_favorite`, `is_hidden`, `deleted_utc` (papelera); `edit_version` (0 = sin editar); `has_live_video`, `has_raw_pair`; `thumb_state`; `phash`; `original_available` (desnormalizado para la cuadrícula) |
| `asset_files` | Archivos físicos | `role` (Original, RawPair, LiveVideo, Sidecar), `storage_mode` (Referenced/Managed), `stored_path` (absoluta o relativa a la biblioteca), `stored_path_key` único (mayúsculas, como Windows), tamaño, `sha256`, mtime, disponibilidad |
| `keywords`, `asset_keywords` | Palabras clave | Únicas sin distinguir mayúsculas; renombrar a un nombre existente fusiona |
| `album_folders` | Carpetas anidadas | Borrar una carpeta mueve su contenido al padre |
| `albums` | Álbumes manuales e inteligentes | `smart_json` con `SmartAlbumDefinition` |
| `album_assets` | Pertenencia y orden manual | Nunca duplica archivos |
| `edit_recipes` | Receta actual por asset | JSON de `EditRecipe` normalizado |
| `edit_history` | Historial de recetas | Versión creciente; permite volver atrás |
| `import_jobs`, `import_items` | Trabajos de importación | Un ítem por archivo, persistido antes de procesar → reanudación |
| `duplicate_dismissals` | Pares descartados como «no duplicado» | `asset_a < asset_b` |
| `assets_fts` | FTS5 | nombre, título, descripción, palabras clave, cámara, objetivo, álbumes, formato |

## Relación lógica ↔ física
Un `Asset` tiene exactamente un archivo `Original` y opcionalmente `RawPair` (RAW del mismo disparo), `LiveVideo` (clip corto) y `Sidecar` (XMP). Las ediciones no generan archivos: son recetas. Las miniaturas no están en la base de datos: su ruta es determinista.

## Fechas
`capture_local` es la hora de pared del lugar de captura (así se agrupa por día). Si hay zona (`OffsetTimeOriginal`, inferida por GPS, o UTC de QuickTime convertido a la zona del equipo) se guarda en `capture_offset_min`. `date_source` indica el origen (EXIF original, digitalizado, QuickTime, XMP, nombre de archivo, fecha de modificación, manual).
