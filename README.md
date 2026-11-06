# Photos Windows

Gestor de fotos y vídeos **local y sin conexión** para Windows 11, escrito en C# / .NET 10 / WinUI 3.
Importa, cataloga, busca, organiza, edita sin destruir, exporta y protege tu colección. Sin cuentas, sin nube, sin telemetría y sin iCloud.

> «Photos Windows» es un nombre de trabajo. El proyecto no está afiliado a Apple y no utiliza su código, marcas ni recursos.

## Estado
En desarrollo activo. El núcleo (biblioteca, importación, catálogo, búsqueda, edición, exportación, copias, verificación) está implementado y cubierto por pruebas automáticas en Linux y Windows. La aplicación WinUI 3 se compila y publica en CI (`windows-latest`); la validación manual en un Windows interactivo sigue pendiente (ver [`docs/QA_CHECKLIST.md`](docs/QA_CHECKLIST.md)). Detalle en [`docs/PROGRESS.md`](docs/PROGRESS.md) y [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Probar la aplicación
1. Descarga el artefacto `PhotosWindows-win-x64` de la última ejecución correcta de GitHub Actions (pestaña *Actions* › *CI*).
2. Descomprímelo en una carpeta y ejecuta `PhotosWindows.exe` (Windows 10 1809+ / Windows 11, x64). No necesita instalar runtimes.
3. Crea una biblioteca, importa una carpeta y explora. Para HEIC/AVIF/RAW instala las extensiones gratuitas de Microsoft Store («Extensiones de imagen HEIF», «Extensión de vídeo AV1», «Extensión de imagen RAW»).

La versión no está firmada: Windows SmartScreen puede pedir confirmación.

## Para usuarios
- **Biblioteca**: una carpeta `Nombre.pwlibrary` que puedes ver en el Explorador. Contiene el catálogo (`catalog.db`), las copias que hagas con «Copiar a la biblioteca» (`originals/`), miniaturas regenerables (`cache/`) y copias automáticas (`backups/`).
- **Añadir sin mover** indexa tus fotos donde están. **Copiar a la biblioteca** las copia a `originals/AAAA/MM`. En ningún caso se modifican ni se borran tus archivos originales.
- **Todo es local**: ver [`docs/PRIVACY.md`](docs/PRIVACY.md).
- **Copias de seguridad**: ver [`docs/BACKUP_AND_RESTORE.md`](docs/BACKUP_AND_RESTORE.md).
- **Formatos**: ver [`docs/SUPPORTED_FORMATS.md`](docs/SUPPORTED_FORMATS.md). HEIC, AVIF y RAW usan las extensiones de imagen de Microsoft Store.
- **Atajos de teclado**: ver [`docs/KEYBOARD_SHORTCUTS.md`](docs/KEYBOARD_SHORTCUTS.md).

### Guía rápida
- **Importar**: botón «Importar…» de la barra lateral, `Ctrl+Mayús+I`, o arrastra carpetas/fotos a la ventana. Elige «Añadir sin mover» o «Copiar a la biblioteca». El progreso aparece arriba y se puede cancelar; una importación interrumpida se reanuda desde «Importaciones».
- **Ver**: la Fototeca agrupa por días, meses o años. Doble clic abre el visor (zoom, pantalla completa, información, comparar con el original).
- **Organizar**: selecciona y usa la barra de comandos o el clic derecho: favoritos, álbumes, palabras clave, ocultar, eliminar. «Mis álbumes» permite carpetas y álbumes inteligentes por reglas.
- **Buscar**: escribe palabras, un año (`2023`), un mes (`2023-07`), un día (`2023-07-14`) o un tipo (`vídeos`, `favoritos`).
- **Editar**: «Editar» en el visor. Estilos, recorte, enderezar, giro, luz, color y efectos con deshacer. El original nunca se toca.
- **Exportar**: original exacto o copia editada (JPEG/PNG/WebP), con o sin metadatos; la ubicación GPS solo si lo activas.
- **Eliminar**: primero va a «Eliminados recientemente». «Eliminar definitivamente» muestra antes qué archivos se borrarán (a la Papelera de reciclaje); los originales añadidos sin mover se conservan salvo que lo pidas.

## Para desarrolladores
Requisitos: .NET SDK 10 (ver `global.json`). Para la app: Windows 11 con Visual Studio 2022/2026 y la carga de trabajo «Desarrollo de aplicaciones de Windows» (Windows App SDK).

```bash
# Núcleo y pruebas (Windows, Linux o macOS)
dotnet test PhotosWindows.Core.slnf -c Release
# Aplicación (solo Windows)
dotnet build src/PhotosWindows.App/PhotosWindows.App.csproj -c Release -p:Platform=x64
dotnet publish src/PhotosWindows.App/PhotosWindows.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/PhotosWindows-win-x64
```
Pruebas de rendimiento del catálogo: `dotnet test PhotosWindows.Core.slnf --filter Category=Performance` (resultados en [`docs/PERFORMANCE.md`](docs/PERFORMANCE.md)).

Estructura:
```
src/PhotosWindows.Domain          entidades y reglas (sin dependencias)
src/PhotosWindows.Application     casos de uso y puertos
src/PhotosWindows.Infrastructure  SQLite, metadatos, ajustes, logging
src/PhotosWindows.Imaging         decodificación, edición y codificación (SkiaSharp)
src/PhotosWindows.Platform.Windows WIC (HEIF/AVIF/TIFF/RAW) y fotogramas de vídeo
src/PhotosWindows.App             aplicación WinUI 3 (MVVM)
tests/                            pruebas unitarias e integración (fixtures sintéticas)
docs/                             especificación, arquitectura, ADR, seguridad, privacidad…
```
Arquitectura: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). Decisiones: [`docs/decisions`](docs/decisions). Dependencias y licencias: [`docs/THIRD_PARTY_NOTICES.md`](docs/THIRD_PARTY_NOTICES.md).

## Licencia
Aún no decidida por el propietario del repositorio; hasta entonces no se concede licencia de uso del código.
