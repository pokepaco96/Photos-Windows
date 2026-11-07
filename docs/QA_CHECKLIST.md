# Lista de comprobación de QA manual (Windows)

Se ejecuta en un **Windows 11 x64 limpio**. Marca cada paso solo después de ejecutarlo realmente, anotando fecha, versión y resultado. Las pruebas automáticas equivalentes están en `tests/` y se ejecutan en CI.

La columna **UI automatizada** recoge el resultado real de la suite `tests/PhotosWindows.UIAutomation.Tests`, que usa la app publicada a través de su interfaz (ver [`QA_AUTOMATION.md`](QA_AUTOMATION.md)). Entorno: Windows Server 2025 (`windows-latest`), 1920×1080, 100 % DPI, `en-US`, datos sintéticos. No sustituye a la prueba en Windows 11 con archivos reales de cámaras y teléfonos: la columna **Estado** (manual) solo se marca tras hacerlo a mano.

Preparación: una carpeta `QA-media` con JPEG con GPS, un HEIC de iPhone + su `.MOV`, un RAW+JPEG de cámara, un MP4 largo, un PNG, un archivo `.jpg` que en realidad sea texto, y una copia idéntica de un JPEG en otra subcarpeta.

| # | Paso | Resultado esperado | UI automatizada (ejecución #22, 28/28) | Estado manual |
|---|---|---|---|---|
| 1 | Instalar el paquete (Fase 10) | Arranca desde el menú Inicio; aparece la pantalla de bienvenida | ◐ Arranque del ejecutable publicado (carpeta portátil) y bienvenida: S01 ✅ y smoke de CI. Sin instalador (ADR-0009) | ☐ No ejecutado |
| 2 | Crear biblioteca en `Imágenes` | Se crea `Nombre.pwlibrary` con `library.json`, `catalog.db`, `originals`, `cache`, `backups` | ✅ S02 (diálogo de nombre + selector de carpetas real) | ☐ |
| 3 | Importar `QA-media` con «Copiar» | Progreso real; informe: N importados, 1 duplicado, 1 no compatible; originales sin cambios (comparar hash) | ✅ S04 (7 nuevos, 1 duplicado, 0 fallos) y S28 (SHA-256 de todo el dataset sin cambios) | ☐ |
| 4 | Importar otra carpeta con «Añadir sin mover» | No aparece nada nuevo en `originals` | ✅ S05 (ningún archivo nuevo en `originals`) | ☐ |
| 5 | Copiar a la biblioteca | Archivos en `originals/AAAA/MM` | ✅ S04 (copias gestionadas presentes) | ☐ |
| 6 | Importar duplicados | Se informan como duplicados; no se copian | ✅ S06 | ☐ |
| 7 | Abrir imagen y vídeo | Visor con zoom/desplazamiento/anterior-siguiente; vídeo con sonido y búsqueda; HEIC visible si la extensión HEIF está instalada | ✅ S08–S11 con JPEG/PNG/MP4 sintéticos (zoom, anterior/siguiente, flechas, vídeo reproduciéndose). ✗ HEIC/RAW reales no probados | ☐ |
| 8 | Editar una foto, cerrar y reabrir la app | Ajustes conservados; hash del original idéntico | ✅ S17–S19 (reinicio real de la app; hash del original idéntico) | ☐ |
| 9 | Crear álbum y álbum inteligente | El álbum no duplica archivos; el inteligente se actualiza al marcar favoritos | ✅ S12, S14 | ☐ |
| 10 | Buscar por fecha, palabra clave y tipo | `2023`, `playa`, `vídeos` devuelven lo esperado | ✅ S16 (palabra clave, año, tipo) | ☐ |
| 11 | Desconectar el disco externo de una carpeta añadida sin mover | Miniaturas siguen visibles; el visor indica «archivo no disponible»; al reconectar y verificar vuelve a estar disponible | ☐ No automatizado | ☐ |
| 12 | Cerrar la app durante una importación | Al abrir: importación «Interrumpida» → Reanudar completa el resto sin duplicar | ☐ No automatizado | ☐ |
| 13 | Crear copia de seguridad y restaurarla como biblioteca nueva | Verificación correcta; misma cantidad de elementos y álbumes | ✅ S22 (copia verificada con originales), S26 (restauración como biblioteca nueva; mismo número de fotos, vídeos y álbumes) | ☐ |
| 14 | Recuperar un elemento de Eliminados recientemente | Vuelve a la fototeca | ✅ S23 (y borrado definitivo solo sobre la biblioteca sintética) | ☐ |
| 15 | Desinstalar y reinstalar | La biblioteca sigue en su carpeta y se puede abrir | — No aplica a la carpeta portátil | ☐ |
| 16 | Accesibilidad | Navegación completa con teclado (Tab, flechas, Enter, Supr, Ctrl+A); Narrador lee nombres de botones y elementos; escalado 200 % sin cortes; contraste alto | ◐ Teclado (S25: flechas, Enter, Escape, Ctrl+F) y nombres accesibles de todos los botones visibles (S27). ✗ Narrador, 200 % y contraste alto no probados | ☐ |
| 17 | Tema | Claro/oscuro/sistema cambian al vuelo | ✅ S24 (claro/oscuro/sistema al vuelo, contraste de los botones de la ventana) | ☐ |
