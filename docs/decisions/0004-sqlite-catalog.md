# ADR-0004 · Catálogo SQLite con Dapper, FTS5 y migraciones

**Decisión.**
- `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` (incluye FTS5 y JSON1; un test lo verifica).
- Dapper para mapear; SQL explícito. Listas de ids vía `json_each(@ids)` (sin límites de parámetros).
- `PRAGMA journal_mode=WAL` (lectores no bloquean a la importación), `foreign_keys=ON`, `busy_timeout=5000`, `synchronous=NORMAL`.
- Escrituras en transacciones `BEGIN IMMEDIATE` con hasta 3 reintentos ante `SQLITE_BUSY/LOCKED`.
- Migraciones en una lista *append-only* (`Migrations.cs`); cada una en su transacción junto a `PRAGMA user_version`. Antes de migrar una biblioteca existente se crea `backups/pre-migration-vN-*.db` con la API de backup de SQLite. Un catálogo de versión superior se rechaza sin modificarlo.
- Búsqueda: tabla `assets_fts` (FTS5, `unicode61 remove_diacritics 2`, prefijos 2-3) mantenida por la aplicación dentro de la misma transacción que modifica los datos indexados. El texto del usuario se convierte en términos entre comillas con prefijo, por lo que no puede inyectar sintaxis FTS.
- Fechas como texto ISO ordenable: `capture_local` (hora de pared del lugar de captura) para agrupar por día, más `capture_offset_min` cuando se conoce.

**Alternativas.** EF Core (más pesado, peor con FTS5), LiteDB (sin FTS de calidad).

**Consecuencias.** Esquema documentado en `DATA_MODEL.md`. `IntegrityCheck` ejecuta `integrity_check` y `foreign_key_check`.
