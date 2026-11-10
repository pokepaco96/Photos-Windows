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
