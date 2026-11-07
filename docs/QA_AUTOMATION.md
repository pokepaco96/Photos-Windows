# Automatización de QA de la interfaz (UI Automation)

Pruebas end-to-end que **usan la aplicación real** (`PhotosWindows.exe`) a través de Windows UI Automation, como lo haría una persona: clics, teclado, diálogos, selector de carpetas de Windows, visor, editor… No llaman a los servicios internos.

## Piezas

| Pieza | Dónde | Qué hace |
|---|---|---|
| Proyecto de pruebas | `tests/PhotosWindows.UIAutomation.Tests` | xUnit + [FlaUI](https://github.com/FlaUI/FlaUI) 5.0 (UIA3, licencia MIT). 28 escenarios ordenados (`EndToEndScenarios.cs`). |
| Sesión de app | `AppSession.cs` | Lanza el ejecutable con un perfil aislado, espera la ventana, busca controles por `AutomationId`/nombre en todas las ventanas del proceso (ventana principal, menús emergentes, diálogos), hace clics/teclado/texto, maneja el selector de carpetas de Windows, detecta cierres inesperados y el diálogo de error de la app, hace capturas y vuelca el árbol de accesibilidad. |
| Dataset | `QaDataset.cs` | Genera datos sintéticos reproducibles (semillas fijas) y un manifiesto SHA-256. |
| Evidencias | `Evidence.cs` | `screenshots/`, `trees/`, `logs/` (log de la ejecución + logs de la app), `results.json`, `report.md`. |
| Workflow | `.github/workflows/ui-automation.yml` | Manual (`workflow_dispatch`); publica la app, prepara el entorno, ejecuta la suite y sube las evidencias como artefacto `ui-automation-evidence`. |

### Identificadores de accesibilidad
Todos los controles interactivos tienen `AutomationProperties.AutomationId` (igual a su `x:Uid`), y cada miniatura expone `Tile_<nombre de archivo>` y un nombre accesible («Foto, fecha, archivo, favorita…»). Esto sirve también a lectores de pantalla; no es lógica de pruebas.

### Aislamiento y seguridad de datos
- Todo ocurre en una carpeta de trabajo nueva (`PW_QA_WORK`, por defecto `%TEMP%\pwqa-<fecha>`); el fixture se niega a usar una carpeta no vacía.
- La app se lanza con `PHOTOSWINDOWS_DATA_DIR=<trabajo>\profile`: ajustes, bibliotecas recientes y logs del usuario real no se tocan.
- Bibliotecas, exportaciones y copias se crean dentro de la carpeta de trabajo; `QaFixture.Inside()` rechaza cualquier ruta fuera de ella.
- El borrado definitivo solo se ejecuta sobre una copia gestionada de la biblioteca sintética, y se comprueba que el archivo de origen sigue intacto.
- Al final se recalcula el SHA-256 de **todos** los archivos del dataset (escenario 28).

### Dataset sintético
| Carpeta | Contenido | Uso |
|---|---|---|
| `copy/` | 5 JPEG con EXIF (fechas 2020-2024, zona horaria, cámara ficticia «QA Labs QA Cam 1»), 1 JPEG con GPS ficticio (Madrid), 1 PNG, nombre con caracteres especiales (`Fiesta ñandú #1 (copia).jpg`), par RAW+JPEG (`RAW_0100.CR2` simulado), duplicado exacto en subcarpeta | Importación «Copiar a la biblioteca» |
| `inplace/` | 2 JPEG (uno con GPS), duplicado de un archivo ya importado, `corrupto.jpg` (texto), `vacio.png` (0 bytes), `png-disfrazado.jpg` (PNG con extensión JPG), `notas.txt`, `clip.mp4` (H.264/AAC, 6 s, generado con ffmpeg) | Importación «Añadir sin mover» |

HEIC y RAW reales no se generan: no hay codificador libre fiable en el runner y los runners Windows Server no incluyen las extensiones HEIF/RAW de Microsoft Store (ver limitaciones).

### Resultados honestos
- Cada escenario se registra como **passed**, **failed** o **blocked**. *Blocked* = no se ejecutó porque falló un requisito previo; también hace fallar el test (nunca cuenta como superado).
- En cada fallo: captura de pantalla, árbol UIA (`AutomationId | Name | ControlType | rect`), log y excepción.

## Ejecutar

### En GitHub Actions
*Actions › UI automation (Windows) › Run workflow*. Entrada `runner`: `"windows-latest"` (hospedado) o `["self-hosted","windows","ui"]`.
El resumen del job muestra la sonda de sesión (id de sesión, interactividad, resolución) y `report.md`. Las evidencias están en el artefacto `ui-automation-evidence`.

> Mientras el workflow no esté en la rama por defecto, GitHub rechaza `workflow_dispatch` (404); por eso la rama `claude/ui-automation-qa` tiene temporalmente un disparador `push` limitado a esa rama.

### En un PC con Windows 10/11 (local)
```powershell
dotnet publish src/PhotosWindows.App/PhotosWindows.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/PhotosWindows-win-x64
winget install Gyan.FFmpeg   # opcional, para el vídeo de prueba
dotnet test tests/PhotosWindows.UIAutomation.Tests -c Release --logger "console;verbosity=normal"
# Evidencias: artifacts/qa/<fecha>/report.md
```
No uses el ratón ni el teclado durante la ejecución (≈5-10 min): las pruebas mueven el ratón y escriben.

### Runner self-hosted dedicado (recomendado para ejecuciones periódicas)
1. Windows 11 (VM o equipo dedicado), usuario local sin privilegios de administrador, p. ej. `qa-runner`.
2. Instalar .NET SDK 10, Git y (opcional) ffmpeg; extensiones de Microsoft Store «HEIF Image Extensions», «HEVC Video Extensions», «Raw Image Extension» para ampliar la cobertura de formatos.
3. Inicio de sesión automático de ese usuario (Sysinternals Autologon) y desactivar bloqueo de pantalla, protector de pantalla y suspensión (`powercfg /change standby-timeout-ac 0`, `powercfg /change monitor-timeout-ac 0`).
4. Instalar el runner de GitHub Actions **como aplicación interactiva, no como servicio** (`config.cmd` respondiendo *No* a «run as service») y lanzarlo al iniciar sesión (acceso directo de `run.cmd` en `shell:startup`). Un servicio corre en la sesión 0 sin escritorio y UI Automation no funcionaría.
5. Etiquetas: `self-hosted, windows, ui`. Resolución ≥ 1920×1080; para probar DPI, ajustar la escala en *Configuración › Pantalla* (125 %, 150 %, 200 %) entre ejecuciones.
6. Si la máquina se usa por RDP, no minimizar ni desconectar la sesión RDP durante las pruebas (el escritorio se bloquea); usar `tscon` para devolver la sesión a la consola antes de desconectar.

## Limitaciones conocidas del entorno
Ver la sección de resultados en `docs/QA_CHECKLIST.md` y el informe de cada ejecución.
- **DPI 200 % y Narrador**: no automatizados (cambiar la escala exige reiniciar sesión; Narrador necesita audio). Pendientes de prueba manual.
- **HEIC/AVIF/RAW reales**: dependen de extensiones de Microsoft Store no disponibles en Windows Server.
- **Vídeo en Windows Server**: necesita la característica *Server Media Foundation* (el workflow intenta instalarla).
