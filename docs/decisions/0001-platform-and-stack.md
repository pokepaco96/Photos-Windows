# ADR-0001 · Plataforma: C#, .NET 10 LTS, WinUI 3, MVVM

**Contexto.** Se pide una aplicación de escritorio nativa para Windows 11 (x64 primero, ARM64 después), mantenible y probada.

**Decisión.**
- C# con .NET 10 (LTS publicado en noviembre de 2025; SDK fijado con `global.json`, `rollForward: latestFeature`, sin previews).
- UI con WinUI 3 (Windows App SDK) y patrón MVVM con CommunityToolkit.Mvvm.
- DI y logging con Microsoft.Extensions.*.
- El núcleo (`Domain`, `Application`, `Infrastructure`, `Imaging`) es `net10.0` puro, sin dependencias de Windows: se compila y se prueba en cualquier SO. Lo específico de Windows vive en `PhotosWindows.Platform.Windows` (WIC, Media Foundation, papelera) y `PhotosWindows.App` (UI).

**Alternativas.** WPF (madura pero sin el estilo Fluent moderno por defecto), Avalonia/MAUI (multiplataforma, no requerido y con menos integración nativa), Electron/web (excluido por el prompt).

**Consecuencias.** WinUI 3 solo compila en Windows (compilador XAML). Se compensa con CI en `windows-latest` (ADR-0007). La arquitectura por capas permite cambiar la UI sin tocar la lógica.
