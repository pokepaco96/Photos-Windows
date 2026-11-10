# Rendimiento

## Catálogo (medido)

Prueba: `tests/PhotosWindows.Integration.Tests/PerformanceTests.cs` (`dotnet test --filter Category=Performance`).
Biblioteca sintética (sin archivos de imagen): mide la base de datos y la capa de consultas que usan la cuadrícula, la búsqueda y los álbumes inteligentes.
Entorno de la medición: contenedor Linux x64 de 4 vCPU, .NET 10.0.403, build Release, 2026-11-06. Segunda ejecución de cada consulta (en caliente).

| Operación | 10.000 elementos | 50.000 elementos |
|---|---|---|
| Sembrado + reconstrucción FTS | 1,0 s | 4,1 s |
| Cronología completa (todas las filas, por fecha) | 73 ms | 254 ms |
| Recuento | < 1 ms | 2 ms |
| Favoritos | 7 ms | 96 ms |
| Búsqueda «beach» (FTS) | 4 ms | 22 ms |
| Búsqueda «2019 video» (fecha + tipo) | < 1 ms | 2 ms |
| Búsqueda por nombre «IMG_0042» | 2 ms | 7 ms |
| Álbum inteligente (palabra clave + cámara + ubicación) | 7 ms | 86 ms |
| Consulta por área GPS | 8 ms | 94 ms |
| Estadísticas | 7 ms | 95 ms |
| Memoria por fila de cuadrícula (`AssetSummary`) | ~184 B | ~183 B |

Extrapolación: 100.000 elementos ≈ 18 MB de filas en memoria y ~0,5 s para cargar la cronología completa; los bitmaps no se cargan hasta que una celda es visible (virtualización), por lo que la memoria de imágenes depende del tamaño de la ventana, no del tamaño de la biblioteca.

Los límites de la prueba son techos generosos (1,5 s por cada 10.000 elementos) para detectar regresiones en CI.

## Personas: inferencia local (medido)
Prueba `End_to_end_groups_the_same_people_and_measures_speed`: 5 retratos de la NASA (~1024×1280), 6 rostros, decodificación a 1600 px + detección YuNet a dos escalas + descriptor SFace por rostro, 2 trabajadores, CPU.

| Entorno | Tiempo por foto (todo incluido) |
|---|---|
| CI `windows-latest`, 2 vCPU, 2026-11-07 | 164 ms |
| CI `ubuntu-latest`, 2 vCPU, 2026-11-07 | 281 ms |
| Contenedor Linux, 4 vCPU, 2026-11-07 | 140 ms |

Extrapolación lineal de lo medido, no una medida: 10.000 fotos ≈ 25–50 min con 2 vCPU. Falta medirlo en un PC con Windows 11 y con una biblioteca grande. La agrupación (en memoria, 128 valores por rostro) y la base de datos no son el cuello de botella con estas cantidades; falta medir 10.000 rostros sintéticos (pendiente).
## Lugares (medido)
Máquina: contenedor Linux de CI, Intel Xeon @ 2,10 GHz, 4 núcleos, .NET 10.0, Release salvo indicación. Método: `Stopwatch`, media de 5 renders tras uno de calentamiento; agrupación repetida 3 veces en un programa aparte.

| Operación | Resultado |
|---|---|
| Render del mapa 1600×1000, mundo (zoom 2,0) | 21,7 ms/frame (Debug, `MapRendererTests`) |
| Render del mapa 1600×1000, región (zoom 6,8) | 14,3 ms/frame (Debug) |
| Agrupar 100 000 puntos uniformes en el mundo, nivel 2 / 6 / 10 | ~15 / ~65 / ~150 ms (Release, peor caso: puntos dispersos) |
| Carga del mapa base | 367 KB comprimidos, 104 180 vértices |

No medido aún: tiempos de la página en Windows con 10k/50k/100k fotos reales (pendiente de la fase de rendimiento).

## Recuerdos (medido)
Máquina: contenedor Linux, Intel Xeon @ 2,10 GHz, 4 núcleos, .NET 10.0, compilación **Debug** (`MemoriesScaleTests`). Datos sintéticos con fechas entre 2012 y 2026, un 75 % con GPS.

| Elementos | Generar candidatos | Ordenar y deduplicar |
|---|---|---|
| 10 000 | 104 ms (248 candidatos) | < 1 ms (13 mostrados) |
| 100 000 | 751 ms (3 479 candidatos) | 5 ms (17 mostrados) |

No incluye la lectura del catálogo ni la carga de portadas, y no es una medida de la página en Windows.

## Pendiente de medir en Windows (QA manual)
- Páginas de Lugares y Recuerdos con 10k/50k/100k elementos reales (tiempo hasta el primer pintado, memoria).
- Importación de 1.000 JPEG reales (copiar y sin mover) en SSD y HDD.
- Generación de miniaturas por segundo con `MaxBackgroundWorkers` 1, 2 y 4.
- Desplazamiento de la cuadrícula con 50.000 elementos (fotogramas perdidos, memoria de trabajo).
- Apertura del visor (primer pintado desde miniatura grande y render a 3840 px).
