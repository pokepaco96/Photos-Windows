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
| ONNX Runtime (`Microsoft.ML.OnnxRuntime`) | MIT | © Microsoft Corporation | https://github.com/microsoft/onnxruntime |

### Modelos que el usuario puede descargar (no se distribuyen con la app)
| Modelo | Licencia | Autoría | Fuente | Nota |
|---|---|---|---|---|
| YuNet `face_detection_yunet_2023mar.onnx` | MIT | © 2020 Shiqi Yu | https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet | Entrenado con WIDER FACE (anotaciones CC BY-NC-ND) |
| SFace `face_recognition_sface_2021dec.onnx` | Apache-2.0 | Yaoyao Zhong; conversión ONNX de Chengrui Wang (OpenCV) | https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface | Procedencia de datos de entrenamiento no indicada en la ficha |

Detalle y riesgos en [research/FACE_MODELS_REVIEW.md](research/FACE_MODELS_REVIEW.md).

## Datos cartográficos
| Datos | Licencia | Origen |
|---|---|---|
| Natural Earth 1:110m land (v4.1.0), 1:50m land (v4.1.0), 1:50m lakes (v5.0.0), 1:50m admin-0 boundary lines land (v5.1.0) | Dominio público | https://www.naturalearthdata.com — convertidos con `tools/basemap/build_basemap.py` (URL y SHA-256 de cada archivo en `src/PhotosWindows.Imaging/Maps/world.manifest.json`) |

La atribución no es obligatoria, pero la app muestra «Mapa: Natural Earth (dominio público)». Las fronteras reflejan la política *de facto* de Natural Earth.

Iconografía: la app usa la fuente del sistema **Segoe Fluent Icons** (incluida en Windows 11) y un icono de aplicación original. No se incluyen iconos, tipografías, sonidos ni recursos de Apple.

Solo para desarrollo (no se distribuyen): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), xunit.runner.visualstudio (Apache-2.0), Xunit.SkippableFact (MS-PL), SkiaSharp.NativeAssets.Linux.NoDependencies (MIT), FlaUI (MIT).

Imágenes de prueba (se descargan durante las pruebas, no se incluyen en el repositorio): retratos de la NASA S84-40242, S93-48446, S64-31845, S66-15621 y S92-42897, obras del Gobierno federal de EE. UU. en el dominio público (https://www.nasa.gov/nasa-brand-center/images-and-media/). Su uso no implica respaldo de la NASA ni de las personas retratadas.
