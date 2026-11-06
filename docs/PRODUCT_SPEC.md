# Especificación de producto — Photos Windows

> Nombre de trabajo interno. Sin afiliación con Apple. No usa marcas, iconos, código ni recursos de Apple.

## Visión
Gestor fotográfico personal para Windows 11, **local-first y offline-first**, con una experiencia comparable a una aplicación de fotos moderna de escritorio: importar, catalogar, ver, buscar, organizar, editar sin destruir, exportar y proteger una colección de fotos y vídeos. Escala a decenas o cientos de miles de elementos.

## Fuera de alcance (no negociable)
iCloud y cualquier servicio de Apple; cuentas de usuario; sincronización en la nube; telemetría remota; IA remota; geocodificación remota por defecto. No existen botones, menús ni ajustes relacionados.

## Principios
1. Los originales son sagrados (solo lectura; ver ADR-0006).
2. La biblioteca pertenece al usuario (carpeta legible, ADR-0003).
3. Privacidad local.
4. Interfaz siempre fluida (E/S y CPU fuera del hilo de UI, virtualización).
5. Recuperación (copias verificadas, verificación/reparación).
6. Transparencia (informes de importación por archivo, estados claros).
7. Accesibilidad (teclado, Narrador, DPI, contraste).
8. Mantenibilidad (capas, MVVM, pruebas).

## Usuarios y escenarios principales
- **Ana, aficionada**: vuelca la tarjeta SD del móvil y la cámara → «Copiar a la biblioteca», ve la cronología por días, marca favoritas, crea un álbum del viaje y exporta 20 fotos editadas para compartir.
- **Luis, fotos ya ordenadas en carpetas** de un disco externo → «Añadir sin mover»; desconecta el disco: la app sigue funcionando y marca los originales como no disponibles; al reconectar vuelven a estar disponibles.
- **Marta, duplicados**: tras años de copias, revisa duplicados exactos y similares y decide qué conservar; nada se borra sin su confirmación.

## Navegación (barra lateral)
**Biblioteca**: Fototeca (agrupación por Años / Meses / Días / Todo), Vídeos, Favoritos, Añadidos recientemente, Editados.
**Álbumes**: Mis álbumes (con carpetas), Álbumes inteligentes.
**Utilidades**: Duplicados, Ocultos, Eliminados recientemente, Importaciones.
**Pie**: Importar, Ajustes (biblioteca, almacenamiento, caché, copias de seguridad, verificación, apariencia, privacidad, acerca de/licencias).
Las secciones «Personas y mascotas», «Lugares» (mapa) y «Recuerdos» no se muestran hasta que funcionen (ADR-0008).

## Funciones (estado detallado en PROGRESS.md)
| Área | Comportamiento |
|---|---|
| Bibliotecas | Crear, abrir, recientes, cambiar, cerrar; errores claros (no encontrada, no es biblioteca, catálogo ausente/dañado, versión más nueva); estadísticas; mover caché; vaciar caché |
| Importación | Carpetas/archivos/unidades; Añadir sin mover / Copiar; SHA-256; duplicados; RAW+JPEG; clip de movimiento (≤4,5 s o mismo ContentIdentifier); sidecar XMP; progreso real con ETA; cancelar y reanudar; informe por archivo |
| Catálogo | Fecha de captura con zona (EXIF OffsetTime, inferida por GPS, QuickTime UTC), cámara, objetivo, exposición, GPS/altitud, dimensiones, orientación, duración; respaldo por nombre de archivo/fecha de modificación |
| Cuadrícula | Virtualizada, agrupada por día/mes/año, zoom, selección Ctrl/Shift, indicadores (vídeo, RAW, movimiento, favorito, editado, no disponible) |
| Visor | Ajustar, zoom, desplazamiento, anterior/siguiente, giro de visualización, pantalla completa, inspector; vídeo con controles del sistema |
| Organización | Álbumes, carpetas, orden manual, inteligentes (14 campos, todas/cualquiera), favoritos, palabras clave, valoración, título/descripción, ocultos, papelera |
| Búsqueda | Texto (FTS5, sin acentos, prefijos), años/meses/días, tipo, favoritos, editados, cámara, objetivo, formato, álbum, área GPS |
| Edición | Recortar, proporción, enderezar, girar, voltear, exposición, brillo, contraste, altas luces, sombras, saturación, vibrancia, temperatura, matiz, B/N, viñeta, nitidez, 9 estilos propios; deshacer/rehacer; antes/después; historial |
| Exportación | Original exacto (+RAW/clip) o editado JPEG/PNG/WebP, calidad, tamaño, metadatos (fecha/cámara, ubicación opt-in, título/palabras clave XMP); nunca sobrescribe |
| Duplicados | Exactos (SHA-256) y similares (dHash, sugerencia); conservar uno / descartar sugerencia |
| Copias | Copia verificada con checksums; restaurar como biblioteca nueva o restaurar solo catálogo |
| Mantenimiento | Verificar integridad y archivos, volver a vincular movidos, adoptar huérfanos, limpiar parciales, reconstruir índice |
