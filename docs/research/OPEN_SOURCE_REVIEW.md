# Revisión de proyectos abiertos y dependencias

Revisión realizada el 2026-11-06. Las versiones se consultaron en la API pública de NuGet (`api.nuget.org/v3-flatcontainer`) y las licencias en los `.nuspec` de cada paquete.

## Proyectos estudiados como referencia (no se ha copiado código)

| Proyecto | Licencia | Qué aporta como referencia | Decisión |
|---|---|---|---|
| digiKam | GPL-2.0+ | Modelo de catálogo con álbumes físicos y etiquetas, sidecars XMP, verificación de base de datos | Referencia conceptual. GPL: no se incorpora código. |
| Immich | AGPL-3.0 | Línea temporal virtualizada, detección de duplicados, “live photos” por pares | Referencia de UX. AGPL: no se incorpora código. |
| darktable | GPL-3.0 | Edición no destructiva por pila de operaciones, orden fijo del pipeline | Inspira la receta ordenada. No se incorpora código. |
| ExifTool | Artistic/GPL | Referencia de etiquetas y casos raros de metadatos | Perl; no se distribuye. Útil solo como referencia manual. |
| MetadataExtractor .NET | Apache-2.0 | Lectura de EXIF/IPTC/XMP/GPS/QuickTime/HEIF en C# puro | **Adoptado** (lectura). |
| LibRaw | LGPL-2.1 / CDDL | Revelado RAW de alta calidad | Aplazado: requiere binario nativo y evaluar LGPL. En Windows se usa WIC + Raw Image Extension. |
| FFmpeg | LGPL/GPL según build | Decodificación universal de vídeo y fotogramas | Aplazado: tamaño y licencia. Se usa Media Foundation (Windows) para reproducción y fotogramas. |

## Dependencias de producción adoptadas

| Paquete | Versión | Licencia | Uso | Motivo |
|---|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.14 | MIT | Acceso a SQLite | Oficial de .NET; el bundle `e_sqlite3` incluye FTS5 y JSON1 (verificado por los tests de catálogo) |
| SQLitePCLRaw (transitiva) | 3.0.x | Apache-2.0 | Binario nativo de SQLite | Viene con Microsoft.Data.Sqlite |
| Dapper | 2.1.89 | Apache-2.0 | Mapeo de consultas | Micro-ORM pequeño; SQL explícito y auditable (FTS5, `json_each`) |
| MetadataExtractor | 2.9.3 | Apache-2.0 | Metadatos | C# puro, sin binarios nativos, ampliamente usado |
| XmpCore (transitiva) | 6.1.10.1 | BSD-3-Clause | Parser XMP | Dependencia de MetadataExtractor |
| SkiaSharp | 4.155.0 | MIT | Decodificar/escalar/codificar imágenes | Rápido, multiplataforma (permite tests en Linux), decodificación JPEG escalada |
| Microsoft.Extensions.* | 10.0.14 | MIT | DI y logging | Recomendado por el prompt; estándar .NET |
| Microsoft.WindowsAppSDK | 1.8.260921001 | Licencia de Microsoft (redistribuible) | WinUI 3 | Ver ADR-0002 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM (source generators) | Evita código repetitivo de `INotifyPropertyChanged`/comandos |

## Alternativas descartadas

- **SixLabors.ImageSharp**: licencia *Six Labors Split License* (comercial por encima de cierto umbral de ingresos). Descartada para no condicionar la distribución.
- **EF Core**: más pesado; las consultas FTS5 y `json_each` se expresan mejor en SQL directo.
- **Magick.NET**: binarios nativos grandes; su ventaja (formatos exóticos) no es necesaria en el MVP.

## Herramientas de desarrollo

xUnit 2.9.3 (Apache-2.0), Microsoft.NET.Test.Sdk 18.11.0 (MIT), xunit.runner.visualstudio 3.1.5 (Apache-2.0), SkiaSharp.NativeAssets.Linux.NoDependencies (MIT, solo tests no-Windows).
