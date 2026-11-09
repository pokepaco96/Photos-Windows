# ADR-0011 · Lugares: mapa offline propio (Natural Earth) y modo lista

**Contexto.** «Lugares» debe mostrar las fotos geolocalizadas sin red y sin enviar coordenadas a nadie. Los servidores públicos de teselas de OpenStreetMap prohíben el uso offline y la descarga masiva; MapLibre Native no tiene binding .NET oficial; WebView + JS añade un motor web. Detalle en `docs/research/OFFLINE_MAPS_REVIEW.md`.

**Decisión.**
- Mapa base **integrado**, generado de forma reproducible (`tools/basemap/build_basemap.py`) a partir de Natural Earth (**dominio público**): tierra 1:110m/1:50m, lagos y fronteras terrestres 1:50m. Formato propio `pwmap/1` (367 KB), embebido en `PhotosWindows.Imaging` y verificado por SHA-256 al cargarlo.
- Render con SkiaSharp (ya dependencia) detrás de `IMapRenderer`: se puede sustituir sin tocar la UI. Si el mapa falta o está corrupto, Lugares muestra solo la lista y un aviso.
- Agrupación por rejilla en espacio proyectado, por nivel de zoom entero (estable al hacer zoom), O(n) y determinista. Los grupos son botones reales (accesibles por teclado, lector de pantalla y UI Automation).
- **Modo lista** completo y un interruptor para desactivar el mapa (persistido).
- Sin geocodificación: las zonas se describen con coordenadas aproximadas («Alrededor de 40,42° N, 3,70° W»), nunca con una dirección.
- Una sola fuente de verdad: las coordenadas del catálogo (`assets.latitude/longitude`). No se añaden tablas ni se duplican coordenadas; la eliminación de ubicación solo toca el catálogo.

**Alternativas.** Mapsui + MBTiles (más dependencias nativas, y sigue faltando un origen de datos con licencia redistribuible); paquetes regionales descargables (aplazado hasta tener una fuente con licencia y verificación de firma, ver revisión).

**Consecuencias.** Sin calles ni ciudades: el detalle llega a escala de región (zoom ≤ 10). Mapas detallados instalados por el usuario (MBTiles/PMTiles con su atribución) quedan como fase posterior, sin botones que aparenten funcionar.
