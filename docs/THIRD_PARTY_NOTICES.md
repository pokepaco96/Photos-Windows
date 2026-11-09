# Avisos de terceros

Photos Windows incluye o usa los siguientes componentes. Los textos completos de las licencias están en los paquetes NuGet correspondientes y en los enlaces indicados.

| Componente | Licencia | Copyright | Enlace |
|---|---|---|---|
| .NET runtime y Microsoft.Extensions.* | MIT | © .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| Microsoft.Data.Sqlite | MIT | © .NET Foundation | https://github.com/dotnet/efcore |
| SQLitePCLRaw | Apache-2.0 | © Zumero LLC / Eric Sink | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Dominio público | — | https://www.sqlite.org/copyright.html |
| Dapper | Apache-2.0 | © Stack Exchange, Inc. | https://github.com/DapperLib/Dapper |
| MetadataExtractor | Apache-2.0 | © Drew Noakes | https://github.com/drewnoakes/metadata-extractor-dotnet |
| XmpCore | BSD-3-Clause | © Adobe Systems Inc. / Drew Noakes | https://github.com/drewnoakes/xmp-core-dotnet |
| SkiaSharp | MIT | © Microsoft Corporation / .NET Foundation | https://github.com/mono/SkiaSharp |
| Skia | BSD-3-Clause | © Google LLC | https://skia.org |
| Windows App SDK / WinUI 3 | Licencia de Microsoft (redistribuible) | © Microsoft Corporation | https://github.com/microsoft/WindowsAppSDK |
| CommunityToolkit.Mvvm | MIT | © .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |

## Datos cartográficos
| Datos | Licencia | Origen |
|---|---|---|
| Natural Earth 1:110m land (v4.1.0), 1:50m land (v4.1.0), 1:50m lakes (v5.0.0), 1:50m admin-0 boundary lines land (v5.1.0) | Dominio público | https://www.naturalearthdata.com — convertidos con `tools/basemap/build_basemap.py` (URL y SHA-256 de cada archivo en `src/PhotosWindows.Imaging/Maps/world.manifest.json`) |

La atribución no es obligatoria, pero la app muestra «Mapa: Natural Earth (dominio público)». Las fronteras reflejan la política *de facto* de Natural Earth.

Iconografía: la app usa la fuente del sistema **Segoe Fluent Icons** (incluida en Windows 11) y un icono de aplicación original. No se incluyen iconos, tipografías, sonidos ni recursos de Apple.

Solo para desarrollo (no se distribuyen): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), xunit.runner.visualstudio (Apache-2.0), SkiaSharp.NativeAssets.Linux.NoDependencies (MIT).
