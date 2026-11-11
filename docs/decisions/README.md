# Architecture Decision Records

Formato breve: contexto, decisión, alternativas, consecuencias. Un ADR aceptado no se edita; si cambia la decisión se añade uno nuevo que lo reemplaza.

| ADR | Título | Estado |
|---|---|---|
| [0001](0001-platform-and-stack.md) | Plataforma: C#, .NET 10 LTS, WinUI 3, MVVM | Aceptado |
| [0002](0002-windows-app-sdk-version.md) | Windows App SDK 1.8 (y no 2.x) para la primera versión | Aceptado |
| [0003](0003-library-layout-and-storage-modes.md) | Estructura de biblioteca y modos de almacenamiento | Aceptado |
| [0004](0004-sqlite-catalog.md) | Catálogo SQLite con Dapper, FTS5 y migraciones | Aceptado |
| [0005](0005-imaging-pipeline.md) | Pipeline de imagen: SkiaSharp + decodificadores enchufables | Aceptado |
| [0006](0006-deletion-lifecycle.md) | Ciclo de borrado y protección de originales | Aceptado |
| [0007](0007-build-and-test-strategy.md) | Estrategia de compilación y pruebas (Linux + runner Windows) | Aceptado |
| [0008](0008-smart-features-deferred.md) | Personas, lugares y recuerdos: locales, opt-in y aplazados | Aceptado (Personas: ver 0010) |
| [0009](0009-packaging.md) | Empaquetado sin paquete y autocontenido | Aceptado |
| [0010](0010-people-local-face-grouping.md) | Personas: agrupación facial local con modelos descargados por el usuario | Aceptado (reemplaza la parte de Personas de 0008) |
| [0011](0011-places-offline-map.md) | Lugares: mapa offline propio (Natural Earth) y modo lista | Aceptado |
| [0012](0012-memories-deterministic-rules.md) | Recuerdos: reglas deterministas y explicables | Aceptado (reemplaza la parte de Recuerdos de 0008) |
| [0013](0013-portable-devices-import.md) | Dispositivos por USB (iPhone): explorar e importar; borrar solo verificado | Aceptado |
| [0014](0014-originals-backup.md) | Copia de originales en carpetas AÑO/MES, separada de la copia de biblioteca | Aceptado |
| [0015](0015-thumbnail-lanes.md) | Carriles de miniaturas interactivo y de fondo, con límite de tiempo por generación | Aceptado |
