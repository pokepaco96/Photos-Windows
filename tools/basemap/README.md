# Mapa base integrado (Natural Earth)

`build_basemap.py` genera `src/PhotosWindows.Imaging/Maps/world.pwmap`, el mapa del mundo que usa «Lugares» sin conexión.

- **Datos:** Natural Earth (naturalearthdata.com), **dominio público**. Capas: tierra 1:110m y 1:50m, lagos 1:50m y fronteras terrestres 1:50m. URL, versión y SHA-256 de cada ZIP están fijados en el script.
- **Reproducible:** solo usa la biblioteca estándar de Python 3, y la salida es idéntica byte a byte entre ejecuciones (verificado: SHA-256 `97ed2537…e7ec` en dos ejecuciones).
- **Formato `pwmap/1`:** cabecera mágica más un flujo DEFLATE. Coordenadas en 1e-4° (~11 m), codificadas en delta con varints zigzag. Está descrito en el propio script, y el lector es `WorldBasemap.cs`, que comprueba el SHA-256 antes de usarlo.
- **Tamaño medido:** 366 942 bytes comprimidos (414 006 sin comprimir) para 104 180 vértices.

```bash
# con los ZIP ya descargados en ./ne
python3 tools/basemap/build_basemap.py --cache ./ne --out src/PhotosWindows.Imaging/Maps/world.pwmap
# o dejando que los descargue (petición iniciada por el desarrollador, nunca por la app)
python3 tools/basemap/build_basemap.py --cache ./ne --download --out src/PhotosWindows.Imaging/Maps/world.pwmap
```

Al regenerar hay que actualizar `WorldBasemap.EmbeddedSha256` y los recuentos de puntos de `MapRendererTests` con los valores de `world.manifest.json`.
