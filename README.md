# Photos Windows

Gestor de fotos y vídeos **local y sin conexión** para Windows 11, escrito en C# / .NET 10 / WinUI 3.
Importa, cataloga, busca, organiza, edita sin destruir, exporta y protege tu colección. Sin cuentas, sin nube, sin telemetría y sin iCloud.

> «Photos Windows» es un nombre de trabajo. El proyecto no está afiliado a Apple y no utiliza su código, marcas ni recursos.

## Estado
En desarrollo activo. El núcleo (biblioteca, importación, catálogo, búsqueda, edición, exportación, copias) está implementado y probado; la interfaz WinUI 3 está en construcción. Consulta [`docs/PROGRESS.md`](docs/PROGRESS.md) y [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Para usuarios
- **Biblioteca**: una carpeta `Nombre.pwlibrary` que puedes ver en el Explorador. Contiene el catálogo (`catalog.db`), las copias que hagas con «Copiar a la biblioteca» (`originals/`), miniaturas regenerables (`cache/`) y copias automáticas (`backups/`).
- **Añadir sin mover** indexa tus fotos donde están. **Copiar a la biblioteca** las copia a `originals/AAAA/MM`. En ningún caso se modifican ni se borran tus archivos originales.
- **Todo es local**: ver [`docs/PRIVACY.md`](docs/PRIVACY.md).
- **Copias de seguridad**: ver [`docs/BACKUP_AND_RESTORE.md`](docs/BACKUP_AND_RESTORE.md).
- **Formatos**: ver [`docs/SUPPORTED_FORMATS.md`](docs/SUPPORTED_FORMATS.md). HEIC, AVIF y RAW usan las extensiones de imagen de Microsoft Store.

## Para desarrolladores
Requisitos: .NET SDK 10 (ver `global.json`). Para la app: Windows 11 con Visual Studio 2022/2026 y la carga de trabajo «Desarrollo de aplicaciones de Windows» (Windows App SDK).

```bash
dotnet build PhotosWindows.sln -c Release
dotnet test PhotosWindows.sln -c Release
```

Estructura:
```
src/PhotosWindows.Domain          entidades y reglas (sin dependencias)
src/PhotosWindows.Application     casos de uso y puertos
src/PhotosWindows.Infrastructure  SQLite, metadatos, ajustes, logging
src/PhotosWindows.Imaging         decodificación, edición y codificación (SkiaSharp)
tests/                            pruebas unitarias e integración (fixtures sintéticas)
docs/                             especificación, arquitectura, ADR, seguridad, privacidad…
```
Arquitectura: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). Decisiones: [`docs/decisions`](docs/decisions). Dependencias y licencias: [`docs/THIRD_PARTY_NOTICES.md`](docs/THIRD_PARTY_NOTICES.md).

## Licencia
Aún no decidida por el propietario del repositorio; hasta entonces no se concede licencia de uso del código.
