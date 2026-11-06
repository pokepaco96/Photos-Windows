# ADR-0007 · Estrategia de compilación y pruebas

**Contexto.** El desarrollo asistido se ejecuta en un contenedor Linux; WinUI 3 necesita el compilador XAML de Windows.

**Decisión.**
- Núcleo `net10.0` + tests (xUnit) se compilan y ejecutan en Linux y Windows. `TreatWarningsAsErrors` activado.
- `PhotosWindows.Platform.Windows` (`net10.0-windows10.0.19041.0`, sin XAML) compila también en Linux gracias a `EnableWindowsTargeting`.
- `PhotosWindows.App` (WinUI 3) solo se compila en el job `windows-latest` de GitHub Actions. La solución completa (`PhotosWindows.sln`) se compila en Windows; en Linux se usa el filtro `PhotosWindows.Core.slnf`.
- Pruebas de integración con pila real (SQLite + archivos) en carpetas temporales, con imágenes y vídeos sintéticos generados en tiempo de ejecución (sin fotos personales ni binarios con licencia dudosa en el repositorio).
- Lo que requiere un Windows interactivo (arranque de la UI, reproducción de vídeo, códecs HEIF/RAW instalados, lector de pantalla) se documenta en `QA_CHECKLIST.md` y no se marca como probado hasta ejecutarse.
