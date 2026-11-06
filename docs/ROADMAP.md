# Roadmap

Refleja lo **terminado** (✅), en curso (🔄) y pendiente (⏳). El detalle con evidencias está en `PROGRESS.md`.

## Hecho
- ✅ Núcleo: dominio, catálogo SQLite (WAL, FTS5, migraciones), importación reanudable, metadatos, miniaturas, organización, búsqueda, edición no destructiva, exportación, duplicados, copias de seguridad y verificación — con pruebas automáticas.
- ✅ CI en Linux y Windows.

## Siguiente
- 🔄 Aplicación WinUI 3 sobre el núcleo (Fases 1, 3–6 en la UI).
- ⏳ Plataforma Windows: WIC (HEIF/AVIF/TIFF/RAW) y fotogramas de vídeo con Media Foundation.
- ⏳ Empaquetado MSIX sin firmar para pruebas + instrucciones (Fase 10).

## Después del primer release candidate
- ⏳ Evaluar migración a Windows App SDK 2.x (ADR-0002).
- ⏳ Lugares con mapa offline u opt-in; recuerdos y presentaciones (Fase 8).
- ⏳ Personas y mascotas con modelo ONNX local de licencia verificada (Fase 8, ADR-0008).
- ⏳ Edición avanzada: curvas, niveles, reducción de ruido, máscaras; edición en 16 bits.
- ⏳ RAW avanzado (LibRaw) tras evaluar licencia y tamaño.
- ⏳ Pruebas de rendimiento con 10k/50k/100k elementos sintéticos (Fase 9).
- ⏳ ARM64.
