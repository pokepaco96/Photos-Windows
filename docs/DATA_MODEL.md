# Modelo de datos (esquema v3)

> Lugares no añade tablas: usa `assets.latitude/longitude` (índice `ix_assets_location`). Las consultas nuevas son `QueryGeoPointsAsync` (id, lat, lon, fecha) y `SetLocationAsync`; el filtro `AssetQuery.Ids` usa `json_each`.

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
| `people` *(v2)* | Grupos de rostros | `name` nulo = «Persona sin identificar» (solo el usuario pone nombres); `cover_face_id` |
| `faces` *(v2)* | Rostros detectados | Caja `x, y, w, h` normalizada (0..1) a la foto con la orientación EXIF aplicada; `score`; `embedding` BLOB (128 `float32`, norma 1); `model_version`; `person_id` (nulo = sin grupo); `review` 0 sugerido / 1 confirmado / 2 ignorado. Se borra con el asset (`ON DELETE CASCADE`) |
| `face_analysis` *(v2)* | Estado por foto | `model_version`, `state` 1 analizada / 2 fallida, `face_count`; sin fila (o con otra versión) = pendiente |
| `face_exclusions` *(v2)* | Fotos excluidas de Personas | Decisión del usuario; se conserva al borrar los datos faciales |
| `memory_hidden` *(v3)* | Recuerdos que el usuario ocultó | Clave estable del recuerdo (`onthisday:2021-11-10`, `trip:20230801-20230804`…), tipo y fechas; no guarda fotos |
| `memory_exclusions` *(v3)* | Exclusiones de Recuerdos | O bien un tipo (`kind`, único), o bien un intervalo de días (`from_local`–`to_local`); restricción `CHECK` |

La migración 3 es aditiva y se puede deshacer borrando esas dos tablas (ver comentario en `Migrations.cs` y la prueba `Catalog_from_version_2_gains_the_memory_tables_and_the_migration_can_be_undone`).

## Relación lógica ↔ física
Un `Asset` tiene exactamente un archivo `Original` y opcionalmente `RawPair` (RAW del mismo disparo), `LiveVideo` (clip corto) y `Sidecar` (XMP). Las ediciones no generan archivos: son recetas. Las miniaturas no están en la base de datos: su ruta es determinista.

## Migraciones
Incrementales, una transacción cada una, con copia previa del catálogo (`LibraryManager`). v2 solo añade tablas: un catálogo v1 se abre y migra sin perder datos (prueba `Catalog_from_version_1_is_migrated_with_its_data`). **Bajar de versión no es seguro**: una app con esquema v1 rechaza abrir un catálogo v2 (mensaje claro, sin modificarlo).

## Fechas
`capture_local` es la hora de pared del lugar de captura (así se agrupa por día). Si hay zona (`OffsetTimeOriginal`, inferida por GPS, o UTC de QuickTime convertido a la zona del equipo) se guarda en `capture_offset_min`. `date_source` indica el origen (EXIF original, digitalizado, QuickTime, XMP, nombre de archivo, fecha de modificación, manual).
