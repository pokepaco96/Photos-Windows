# Copias de seguridad y restauración

> Una copia del **catálogo** no es una copia de tus **fotos**. Para copiar los archivos originales de toda la biblioteca, incluidos los «añadidos sin mover», usa la **copia de originales** (página «Copia de seguridad»).

## Dos copias distintas
| | Copia de originales (página «Copia de seguridad») | Copia de biblioteca (Ajustes) |
|---|---|---|
| Qué guarda | El archivo original de **todos** los elementos del catálogo: gestionados y **añadidos sin mover**, RAW, clips de Live Photo y XMP; también ocultos y en «Eliminados recientemente» | Catálogo (álbumes, palabras clave, ediciones, favoritos) y, opcionalmente, los originales copiados dentro de la biblioteca |
| Formato | Carpetas normales `AÑO/MES` + `backup-manifest.json` | Carpeta `PhotosWindows-Backup-…` con `catalog.db`, `library.json`, `backup.json` |
| Para qué | Tener tus fotos y vídeos fuera de la app, abribles con cualquier programa | Restaurar la biblioteca completa en Photos Windows |
| Restaurar | No se «restaura»: son tus archivos. Para volver a la app, impórtalos | «Restaurar como biblioteca nueva» |

## Copia de originales (AÑO/MES)
1. «Copia de seguridad» en la barra lateral › «Elegir destino…» (disco externo, memoria USB u otra carpeta).
2. La app analiza la biblioteca: fotos, vídeos, archivos, tamaño, espacio libre y archivos no disponibles (por ejemplo, en un disco desconectado).
3. «Crear copia de seguridad de originales». Crea `PhotosWindows-Originales-Backup-<biblioteca>-<fecha>` con esta estructura:

```text
2023/12/DSC_0100.JPG
2023/12/DSC_0100.CR2        ← RAW del mismo disparo, junto a su JPEG
2024/07/IMG_0200.HEIC
2024/07/IMG_0200.MOV        ← clip de la Live Photo
2024/07/IMG_0200 (2).HEIC   ← otro archivo con el mismo nombre ese mes
Sin fecha/00/archivo.mov    ← la biblioteca no conoce su fecha
backup-manifest.json
```

**Garantías.**
- Copia byte a byte: sin recomprimir, convertir ni tocar metadatos. Cada archivo se copia a `*.pwpartial`, se relee y se compara su SHA-256 con el del origen antes de darle su nombre.
- La carpeta del mes sale de la fecha de captura local que ya usa la biblioteca; nunca de la hora de la copia.
- Nunca sobrescribe nada ni borra copias anteriores. Los originales solo se abren para leer.
- «Completada y verificada» solo si **todos** los archivos se copiaron sin ninguna incidencia. Si un original ha cambiado desde que se importó, o cambia mientras se copia, se informa y la copia no se da por completa.
- Si faltan originales, la copia no empieza. Puedes marcar «Copiar solo los archivos disponibles»; la carpeta termina en `-INCOMPLETA` y su manifiesto dice `"complete": false`.
- Antes de empezar se comprueba el espacio libre y el límite de 4 GB por archivo de FAT32 (usa exFAT o NTFS).
- Cancelar, desconectar el disco o quedarse sin espacio deja una carpeta `.partial`. «Reanudar copia sin terminar» vuelve a comprobar por SHA-256 lo ya copiado y sigue. Si la unidad cambió de letra, elige de nuevo el destino.
- «Verificar una copia existente…» relee todos los archivos y los compara con el manifiesto.
- El manifiesto no contiene rutas de tu equipo, coordenadas GPS, personas ni datos faciales.

## Copia de biblioteca (Ajustes › Copias de seguridad)
1. Elige una carpeta de destino, preferiblemente en otro disco.
2. Decide si incluir los **originales gestionados** (`originals/`). Recomendado si importas con «Copiar a la biblioteca».
3. La app crea `PhotosWindows-Backup-<biblioteca>-<fecha>.partial`, copia el catálogo con la API de backup de SQLite (copia consistente aunque la app esté en uso), `library.json` y, si se pidió, los originales; calcula SHA-256 de cada archivo y escribe `backup.json`.
4. **Verifica** la copia releyendo todo. Solo entonces renombra la carpeta quitando `.partial`. Una copia interrumpida nunca parece válida.
5. Las copias anteriores no se borran nunca automáticamente.

## Verificar una copia
«Verificar» recalcula los checksums y comprueba que el catálogo existe y que no procede de una versión más nueva de la app.

## Restaurar
- **Como biblioteca nueva** (recomendado): crea `Nombre (restored).pwlibrary` a partir de la copia verificada y la abre. La biblioteca actual no se modifica.
- **Solo el catálogo, en la biblioteca actual**: útil si el catálogo se dañó o quieres deshacer cambios de organización. Antes se guarda el catálogo actual en `backups/replaced-<fecha>.db`. Los originales no se tocan. Solo se permite con copias de la misma biblioteca.

## Copias automáticas
Antes de migrar el esquema de una biblioteca existente se guarda `backups/pre-migration-v<N>-<fecha>.db`.

## Reconstrucción
- **Miniaturas**: Ajustes › Almacenamiento › Vaciar caché. Se regeneran solas.
- **Índice de búsqueda**: Ajustes › Mantenimiento › Reconstruir índice.
- **Archivos movidos**: Ajustes › Mantenimiento › Verificar; después «Buscar en carpeta…» para volver a vincular (coincidencia por nombre, tamaño y SHA-256).
- **Copias gestionadas sin catalogar** (p. ej. apagón durante una importación): la verificación las lista como huérfanas; «Adoptar» las vuelve a catalogar sin copiarlas.
- **Importación interrumpida**: Importaciones › Reanudar.
