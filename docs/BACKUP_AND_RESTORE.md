# Copias de seguridad y restauración

> Una copia del **catálogo** no es una copia de tus **fotos**. Si usas «Añadir sin mover», tus originales siguen en sus carpetas: haz también copia de esas carpetas con tu herramienta habitual (Historial de archivos, disco externo…).

## Crear una copia (Ajustes › Copias de seguridad)
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
