# Revisión: mapas offline para «Lugares»

Fecha de la revisión: 8 nov 2026. Fuentes consultadas directamente (páginas oficiales de cada proyecto y política de uso de tiles de la OSMF). Nada de esta revisión es asesoramiento jurídico; donde hay dudas se indica.

## Requisitos de partida

- Funciona **sin red**. Ninguna coordenada del usuario sale del equipo: sin tiles remotos por defecto, sin geocodificación ni geocodificación inversa remota.
- Licencias revisadas **por separado** para: código del motor, binarios nativos, estilo, datos cartográficos y tiles generados.
- Cualquier descarga de mapas es iniciada por el usuario, muestra origen, licencia, atribución, tamaño y destino, verifica integridad, tiene límite de tamaño y se puede eliminar.
- Debe existir un **modo lista** que funcione sin ningún mapa.

## Tecnologías evaluadas

| Opción | Código | Datos / estilo | Offline | .NET / WinUI 3 | Notas |
|---|---|---|---|---|---|
| Servidores públicos `tile.openstreetmap.org` | — | ODbL (datos), tiles CC BY-SA | **Prohibido** | — | La *Tile Usage Policy* de la OSMF dice literalmente «Offline use is not permitted» y prohíbe la descarga masiva y el *prefetch*. Además cada petición revelaría al servidor la zona que mira el usuario. **Descartado.** |
| Mapsui 4.x/5.x | MIT | Lo que se le dé | Sí (MBTiles, capas propias) | Sí (`Mapsui.WinUI`) | Motor maduro, pero añade SkiaSharp, BruTile y NetTopologySuite (todos MIT/BSD, pero ~15 MB y nativos por arquitectura). |
| MapLibre Native | BSD-2 | Estilos de terceros | Sí (MBTiles vectorial) | **Sin binding oficial .NET** | Habría que mantener un binding C++/WinRT propio. Coste y riesgo altos. |
| WebView2 + MapLibre GL JS | BSD-3 | Estilos de terceros | Sí con servidor local | Sí | Introduce un motor web y un servidor local de tiles. Contradice el espíritu «nativo» del proyecto y amplía la superficie de ataque. |
| `MapControl` de Windows (Bing) | Propietario | Bing/Azure Maps | No | UWP, retirado | Requiere clave y red. **Descartado.** |
| Protomaps (PMTiles) | BSD-3 | Estilo CC0, datos OSM (ODbL) | Sí | Lector propio necesario | Formato excelente para un único fichero; los **datos** siguen siendo ODbL: redistribuirlos dentro de la app implica atribución y obligaciones *share-alike* sobre la base de datos. No se incluye; queda como formato candidato para mapas instalados por el usuario. |
| **Natural Earth** (vectores) | — | **Dominio público** | Sí | Render propio | «All versions of Natural Earth raster + vector map data found on this website are in the public domain.» Escala 1:50m/1:110m: costas, fronteras de países, lagos. Sin calles. |

## Recomendación (adoptada en ADR-0010)

1. **Mapa base integrado** generado a partir de Natural Earth (dominio público) y **renderizado por la propia app** con Win2D/XAML Shapes, detrás de una abstracción (`IMapRenderer`/`MapProjection` en el núcleo). Sin dependencias nuevas de terceros ni binarios nativos.
   - Capas: tierra 1:110m (zoom bajo), tierra 1:50m (zoom alto), lagos 1:50m y fronteras terrestres 1:50m.
   - Se convierten con un generador **reproducible** (`tools/basemap/build_basemap.py`) a un binario compacto (`world.pwmap`, float32, delta por anillo) que va embebido como recurso. El generador anota versión, URL de origen y SHA-256 de cada ZIP de entrada.
   - Atribución: no es obligatoria en dominio público, pero se muestra «Mapa: Natural Earth (dominio público)» por transparencia.
2. **Modo lista** (agrupado por zona/cluster y fecha) que no depende de ningún mapa y es el modo accesible por defecto para lectores de pantalla.
3. **Mapas detallados opcionales** (fase posterior, no en este PR): el usuario elige un fichero MBTiles/PMTiles **que ya tiene** («Abrir archivo…»), la app lee sus metadatos (`attribution`, `name`, licencia) y los muestra siempre en pantalla. La app **no** descarga tiles ni ofrece un catálogo propio hasta que haya una fuente con licencia que permita redistribución offline y un proceso de descarga con verificación de integridad. Esto queda documentado como limitación, sin botones que aparenten funcionar.
4. **Sin geocodificación.** Los grupos de lugares se nombran por coordenadas aproximadas y, si existe, por el título/álbum más común del grupo. La geocodificación inversa offline (p. ej. GeoNames, CC BY 4.0) queda como propuesta futura con su propia revisión de licencia.

## Almacenamiento, actualizaciones y privacidad

- Mapa base: ~0,4–0,7 MB embebidos (medido en la generación; ver `tools/basemap/README.md`). No hay actualizaciones automáticas; se regenera con una nueva versión de Natural Earth y se publica con la app.
- No se escriben tiles en disco ni se cachean peticiones: no hay peticiones.
- Las coordenadas sólo viven en el catálogo SQLite local, como hasta ahora. Las preferencias de filtros de Lugares se guardan en la configuración local del usuario.
- Riesgo residual: capturas de pantalla o exportaciones del usuario pueden contener ubicación; ya cubierto por la opción de eliminar GPS al exportar (`docs/PRIVACY.md`).

## Riesgos y límites conocidos

- La resolución 1:50m no muestra calles ni ciudades: sirve para ver «dónde» a escala de país/región; para más detalle el usuario usa la lista o, en el futuro, sus propios MBTiles.
- Las fronteras de Natural Earth reflejan su política *de facto*; algunas zonas son disputadas. Se documenta en la pantalla «Acerca de».
- El render propio no implementa rotación ni inclinación; proyección Web Mercator limitada a ±85,05°. Las fotos por encima de esa latitud se muestran en el borde y en la lista.
