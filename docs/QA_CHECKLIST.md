# Lista de comprobación de QA manual (Windows)

Se ejecuta en un **Windows 11 x64 limpio**. Marca cada paso solo después de ejecutarlo realmente, anotando fecha, versión y resultado. Las pruebas automáticas equivalentes están en `tests/` y se ejecutan en CI.

Preparación: una carpeta `QA-media` con JPEG con GPS, un HEIC de iPhone + su `.MOV`, un RAW+JPEG de cámara, un MP4 largo, un PNG, un archivo `.jpg` que en realidad sea texto, y una copia idéntica de un JPEG en otra subcarpeta.

| # | Paso | Resultado esperado | Estado |
|---|---|---|---|
| 1 | Instalar el paquete (Fase 10) | Arranca desde el menú Inicio; aparece la pantalla de bienvenida | ☐ No ejecutado |
| 2 | Crear biblioteca en `Imágenes` | Se crea `Nombre.pwlibrary` con `library.json`, `catalog.db`, `originals`, `cache`, `backups` | ☐ |
| 3 | Importar `QA-media` con «Copiar» | Progreso real; informe: N importados, 1 duplicado, 1 no compatible; originales sin cambios (comparar hash) | ☐ |
| 4 | Importar otra carpeta con «Añadir sin mover» | No aparece nada nuevo en `originals` | ☐ |
| 5 | Copiar a la biblioteca | Archivos en `originals/AAAA/MM` | ☐ |
| 6 | Importar duplicados | Se informan como duplicados; no se copian | ☐ |
| 7 | Abrir imagen y vídeo | Visor con zoom/desplazamiento/anterior-siguiente; vídeo con sonido y búsqueda; HEIC visible si la extensión HEIF está instalada | ☐ |
| 8 | Editar una foto, cerrar y reabrir la app | Ajustes conservados; hash del original idéntico | ☐ |
| 9 | Crear álbum y álbum inteligente | El álbum no duplica archivos; el inteligente se actualiza al marcar favoritos | ☐ |
| 10 | Buscar por fecha, palabra clave y tipo | `2023`, `playa`, `vídeos` devuelven lo esperado | ☐ |
| 11 | Desconectar el disco externo de una carpeta añadida sin mover | Miniaturas siguen visibles; el visor indica «archivo no disponible»; al reconectar y verificar vuelve a estar disponible | ☐ |
| 12 | Cerrar la app durante una importación | Al abrir: importación «Interrumpida» → Reanudar completa el resto sin duplicar | ☐ |
| 13 | Crear copia de seguridad y restaurarla como biblioteca nueva | Verificación correcta; misma cantidad de elementos y álbumes | ☐ |
| 14 | Recuperar un elemento de Eliminados recientemente | Vuelve a la fototeca | ☐ |
| 15 | Desinstalar y reinstalar | La biblioteca sigue en su carpeta y se puede abrir | ☐ |
| 16 | Accesibilidad | Navegación completa con teclado (Tab, flechas, Enter, Supr, Ctrl+A); Narrador lee nombres de botones y elementos; escalado 200 % sin cortes; contraste alto | ☐ |
| 17 | Tema | Claro/oscuro/sistema cambian al vuelo | ☐ |
