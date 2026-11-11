# Roadmap

Refleja lo **terminado** (✅), en curso (🔄) y pendiente (⏳). El detalle con evidencias está en `PROGRESS.md`.

## Hecho
- ✅ Núcleo: dominio, catálogo SQLite (WAL, FTS5, migraciones), importación reanudable, metadatos, miniaturas, organización, búsqueda, edición no destructiva, exportación, duplicados, copias de seguridad y verificación — con pruebas automáticas.
- ✅ Aplicación WinUI 3 sobre el núcleo; capa Windows (WIC, Media Foundation); publicación autocontenida x64 con prueba de arranque en CI.
- ✅ CI en Linux y Windows; suite de UI Automation que usa la app publicada a través de su interfaz (ver `QA_AUTOMATION.md`).

## Hecho (integrado en la rama principal, pendiente de validación manual en Windows 11)
- ✅ **Personas**: detección y agrupación facial local con modelos descargados por el usuario, consentimiento, corrección manual y borrado total ([ADR-0010](decisions/0010-people-local-face-grouping.md)).
- ✅ **Lugares**: mapa offline propio (Natural Earth), grupos por zona, lista, filtros, edición de ubicación en el catálogo y búsqueda por área que cruza el antimeridiano ([ADR-0011](decisions/0011-places-offline-map.md)).

- ✅ **Recuerdos**: reglas deterministas y explicables, pase de diapositivas, ocultar y excluir ([ADR-0012](decisions/0012-memories-deterministic-rules.md), [`MEMORIES.md`](MEMORIES.md)).

## Siguiente
- ⏳ QA en Windows 11 real: Narrador, DPI 150/200 %, contraste alto, HEIC/AVIF/TIFF/RAW y vídeo reales, disco externo, importación interrumpida, copia y restauración reales.
- ⏳ Rendimiento medido en Windows con 10k/50k/100k elementos: importación, miniaturas, desplazamiento, búsqueda, visor, Lugares y Recuerdos (RAM, CPU, latencia).
- ⏳ Distribución: instalador/MSIX, firma, actualización y desinstalación conservando las bibliotecas (Fase 10).
- ⏳ Revisión jurídica de los modelos de rostros antes de cualquier uso comercial.
- ⏳ Lugares: mapas detallados instalados por el usuario (MBTiles/PMTiles), solo si hay una fuente con derechos de uso offline claros.

## Después del primer release candidate
- ⏳ Evaluar migración a Windows App SDK 2.x (ADR-0002).
- ⏳ Edición avanzada: curvas, niveles, reducción de ruido, máscaras; edición en 16 bits.
- ⏳ RAW avanzado (LibRaw) tras evaluar licencia y tamaño.
- ⏳ Pruebas de rendimiento con 10k rostros sintéticos.
- ⏳ ARM64 (ONNX Runtime ya publica binarios win-arm64; falta probar la app).
