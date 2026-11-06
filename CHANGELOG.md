# Changelog

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versionado semántico.

## [Unreleased]
### Added
- Núcleo de biblioteca: catálogo SQLite con migraciones, FTS5 y copias previas a migración.
- Importación «Añadir sin mover» y «Copiar a la biblioteca» con SHA-256, duplicados, RAW+JPEG, clips de movimiento, sidecars XMP, cancelación y reanudación.
- Lectura de metadatos EXIF/IPTC/XMP/GPS/QuickTime.
- Miniaturas en caché con versión de edición; hash perceptual.
- Álbumes, carpetas, álbumes inteligentes, palabras clave, favoritos, ocultos, papelera y borrado definitivo con plan previo.
- Búsqueda local con filtros combinables.
- Edición no destructiva (geometría, luz, color, efectos, estilos) con deshacer/rehacer e historial.
- Exportación de originales y editados (JPEG/PNG/WebP) con metadatos limpios.
- Duplicados exactos y similares; copias de seguridad verificadas y restauración; verificación y reparación de biblioteca.
- Aplicación WinUI 3: bienvenida, fototeca agrupada y virtualizada, visor, editor, álbumes e inteligentes, duplicados, importaciones, ajustes; español e inglés.
- Capa Windows: decodificación WIC (HEIF/AVIF/TIFF/RAW con las extensiones de Microsoft Store) y fotogramas de vídeo.
- Pruebas de rendimiento del catálogo (10k/50k elementos).
### Fixed
- Las copias de seguridad fallaban en Windows al renombrar la carpeta final porque una conexión SQLite agrupada mantenía abierto el catálogo copiado.
- La inferencia de zona horaria por GPS aceptaba cualquier diferencia.
