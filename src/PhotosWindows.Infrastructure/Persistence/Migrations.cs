namespace PhotosWindows.Infrastructure.Persistence;

/// <summary>
/// Ordered, append-only list of schema migrations. Never edit a released migration: add a new one.
/// Each migration runs in its own transaction together with the <c>user_version</c> bump.
/// </summary>
internal static class Migrations
{
    public static IReadOnlyList<(int Version, string Description, string Sql)> All { get; } =
    [
        (1, "Initial catalog", V1),
    ];

    public static int Latest => All[^1].Version;

    private const string V1 = """
        CREATE TABLE library_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE schema_migrations (
            version     INTEGER PRIMARY KEY,
            description TEXT NOT NULL,
            applied_utc TEXT NOT NULL
        );

        CREATE TABLE assets (
            id                INTEGER PRIMARY KEY,
            uid               TEXT    NOT NULL UNIQUE,
            media_type        INTEGER NOT NULL,
            capture_local     TEXT    NOT NULL,
            capture_offset_min INTEGER NULL,
            date_source       INTEGER NOT NULL,
            imported_utc      TEXT    NOT NULL,
            file_modified_utc TEXT    NOT NULL,
            pixel_width       INTEGER NOT NULL DEFAULT 0,
            pixel_height      INTEGER NOT NULL DEFAULT 0,
            orientation       INTEGER NOT NULL DEFAULT 1,
            duration_ms       INTEGER NULL,
            file_name         TEXT    NOT NULL,
            format            TEXT    NOT NULL,
            file_size         INTEGER NOT NULL,
            camera_make       TEXT NULL,
            camera_model      TEXT NULL,
            lens              TEXT NULL,
            f_number          REAL NULL,
            exposure_s        REAL NULL,
            iso               INTEGER NULL,
            focal_mm          REAL NULL,
            latitude          REAL NULL,
            longitude         REAL NULL,
            altitude          REAL NULL,
            title             TEXT NULL,
            description       TEXT NULL,
            rating            INTEGER NOT NULL DEFAULT 0,
            is_favorite       INTEGER NOT NULL DEFAULT 0,
            is_hidden         INTEGER NOT NULL DEFAULT 0,
            deleted_utc       TEXT NULL,
            edit_version      INTEGER NOT NULL DEFAULT 0,
            has_live_video    INTEGER NOT NULL DEFAULT 0,
            has_raw_pair      INTEGER NOT NULL DEFAULT 0,
            thumb_state       INTEGER NOT NULL DEFAULT 0,
            original_available INTEGER NOT NULL DEFAULT 1,
            phash             INTEGER NULL,
            metadata_json     TEXT NULL
        );
        CREATE INDEX ix_assets_capture   ON assets(capture_local);
        CREATE INDEX ix_assets_imported  ON assets(imported_utc);
        CREATE INDEX ix_assets_type      ON assets(media_type, capture_local);
        CREATE INDEX ix_assets_visible   ON assets(is_hidden, deleted_utc, capture_local);
        CREATE INDEX ix_assets_favorite  ON assets(is_favorite) WHERE is_favorite = 1;
        CREATE INDEX ix_assets_thumb     ON assets(thumb_state);
        CREATE INDEX ix_assets_location  ON assets(latitude, longitude) WHERE latitude IS NOT NULL;
        CREATE INDEX ix_assets_camera    ON assets(camera_model);
        CREATE INDEX ix_assets_deleted   ON assets(deleted_utc) WHERE deleted_utc IS NOT NULL;

        CREATE TABLE asset_files (
            id                INTEGER PRIMARY KEY,
            asset_id          INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            role              INTEGER NOT NULL,
            storage_mode      INTEGER NOT NULL,
            stored_path       TEXT    NOT NULL,
            stored_path_key   TEXT    NOT NULL UNIQUE,
            size_bytes        INTEGER NOT NULL,
            sha256            TEXT    NOT NULL,
            file_modified_utc TEXT    NOT NULL,
            format            TEXT    NOT NULL,
            is_available      INTEGER NOT NULL DEFAULT 1,
            last_verified_utc TEXT    NOT NULL
        );
        CREATE INDEX ix_files_asset ON asset_files(asset_id, role);
        CREATE INDEX ix_files_hash  ON asset_files(sha256);

        CREATE TABLE keywords (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE
        );

        CREATE TABLE asset_keywords (
            asset_id   INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            keyword_id INTEGER NOT NULL REFERENCES keywords(id) ON DELETE CASCADE,
            PRIMARY KEY (asset_id, keyword_id)
        ) WITHOUT ROWID;
        CREATE INDEX ix_asset_keywords_keyword ON asset_keywords(keyword_id);

        CREATE TABLE album_folders (
            id         INTEGER PRIMARY KEY,
            name       TEXT NOT NULL,
            parent_id  INTEGER NULL REFERENCES album_folders(id) ON DELETE SET NULL,
            sort_order INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE albums (
            id          INTEGER PRIMARY KEY,
            uid         TEXT NOT NULL UNIQUE,
            name        TEXT NOT NULL,
            kind        INTEGER NOT NULL,
            folder_id   INTEGER NULL REFERENCES album_folders(id) ON DELETE SET NULL,
            smart_json  TEXT NULL,
            sort_order  INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT NOT NULL
        );
        CREATE INDEX ix_albums_folder ON albums(folder_id);

        CREATE TABLE album_assets (
            album_id  INTEGER NOT NULL REFERENCES albums(id) ON DELETE CASCADE,
            asset_id  INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            position  INTEGER NOT NULL,
            added_utc TEXT NOT NULL,
            PRIMARY KEY (album_id, asset_id)
        ) WITHOUT ROWID;
        CREATE INDEX ix_album_assets_order ON album_assets(album_id, position);
        CREATE INDEX ix_album_assets_asset ON album_assets(asset_id);

        CREATE TABLE edit_recipes (
            asset_id    INTEGER PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
            recipe_json TEXT NOT NULL,
            version     INTEGER NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE edit_history (
            id          INTEGER PRIMARY KEY,
            asset_id    INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            recipe_json TEXT NOT NULL,
            version     INTEGER NOT NULL,
            created_utc TEXT NOT NULL
        );
        CREATE INDEX ix_edit_history_asset ON edit_history(asset_id, version);

        CREATE TABLE import_jobs (
            id                INTEGER PRIMARY KEY,
            uid               TEXT NOT NULL UNIQUE,
            mode              INTEGER NOT NULL,
            status            INTEGER NOT NULL,
            import_duplicates INTEGER NOT NULL DEFAULT 0,
            started_utc       TEXT NOT NULL,
            finished_utc      TEXT NULL,
            total             INTEGER NOT NULL DEFAULT 0,
            processed         INTEGER NOT NULL DEFAULT 0,
            imported          INTEGER NOT NULL DEFAULT 0,
            duplicates        INTEGER NOT NULL DEFAULT 0,
            skipped           INTEGER NOT NULL DEFAULT 0,
            failed            INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE import_items (
            id          INTEGER PRIMARY KEY,
            job_id      INTEGER NOT NULL REFERENCES import_jobs(id) ON DELETE CASCADE,
            source_path TEXT NOT NULL,
            status      INTEGER NOT NULL,
            asset_id    INTEGER NULL REFERENCES assets(id) ON DELETE SET NULL,
            message     TEXT NULL,
            target_path TEXT NULL
        );
        CREATE INDEX ix_import_items_job ON import_items(job_id, status);
        CREATE INDEX ix_import_items_asset ON import_items(asset_id);

        CREATE TABLE duplicate_dismissals (
            asset_a INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            asset_b INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            PRIMARY KEY (asset_a, asset_b),
            CHECK (asset_a < asset_b)
        ) WITHOUT ROWID;
        CREATE INDEX ix_duplicate_dismissals_b ON duplicate_dismissals(asset_b);

        CREATE VIRTUAL TABLE assets_fts USING fts5(
            file_name, title, description, keywords, camera, lens, albums, format,
            tokenize = 'unicode61 remove_diacritics 2',
            prefix = '2 3'
        );
        """;
}
