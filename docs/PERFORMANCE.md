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

## Importación (medido en Linux; Windows 11 pendiente)

Banco reproducible: `tools/import-bench` (genera un conjunto sintético sin datos personales e importa con el `ImportService` real; informe JSON con fases, MB/s, archivos/s, CPU, memoria y verificación SHA-256). Informes en [`artifacts/perf/2026-11-11-import-linux`](../artifacts/perf/2026-11-11-import-linux/).

```
dotnet run -c Release --project tools/import-bench -- generate --out D:\pwbench\A --photos 3000 --photo-kb 1200 --videos 6 --video-mb 150 --dup-pct 25 --sidecar-pct 5 --live-pct 5
dotnet run -c Release --project tools/import-bench -- run --data D:\pwbench\A --work D:\pwbench\work --workers 2 --mode copy --background --drain --verify
```

**Entorno:** contenedor Linux, 4 vCPU Intel Xeon 2,1 GHz, 15 GB de RAM, disco virtual con caché de páginas caliente (no modela un HDD, un USB ni Defender). Release, .NET 10.

**Conjunto A:** 3 269 archivos, 5,4 GB. Contiene 3 000 JPEG de 2560×1920 (25 % duplicados exactos), 150 XMP, 125 clips Live Photo de 3 MB y 6 vídeos de 150 MB.

| Medida (A, copiar a la biblioteca, 2 workers) | Antes (`855c8e5`) | Después |
|---|---|---|
| Primer aviso de progreso | al terminar el escaneo | 0,003 s (fase «Buscando…») |
| Avisos de progreso enviados a la UI | 6 014 (2 por archivo) | 73 (≤ 5/s) |
| Primer elemento en la cuadrícula | al terminar toda la importación | 2 s después del primer elemento importado (refresco progresivo) |
| Importación (copia + SHA-256 + catálogo) | 15,8 s · 340 MiB/s | 14,7 s · 366 MiB/s |
| Miniaturas y hash perceptual tras importar | **271,7 s** | **64,3 s** |
| Miniaturas compitiendo con la importación | sí (137 durante la importación) | no: esperan a que termine; la cuadrícula sigue pidiendo las visibles |
| Memoria máxima | 404 MiB | 261 MiB |
| SHA-256 de cada copia frente al catálogo | 2 484/2 484 correctos | 2 484/2 484 correctos |

Causas encontradas, por orden de impacto:

1. **La interfaz no mostraba nada durante la importación.** La cuadrícula solo se recargaba con `ImportFinished`, y durante el escaneo no se emitía ningún progreso: la barra decía «Iniciando…» hasta tener la lista completa. Ahora el escaneo informa de los archivos examinados, los compatibles y la carpeta actual (solo el nombre). La cuadrícula se refresca a intervalos crecientes (de 2 s a 15 s) mientras tiene menos de 2 000 elementos y no hay selección.
2. **Las miniaturas decodificaban la foto completa.** Para 360 px, el escalado de JPEG a 1/8 se quedaba por debajo del objetivo y el código volvía al tamaño completo, en lugar de usar 2/8. Afecta a todos los JPEG del usuario: en Windows también se decodifican con Skia. HEIC y RAW pasan por WIC, que ya escalaba.
3. **La pasada de miniaturas era secuencial** (un núcleo) y **el hash perceptual volvía a decodificar el original**. Ahora la pasada usa todos los workers de fondo, y el hash se calcula desde la miniatura sin ediciones.
4. **Cada archivo encolaba dos avisos en el hilo de UI**, y cada aviso hacía dos saltos de despachador. Ahora los avisos están limitados a 5 por segundo en el servicio, y en la UI hay como máximo un despacho pendiente con el último valor.

**Workers (A, con miniaturas al final):**

| Workers | Importación | MiB/s | Archivos/s | CPU media | Miniaturas después |
|---|---|---|---|---|---|
| 1 | 24,1 s | 223 | 136 | 25 % | 128,9 s |
| 2 (por defecto con 4 núcleos) | 14,5 s | 370 | 225 | 48 % | 64,1 s |
| 4 | 10,6 s | 506 | 308 | 86 % | 34,1 s |
| 6 | 10,3 s | 524 | 319 | 85 % | 33,9 s |
| 8 | 10,0 s | 539 | 328 | 84 % | 32,0 s |

El rendimiento escala hasta el número de núcleos y después se estanca. No se cambia el valor por defecto (la mitad de los núcleos, entre 1 y 4), porque deja CPU libre para la interfaz. Con caché caliente, SHA-256 es el coste dominante. En un HDD o un USB el límite será el disco; hay que medirlo en Windows.

**Conjunto B:** 50 000 JPEG pequeños (2,2 GB, 25 % duplicados), 2 workers. La importación tarda 93,3 s (536 archivos/s), con 40 % de CPU media y 265 MiB de memoria máxima. El primer progreso llega a los 0,003 s y el primer elemento importado a los 1,3 s. Tiempo de worker sumado por fase:

| Fase | Tiempo |
|---|---|
| Copia + SHA-256 | 67,5 s |
| Inserción en el catálogo | 64,7 s |
| Estado de cada elemento | 22,9 s |
| Búsqueda de duplicados | 9,1 s |
| Metadatos | 6,6 s |
| Firma | 3,6 s |
| Escaneo | 0,14 s |

**Siguiente optimización medida, no hecha:** agrupar en lotes las escrituras en SQLite (inserción de recursos y estado de elementos). Con archivos pequeños suponen casi la mitad del tiempo.

## Pendiente de medir en Windows (QA manual)
- Páginas de Lugares y Recuerdos con 10k/50k/100k elementos reales (tiempo hasta el primer pintado, memoria).
- Importación de 1.000 JPEG reales (copiar y sin mover) en SSD y HDD.
- Generación de miniaturas por segundo con `MaxBackgroundWorkers` 1, 2 y 4.
- Desplazamiento de la cuadrícula con 50.000 elementos (fotogramas perdidos, memoria de trabajo).
- Apertura del visor (primer pintado desde miniatura grande y render a 3840 px).

## Rendimiento en Windows 11 (pendiente, manual)

**No medido.** Las cifras anteriores son de Linux o de Windows Server en CI con datos sintéticos. Procedimiento previsto para un equipo Windows 11 real, con bibliotecas de 10 000, 50 000 y 100 000 elementos:

1. Generar la biblioteca con archivos reales o duplicados de prueba. No usar fotos privadas en informes.
2. Medir el arranque en frío hasta el primer pintado de la cuadrícula, el desplazamiento rápido y la memoria (Administrador de tareas, conjunto de trabajo).
3. Medir el primer pintado de Lugares y Recuerdos, el tiempo de búsqueda y el análisis previo de la copia de originales.
4. Anotar el hardware (CPU, disco SSD/HDD, RAM) y la versión del commit.

Hasta que se haga, la fase 9 no pasa a `DONE`.
