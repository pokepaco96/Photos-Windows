# Roadmap

Refleja lo **terminado** (✅), en curso (🔄) y pendiente (⏳). El detalle con evidencias está en `PROGRESS.md`.

## Hecho
- ✅ Núcleo: dominio, catálogo SQLite (WAL, FTS5, migraciones), importación reanudable, metadatos, miniaturas, organización, búsqueda, edición no destructiva, exportación, duplicados, copias de seguridad y verificación — con pruebas automáticas.
- ✅ Aplicación WinUI 3 sobre el núcleo; capa Windows (WIC, Media Foundation); publicación autocontenida x64 con prueba de arranque en CI.
- ✅ CI en Linux y Windows; suite de UI Automation que usa la app publicada a través de su interfaz (ver `QA_AUTOMATION.md`).

## En curso
- 🔄 **Personas** (hito 1, rama `claude/smart-people`): detección y agrupación facial local con modelos descargados por el usuario, consentimiento, corrección manual y borrado total ([ADR-0010](decisions/0010-people-local-face-grouping.md)). Pendiente de revisión y de prueba en Windows 11.

## Siguiente
- ⏳ Mascotas: detección por especie (YOLOX-s), sin identidad individual.
- ⏳ Lugares: agrupación de coordenadas, vista de lista y mapa offline con licencia clara (incluye corregir la búsqueda por área que cruza el antimeridiano).
- ⏳ Recuerdos: reglas transparentes (en este día, viajes, eventos, temporadas, resumen anual) y presentación.
- ⏳ QA en Windows 11 real: Narrador, DPI 125/150/200 %, contraste alto, HEIC/AVIF/RAW reales, disco externo, importación interrumpida.
- ⏳ Empaquetado MSIX/instalador (Fase 10).

## Después del primer release candidate
- ⏳ Evaluar migración a Windows App SDK 2.x (ADR-0002).
- ⏳ Edición avanzada: curvas, niveles, reducción de ruido, máscaras; edición en 16 bits.
- ⏳ RAW avanzado (LibRaw) tras evaluar licencia y tamaño.
- ⏳ Pruebas de rendimiento con 100k elementos y 10k rostros sintéticos.
- ⏳ ARM64 (ONNX Runtime ya publica binarios win-arm64; falta probar la app).
