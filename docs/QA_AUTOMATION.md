# Automatización de QA de la interfaz (UI Automation)

Pruebas end-to-end que **usan la aplicación real** (`PhotosWindows.exe`) a través de Windows UI Automation, como lo haría una persona: clics, teclado, diálogos, selector de carpetas de Windows, visor, editor… No llaman a los servicios internos.

## Piezas

| Pieza | Dónde | Qué hace |
|---|---|---|
| Proyecto de pruebas | `tests/PhotosWindows.UIAutomation.Tests` | xUnit + [FlaUI](https://github.com/FlaUI/FlaUI) 5.0 (UIA3, licencia MIT). 65 escenarios ordenados: 28 generales (`EndToEndScenarios.cs`, el 28 —SHA-256 y cierre— siempre el último), 9 de Personas (`PeopleScenarios.cs`, S29–S37), 10 de Lugares (`PlacesScenarios.cs`, S40–S49), 7 de Recuerdos (`MemoriesScenarios.cs`, S50–S56), 6 de Copia de seguridad de originales (`BackupScenarios.cs`, S60–S65) y 5 de Dispositivos (`DevicesScenarios.cs`, S70–S74, con un dispositivo simulado sobre una carpeta: no es un iPhone real). |
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

| `people/` | 5 retratos de la NASA de dominio público (Mark Lee 1984 y 1993, Eugene Cernan 1964 y con Thomas Stafford 1966, Kathryn Thornton 1992), descargados con SHA-256 fijado; sin ellos los escenarios de Personas quedan bloqueados | Personas (S31–S36) |

HEIC y RAW reales no se generan: no hay codificador libre fiable en el runner y los runners Windows Server no incluyen las extensiones HEIF/RAW de Microsoft Store (ver limitaciones).

### Resultados honestos
- Cada escenario se registra como **passed**, **failed** o **blocked**. *Blocked* = no se ejecutó porque falló un requisito previo; también hace fallar el test (nunca cuenta como superado).
- En cada fallo: captura de pantalla, árbol UIA (`AutomationId | Name | ControlType | rect`), log y excepción.

## Ejecutar

### En GitHub Actions
*Actions › UI automation (Windows) › Run workflow*. Entrada `runner`: `"windows-latest"` (hospedado) o `["self-hosted","windows","ui"]`.
El resumen del job muestra la sonda de sesión (id de sesión, interactividad, resolución) y `report.md`. Las evidencias están en el artefacto `ui-automation-evidence`.

> GitHub solo acepta `workflow_dispatch` para workflows que están en la rama por defecto (si no, responde 404). Durante el desarrollo de la suite la rama `claude/ui-automation-qa` tuvo un disparador `push` temporal; ya se ha retirado. Hasta que el workflow llegue a la rama por defecto, para lanzarlo desde otra rama hay que añadir temporalmente ese disparador.

Comprobado en el runner hospedado `windows-latest` (Windows Server 2025 Datacenter, build 26100): **tiene escritorio interactivo** (sesión 2, `UserInteractive=True`), la resolución se puede fijar a 1920×1080 y Media Foundation reproduce H.264/AAC. No hace falta un runner propio para la suite; el self-hosted sigue siendo útil para Windows 11, DPI y extensiones de formatos.

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

## Resultados

Las ejecuciones son reales (escritorio de Windows Server 2025 en `windows-latest`, ventana 1400×900 en pantalla 1920×1080, 100 % DPI, `en-US`). Cada una está en el artefacto `ui-automation-evidence` de su ejecución del workflow; la #22 se conserva también en [`artifacts/qa/evidence/`](../artifacts/qa/evidence/). Las ejecuciones #18 y #19 repitieron código ya probado (#19 se canceló al llegar #20).

| Ejecución | Superados | Fallidos | Bloqueados | Qué cambió |
|---|---|---|---|---|
| #1, #3–#7 | 0 | 1 | 27 | La app publicada no arrancaba (bug 1). #2 se canceló por redundante. |
| #8 | 1 | 2 | 25 | Primer arranque real; el selector de carpetas de Windows no se encontraba (arnés) y el informe no se escribió (cifras del `run.log`). |
| #9 | 2 | 1 | 25 | Biblioteca creada con el selector real; bug 3 detectado en la captura. |
| #10 | 5 | 1 | 22 | Importación real; bugs 5 y 6. |
| #11 | 5 | 1 | 22 | Bug 7 (captura de Ajustes). |
| #12, #13 | 10 | 5 | 13 | Bugs 8–13. |
| #14, #15 | 24 | 4 | 0 | Bugs 14–16; revisión visual → bugs 17–18. #15 confirma 14 y 16. |
| #16 | 25 | 3 | 0 | Bugs 17–18 corregidos y comprobados. |
| #17 | 26 | 2 | 0 | Escape en el visor corregido salvo tras el zoom; la flecha izquierda tras un clic en la foto sigue fallando. |
| #20 | 27 | 1 | 0 | Flecha izquierda tras clic corregida; restauración con los mismos recuentos. |
| #21 | 27 | 1 | 0 | Escape tras el zoom corregido; S15 falla por el arnés (miniatura no realizada por la virtualización). |
| **#22** | **28** | **0** | **0** | **Todos los escenarios superados** (commit `f2f3315`). Evidencias en [`artifacts/qa/evidence/2026-11-07-run22`](../artifacts/qa/evidence/2026-11-07-run22/). |
| #23 (Personas) | 28 | 1 | 8 | S29: el id del diálogo de descarga estaba en un `StackPanel` sin par UIA (arnés/accesibilidad); S30–S37 bloqueados. |
| #24 (Personas) | 32 | 1 | 4 | S29–S31 y S37 superados; S32 contaba las tarjetas fuera de la página de Personas (arnés). |
| **#25 (Personas)** | **37** | **0** | **0** | **Los 28 anteriores y los 9 de Personas** (commit `93551f9`, rama `claude/smart-people`). Evidencias en [`artifacts/qa/evidence/2026-11-08-run25`](../artifacts/qa/evidence/2026-11-08-run25/). |
| #27–#29 (Lugares) | 31 → 37 | 6 → 1 | 1 → 0 | Fallos del arnés (la bandeja era un `Grid` sin par UIA; los hashes se perdían entre pasos). La captura de #28 mostró un fallo real: en 900×640 la bandeja tapaba los botones de zoom (corregido). |
| #30 (Lugares) | 38 | 0 | 0 | Los 28 base y los 10 de Lugares (`0434bbd`, rama antigua `claude/smart-places`). |
| **#31 (Personas, integrada)** | **37** | **0** | **0** | Personas reconstruida sobre el default (`fbe6516`), fusionada como #5. 22 archivos del dataset sin cambios. |
| **#32 (Lugares, integrada)** | **47** | **0** | **0** | 28 base + 9 Personas + 10 Lugares sobre `1b587c8`, fusionada como #6. Incluye el recentrado de la bandeja. 22 archivos del dataset y 7 de Lugares sin cambios; ninguna conexión TCP remota en el muestreo. |
| #33 (Recuerdos) | — | — | — | No llegó a ejecutar pasos: la app no compiló en Windows (`DispatcherQueueTimer` ambiguo). Corregido en `2ff3138`. |
| #34 (Recuerdos) | 54 | 0 | 0 | 28 base + 9 Personas + 10 Lugares + 7 Recuerdos sobre `2ff3138`. La revisión de capturas encontró un fallo real que las aserciones no cubrían: los botones inferiores de la segunda tarjeta salían cortados. |
| #35 (Recuerdos) | 54 | 0 | 0 | **Tarjetas de altura fija y nueva comprobación en S50** (`a7c6c72`). Sin cambios: 22 archivos del dataset, 7 de Lugares y 13 de Recuerdos (SHA-256). Ninguna conexión TCP remota en el muestreo. CI #75/#76 en verde sobre el mismo commit. |
| #36 (Recuerdos) | 54 | **0** | **0** | Sobre `dd04124` (el commit fusionado como #7, `400dfa3`; solo añade documentación a `a7c6c72`). Sin cambios: 22 archivos del dataset, 7 de Lugares y 13 de Recuerdos (SHA-256). Ninguna conexión TCP remota en el muestreo. CI #77/#78 en verde sobre el mismo commit; CI #79 en verde sobre el default tras la fusión. |
| #37, #45, #46 (Copia) | — | 1 | — | La interfaz se congelaba al reanudar una copia (S62). Las pilas de la app (ClrMD, #46) mostraron el hilo de UI dentro de `Application.Start`: un `ListView` anidado en el `ScrollViewer` de la página. Se sustituyó por `ItemsRepeater`. |
| #38, #42 | — | — | — | El proceso de la app desapareció en el runner sin error de la app; #39 pasó. Se añadió diagnóstico de cierre (código de salida y registro de eventos). |
| #41 (Dispositivos) | — | 1 | — | Arnés: buscaba la copia gestionada en la biblioteca equivocada tras S26. |
| #43 (Copia) | 60 | 0 | 0 | La revisión de capturas encontró dos fallos reales: el aviso no seguía el tema oscuro y al volver a la página se mostraba un resultado antiguo. Corregidos, con comprobación en S64/S65. |
| #44 (Dispositivos) | 59 | 0 | 0 | Sobre `feae4c3`. |
| #47 (Copia) | 59 | 1 | 0 | Sin congelación; el id `Backup_Problems` estaba en un `ItemsRepeater`, que no tiene par UIA (arnés). |
| **#48 (Copia, integrada)** | **60** | **0** | **0** | Sobre `88dbca6`, fusionada como #9 (`b00ed5c`). 22 archivos del dataset sin cambios. |
| #49 (Dispositivos + Copia) | 65 | 0 | 0 | Sobre `9030ba7`, fusionada como #10 (`4a7134c`). CI #106/#107 en verde. |
| #50, #51 (Importación) | — | — | — | Fallidas. El CI de los mismos commits mostraba que la app no compilaba en Windows (`Volatile` sobre un `struct` y luego una llave sobrante en `ActivityCenter`). |
| **#52 (Importación)** | **65** | **0** | **0** | Progreso del escaneo, avisos agrupados, cuadrícula progresiva y miniaturas más rápidas sobre `0fb7b06`, fusionado como #12 (`a3e3b51`). |
| #53 (Importación, lotes) | 64 | 1 | 0 | S11: la posición del vídeo no avanzó a tiempo, aunque la captura lo muestra reproduciéndose (fallo del runner). |
| **#54 (Importación, lotes, final)** | **65** | **0** | **0** | **Última ejecución completa**: repetición sobre el mismo `4f69b85`, fusionado como #13 (`afddee5`). CI #121 en verde sobre el default. |

### Errores encontrados y corregidos

Todos se encontraron usando la app real a través de la interfaz; cada corrección tiene una comprobación de regresión en la suite (o en CI) y se volvió a ejecutar el escenario.

| # | Error | Causa | Evidencia | Regresión |
|---|---|---|---|---|
| 1 | La app publicada se cerraba al arrancar (`XamlParseException` en `MainWindow`) | `dotnet publish` no copiaba `resources.pri` ni los `.xbf` compilados | `startup-smoke.txt` (#7) | CI arranca el ejecutable publicado y exige que siga vivo |
| 2 | Ventana fija de 1400×900 mayor que pantallas pequeñas | Tamaño fijo | Captura #8 | S01 comprueba que cabe en el área de trabajo |
| 3 | La barra lateral resaltaba «Mis álbumes» estando en «Todas las fotos» | Se leía el destino de la página antes de `OnNavigatedTo` | `02_empty_library` (#9) | S02/S03 comprueban el elemento seleccionado |
| 4 | Miniaturas y tarjetas de álbum sin nombre accesible | Sin `AutomationProperties` en los contenedores | Árbol UIA (#9) | Las búsquedas por `Tile_*`/`AlbumCard_*` lo exigen |
| 5 | Al terminar una importación la barra seguía con el texto de progreso («…about 0 min left») | Un `Progress<T>` creado fuera del hilo de UI entregaba el último informe después del resultado | `FAIL_04` (#10) | S04 lee el resumen final |
| 6 | Botones minimizar/maximizar/cerrar casi invisibles con tema oscuro y Windows en claro | El sistema los pinta con el tema del sistema | `24_library_dark` (#10) | S24 mide la luminancia de los botones |
| 7 | Ajustes: el valor de «Originals added without moving» quedaba cortado | `UniformGridLayout` da a todas las celdas el tamaño de la primera y la etiqueta se partía en dos líneas | `24_settings_dark` (#11) | S24 comprueba que las etiquetas ocupan una línea |
| 8 | Quitar una foto de un álbum la dejaba en pantalla y dejaba en blanco las miniaturas | La cuadrícula seguía enlazada al grupo anterior tras recargar | `FAIL_12` (#12) | S12 |
| 9 | Enter sobre una miniatura no abría el visor | El `GridViewItem` consume Enter antes de `KeyDown` | `FAIL_25` (#12) | S25 |
| 10 | En el visor la foto quedaba pegada arriba con una franja vacía debajo | La imagen ocupaba todo el escenario y `Stretch=Uniform` la dibujaba arriba | `FAIL_08` (#12) | S08 comprueba proporción y centrado |
| 11 | El panel de información y Ajustes anunciaban solo la etiqueta («Camera») y no el valor | `AutomationProperties.Name` = etiqueta | Árbol UIA (#12) | S08 busca «Camera: QA Labs QA Cam 1» |
| 12 | Al pasar el ratón por cualquier parte aparecía «Ctrl+Shift+I» | Atajos globales en la raíz con tooltip automático | Varias capturas (#12) | S25 comprueba que no aparece |
| 13 | «1 photos», «1 videos» | Una sola cadena para singular y plural | `FAIL_12` (#12) | S12 comprueba «1 photo · 0 videos» |
| 14 | Tras «Siguiente» la foto se dibujaba más grande que el visor | Se calculaba el tamaño con la foto anterior | `FAIL_09` (#14) | S09 comprueba que cabe y está centrada |
| 15 | En el visor las flechas y Escape no hacían nada en ciertos casos (tras un clic en la foto, al volver del editor, tras el zoom) | La página no podía recibir el foco (`Page` no es tab stop), el `ScrollViewer` consumía flechas y clics y un botón de la barra consumía Escape. Ahora la página toma el foco, las flechas se atienden en `PreviewKeyDown` y Escape es un `KeyboardAccelerator` | `FAIL_09/10/11/19` (#14–#21) | S09, S10, S11, S19 |
| 16 | Los vídeos no tenían miniatura en Windows Server | El shell no tiene generador de miniaturas MP4 aunque el vídeo se reproduce | `FAIL_11` (#14) | S11 mide el color de la miniatura |
| 17 | Editor: «Reset crop» cortado en el borde del panel | Fila horizontal sin ajuste | `17_editor_mono_exposure` (#14, revisión visual) | S17 comprueba que cabe |
| 18 | Tarjetas de álbum con un número suelto («1») en vez de «1 item(s)» | Se reutilizaba la cadena de los encabezados de fecha | `27_page_albums` (#14, revisión visual) | S12 |

Además se corrigieron fallos del propio arnés (no de la app): diálogo de carpetas como hijo de la ventana, elementos virtualizados o fuera de pantalla, lectura de nombres que cambian, teclas que no se soltaban (`Keyboard.Press`), miniaturas no realizadas por la virtualización, informe que no se escribía si fallaba el cierre.

### Revisión visual
Revisadas en las capturas reales de la ejecución #14 (y siguientes): bienvenida, biblioteca vacía y con fotos, importación (opciones, progreso, informe), duplicados, visor con información, vídeo, álbumes, álbum inteligente, ocultos, búsqueda con y sin resultados, editor, exportación, copia de seguridad, borrado (confirmación y plan), ajustes, temas claro y oscuro y la biblioteca restaurada. Las incidencias encontradas son los bugs 3, 6, 7, 10, 12, 17 y 18.

Observaciones sin corregir (decisiones de diseño o menores):
- La barra de resultado de una importación permanece abierta hasta cerrarla y reduce el espacio del visor; se podría cerrar sola tras unos segundos si fue correcta.
- El cuadro de búsqueda conserva la última consulta al cambiar de sección.
- La superficie del vídeo (`MediaPlayerElement`) no expone nombre propio a UIA; sí lo hacen sus controles (Seek, Play, Mute…).

## Limitaciones conocidas del entorno
Ver la sección de resultados en `docs/QA_CHECKLIST.md` y el informe de cada ejecución.
- **DPI 200 % y Narrador**: no automatizados (cambiar la escala exige reiniciar sesión; Narrador necesita audio). Pendientes de prueba manual.
- **HEIC/AVIF/RAW reales**: dependen de extensiones de Microsoft Store no disponibles en Windows Server.
- **Vídeo en Windows Server**: necesita la característica *Server Media Foundation* (el workflow intenta instalarla). En `windows-latest` (Server 2025) el vídeo H.264 se reproduce.
- **No automatizado todavía**: desconectar un disco externo, cerrar la app a mitad de una importación, instalación/desinstalación (la app es una carpeta portátil, ADR-0009), contraste alto.
