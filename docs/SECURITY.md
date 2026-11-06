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

## Pendiente / limitaciones
- Los decodificadores nativos (Skia, WIC y sus extensiones) son código de terceros; se mitiga con límites y aislamiento por archivo, no con un proceso sandbox (evaluar en Fase 9).
- Condición de carrera validación→apertura: los archivos se abren una vez para firma y otra para hash/copia; un cambio intermedio se detecta porque el hash queda registrado y la verificación compara tamaños/hash.

## Notificar vulnerabilidades
Abre un *security advisory* privado en GitHub en lugar de un issue público.
