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
- Suite de UI Automation (FlaUI/UIA3) que usa la app publicada a través de su interfaz: 28 escenarios, dataset sintético reproducible con SHA-256, perfil aislado (`PHOTOSWINDOWS_DATA_DIR`), capturas, árbol de accesibilidad y logs como evidencia; workflow *UI automation (Windows)*.
- Prueba de arranque del ejecutable publicado en CI.
- Identificadores y nombres accesibles en todos los controles, miniaturas y tarjetas de álbum.
- Miniaturas de vídeo mediante Media Foundation cuando Windows no ofrece miniatura del shell.
### Fixed
- La app publicada se cerraba al arrancar: `dotnet publish` no incluía `resources.pri` ni los `.xbf`.
- La ventana inicial podía ser mayor que la pantalla.
- La barra lateral resaltaba «Mis álbumes» estando en «Todas las fotos».
- La barra de actividad mostraba el progreso en lugar del resumen al terminar una importación, exportación o copia.
- Botones de la ventana casi invisibles con el tema oscuro de la app y Windows en claro.
- Valores cortados en las tarjetas de almacenamiento de Ajustes.
- Quitar una foto de un álbum la dejaba en pantalla y dejaba las miniaturas en blanco.
- Enter no abría la foto seleccionada; en el visor, las flechas y Escape no funcionaban según dónde estuviera el foco.
- En el visor la foto quedaba pegada arriba, o desbordaba tras «Siguiente».
- Lectores de pantalla: los detalles anunciaban la etiqueta sin el valor.
- Tooltip «Ctrl+Shift+I» al pasar el ratón por cualquier parte de la ventana.
- Plurales («1 photos»), recuento de las tarjetas de álbum y botón «Reset crop» cortado en el editor.
- Las copias de seguridad fallaban en Windows al renombrar la carpeta final porque una conexión SQLite agrupada mantenía abierto el catálogo copiado.
- La inferencia de zona horaria por GPS aceptaba cualquier diferencia.
