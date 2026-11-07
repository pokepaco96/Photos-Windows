# Seguridad

## Modelo de amenazas (resumen)
La app procesa archivos no confiables (tarjetas SD, descargas, discos ajenos). Los riesgos principales son: parsers explotables, archivos disfrazados, bombas de descompresión, recorridos de ruta en copias/restauraciones, borrados accidentales y fuga de datos personales.

## Medidas implementadas
| Riesgo | Medida | Dónde |
|---|---|---|
| Extensión falsa | Firma (magic bytes) comprobada contra la extensión antes de importar | `MediaFormats.Sniff/SignatureMatchesExtension`, `ImportService` |
| Bomba de descompresión | Límite 200 MP y 2 GiB por imagen; decodificación escalada | `DecodeLimits`, `SkiaImageRenderer` |
| Archivos corruptos/truncados | Excepciones tipadas por archivo; el trabajo continúa; JPEG truncado se muestra parcialmente | `ImportService`, `ThumbnailService` |
| Inyección SQL / FTS | Parámetros en todas las consultas; texto de búsqueda reducido a términos entre comillas | `AssetQuerySql` |
| Recorrido de ruta | Rutas del manifiesto de copia comprobadas con `PathSafety.IsUnder`; nombres saneados | `BackupService`, `PathSafety` |
| Enlaces simbólicos/uniones | El escaneo no sigue `ReparsePoint` ni carpetas ocultas/de sistema | `ImportService.Scan` |
| Sobrescritura accidental | `FileMode.CreateNew` y `File.Move(..., overwrite:false)`; nombres únicos `(n)` | Importación, exportación, copias |
| Borrado accidental | Plan previo + confirmación; solo desde la papelera; referenciados conservados por defecto; Papelera de reciclaje | ADR-0006 |
| XML externo (XXE) en XMP | Sidecars limitados a 1 MB; parser XmpCore | `MetadataExtractorReader` |
| Ejecución de contenido | Nunca se ejecutan ni abren con otras apps los archivos importados | — |
| Procesos externos | No se invocan procesos externos en el MVP | — |
| Dependencias vulnerables | `NuGetAudit` (all) y `dotnet list package --vulnerable` en CI | `Directory.Build.props`, `ci.yml` |
| Modelo ML manipulado o sustituido | URL fijada a un commit; tamaño y SHA-256 fijados en el código y comprobados tras descargar o copiar y al cargar (caché por tamaño/fecha); un archivo distinto nunca se carga; descarga a `.partial` y movimiento atómico; tope de tamaño durante la descarga | `FaceModelCatalog`, `FaceModelManager` |
| Fuga de datos biométricos | Opt-in con consentimiento; nada sale del equipo; logs sin datos faciales; copias sin datos faciales por defecto; borrado físico (`secure_delete` + `VACUUM` + WAL truncado) | `PeopleService`, `PeopleRepository`, `BackupService` |

## Personas: modelo de amenazas de los datos biométricos
- **Qué se guarda:** posición del rostro y un descriptor de 128 números por rostro, en `catalog.db` (carpeta de la biblioteca), y miniaturas de rostros en `cache/faces`.
- **Protección:** la de los archivos del usuario (permisos de su cuenta de Windows). **No se cifran**: una clave guardada junto al catálogo no protegería frente a quien ya puede leer la biblioteca, y DPAPI ataría la biblioteca a una cuenta y rompería copias y restauraciones. Se documenta en lugar de prometer una seguridad que no existe. Para proteger la biblioteca, cifrado de disco (BitLocker) o carpeta en una unidad cifrada.
- **Exposición:** quien pueda leer la biblioteca puede leer descriptores; un descriptor no permite reconstruir la cara, pero permite comparar rostros con otros calculados con el mismo modelo. Por eso las copias no los incluyen por defecto y existe el borrado total.
- **Fuera de alcance:** identificación de personas reales, búsqueda en internet, atributos sensibles.

## Pendiente / limitaciones
- Los decodificadores nativos (Skia, WIC y sus extensiones) son código de terceros; se mitiga con límites y aislamiento por archivo, no con un proceso sandbox (evaluar en Fase 9).
- Condición de carrera validación→apertura: los archivos se abren una vez para firma y otra para hash/copia; un cambio intermedio se detecta porque el hash queda registrado y la verificación compara tamaños/hash.

## Notificar vulnerabilidades
Abre un *security advisory* privado en GitHub en lugar de un issue público.
