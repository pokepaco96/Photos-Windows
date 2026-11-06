# ADR-0003 · Estructura de biblioteca y modos de almacenamiento

**Contexto.** La biblioteca debe pertenecer al usuario, ser fácil de localizar y no obligar a duplicar originales.

**Decisión.** Una biblioteca es una carpeta `Nombre.pwlibrary/`:

```
Nombre.pwlibrary/
  library.json      identidad (id, nombre, formato, ubicación de caché opcional)
  catalog.db        catálogo SQLite (+ -wal / -shm mientras está abierta)
  originals/AAAA/MM/  copias gestionadas (modo «Copiar a la biblioteca»)
  cache/thumbs/     miniaturas regenerables (reubicable)
  backups/          copias automáticas previas a migraciones y a restauraciones
```

Dos modos por archivo (`StorageMode`):
- **Referenced** («Añadir sin mover»): el catálogo guarda la ruta absoluta; el archivo no se mueve ni se copia.
- **Managed** («Copiar a la biblioteca»): se copia a `originals/AAAA/MM/` (fecha de captura); la ruta se guarda relativa a la biblioteca, por lo que la carpeta completa puede moverse de disco.

Un archivo dentro de `originals/` importado «sin mover» se trata como gestionado (permite adoptar copias huérfanas).

**Alternativas.** Estructura opaca tipo paquete (dificulta encontrar originales); solo referencias (no permite consolidar tarjetas SD).

**Consecuencias.** Las rutas referenciadas pueden quedar no disponibles (disco externo); la verificación las marca y permite volver a vincularlas (`RelinkMissingAsync`). La caché puede borrarse sin pérdida.
