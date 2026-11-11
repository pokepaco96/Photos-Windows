# Lista de comprobación de QA manual (Windows)

Se ejecuta en un **Windows 11 x64 limpio**. Marca cada paso solo después de ejecutarlo realmente, anotando fecha, versión y resultado. Las pruebas automáticas equivalentes están en `tests/` y se ejecutan en CI.

La columna **UI automatizada** recoge el resultado real de la suite `tests/PhotosWindows.UIAutomation.Tests`, que usa la app publicada a través de su interfaz (ver [`QA_AUTOMATION.md`](QA_AUTOMATION.md)). Entorno: Windows Server 2025 (`windows-latest`), 1920×1080, 100 % DPI, `en-US`, datos sintéticos. No sustituye a la prueba en Windows 11 con archivos reales de cámaras y teléfonos: la columna **Estado** (manual) solo se marca tras hacerlo a mano.

Preparación: una carpeta `QA-media` con JPEG con GPS, un HEIC de iPhone + su `.MOV`, un RAW+JPEG de cámara, un MP4 largo, un PNG, un archivo `.jpg` que en realidad sea texto, y una copia idéntica de un JPEG en otra subcarpeta.

| # | Paso | Resultado esperado | UI automatizada (pasos S01–S28 desde la ejecución #22; última completa #36 sobre `dd04124`, 54/54) | Estado manual |
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
| 18 | Personas con una biblioteca real (cientos de fotos propias, varias personas, niños, perfiles, gafas, poca luz) | Grupos coherentes; ningún nombre inventado; corregir, fusionar y separar funciona; sin bloqueos de la interfaz durante el análisis | ✅ S29–S37 con retratos de dominio público (ejecución #25): descarga de modelos, consentimiento, análisis cancelado y reanudado, 2 grupos correctos sin nombres inventados, nombrar, separar, fusionar, excluir, reiniciar, borrar todo. ✗ No sustituye a una biblioteca real | ☐ |
| 19 | Personas con Narrador y escalado 200 % | Las tarjetas y los rostros se anuncian con nombre y número de fotos; nada se corta | ◐ Nombres accesibles en todas las tarjetas y rostros (S31–S34). ✗ Narrador y 200 % no probados | ☐ |
| 20 | Borrar todos los datos de reconocimiento facial | Desaparece la sección; no quedan rostros ni nombres en el catálogo; fotos intactas | ✅ S37 (interfaz) e integración (`Delete_all_removes_every_derived_byte_and_keeps_the_library`: ni descriptores ni nombres en el archivo ni en el WAL) | ☐ |
| 21 | Lugares con una biblioteca real (miles de fotos con GPS, varios países) | Grupos coherentes al hacer zoom; lista y mapa coinciden; quitar ubicación no toca el archivo | ◐ S40–S49 con 9 fotos sintéticas, incluido el meridiano 180° (UI #32 y #36) | ☐ |
| 22 | Lugares con Narrador, DPI 150/200 % y contraste alto | Cada grupo se anuncia con zona, número y fechas; mapa nítido; nada cortado | ◐ Nombres accesibles y ventana 900×640 (S49) | ☐ |
| 23 | Recuerdos con una biblioteca real de varios años | «Tal día como hoy», viajes y eventos razonables; ninguna tarjeta con nombre de lugar inventado; la explicación coincide con las fotos | ◐ S50–S56 con 13 fotos sintéticas (UI #36) | ☐ |
| 24 | Pase de diapositivas con teclado y Narrador; vídeo dentro de un recuerdo | Espacio, flechas y Esc funcionan; los botones se anuncian; el vídeo se muestra como imagen fija | ◐ S52 (botones y teclado, sin Narrador) | ☐ |
| 25 | Ocultar y excluir recuerdos, cerrar y abrir la app | Siguen ocultos; restaurar los devuelve; ninguna foto borrada | ✅ S54, S55 | ☐ |
| 26 | Biblioteca grande real (10k/50k/100k) en Windows 11 | Importación, miniaturas, desplazamiento, búsqueda, visor, Lugares y Recuerdos fluidos; anotar RAM, CPU y tiempos | ☐ No medido | ☐ |
| 27 | iPhone físico: modelo e iOS, Dispositivos Apple instalado, desbloquear y Confiar | Aparece en «Dispositivos»; bloqueado o sin confiar muestra la ayuda | ◐ Ayuda y detección con dispositivo simulado (S70) | ☐ |
| 28 | iPhone: explorar sin importar | Miniaturas y vista previa; la biblioteca y el iPhone no cambian | ◐ S71 (simulado) | ☐ |
| 29 | iPhone: importar solo (selección y todo), HEIC, HEVC/MOV y Live Photo | Copias idénticas (SHA-256) en la biblioteca; Live Photo con su clip; iPhone intacto | ◐ S72 (simulado, JPEG) | ☐ |
| 30 | iPhone: «Conservar originales» frente a conversión automática | Con conversión, la app avisa y no borra; con originales, HEIC/HEVC llegan sin convertir | ☐ | ☐ |
| 31 | iPhone: fotos solo en iCloud (Optimizar almacenamiento) | No aparecen o fallan con explicación; nada se da por importado | ☐ | ☐ |
| 32 | iPhone: desconectar durante la copia; reimportar duplicados | Lo copiado queda verificado; lo demás «no disponible»; duplicados reconocidos | ◐ Integración con dispositivo simulado | ☐ |
| 33 | iPhone: importar y eliminar | Hoy debe aparecer desactivado con su explicación. Solo se activará tras verificar el borrado con este modelo | ◐ S72 (desactivado), S73 (simulado con borrado) | ☐ |
| 34 | Explorar o importar no cambia nada en el iPhone (comparar número y hashes antes/después) | Idéntico | ◐ S71, S72 (simulado) | ☐ |
| 35 | Copia de originales a un disco externo real (NTFS o exFAT) con una biblioteca real | Árbol AÑO/MES; recuento y SHA-256 iguales al manifiesto; «Verificar» correcto; originales intactos | ◐ S60–S64 con datos sintéticos en el disco del runner | ☐ |
| 36 | Copia de originales: desconectar el disco a mitad y reanudar (también con otra letra de unidad) | Queda `.partial`; reanudar termina sin duplicar y verifica lo ya copiado | ◐ Integración y S62 (simulado) | ☐ |
| 37 | Copia de originales a una memoria FAT32 con un vídeo de más de 4 GB | Se avisa antes de empezar; no se da por completa | ◐ Integración (límite simulado) | ☐ |
