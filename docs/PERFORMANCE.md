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

## Pendiente de medir en Windows (QA manual)
- Importación de 1.000 JPEG reales (copiar y sin mover) en SSD y HDD.
- Generación de miniaturas por segundo con `MaxBackgroundWorkers` 1, 2 y 4.
- Desplazamiento de la cuadrícula con 50.000 elementos (fotogramas perdidos, memoria de trabajo).
- Apertura del visor (primer pintado desde miniatura grande y render a 3840 px).
