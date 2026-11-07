# Evidencias de QA de la interfaz

Estructura de las evidencias que produce la suite `tests/PhotosWindows.UIAutomation.Tests` (ver [`docs/QA_AUTOMATION.md`](../../docs/QA_AUTOMATION.md)).

```
artifacts/qa/
├── README.md                 este archivo (versionado)
├── evidence/                 selección versionada de ejecuciones reales (solo datos sintéticos)
│   └── <fecha>-run<N>/       report.md, results.json, run.log y capturas clave de una ejecución
├── run/                      salida de la ejecución en CI (no versionada; se sube como artefacto)
└── <fecha>/                  salida de una ejecución local (no versionada)
```

Cada ejecución completa contiene:

| Carpeta / archivo | Contenido |
|---|---|
| `report.md` | Entorno (Windows, sesión, resolución, commit) y resultado de cada escenario: `passed`, `failed` o `blocked` (no ejecutado por un requisito previo; nunca cuenta como superado). |
| `results.json` | Lo mismo en formato máquina. |
| `screenshots/` | Capturas de pantalla reales de cada paso y de cada fallo. |
| `trees/` | Árbol de accesibilidad (UIA) en cada fallo: `ControlType | AutomationId | Name | clase | rectángulo`. |
| `logs/run.log` | Registro de la ejecución (clics, textos, mediciones, excepciones completas). |
| `logs/app-*.log` | Logs de la aplicación del perfil aislado. |
| `logs/startup-smoke.txt` | Prueba de arranque del ejecutable publicado y del de compilación. |
| `trx/` | Resultado de `dotnet test`. |
| `dataset-manifest.json` | SHA-256 de cada archivo del dataset sintético. |

Las ejecuciones completas de CI están en el artefacto `ui-automation-evidence` de cada ejecución del workflow *UI automation (Windows)*; GitHub las conserva 90 días. Por eso una selección queda versionada en `evidence/`.

**Privacidad:** todas las imágenes, vídeos y nombres son sintéticos (generados con semillas fijas por `QaDataset.cs`); no hay fotografías ni datos personales. Las rutas son las del runner (`D:\a\_temp\pwqa\...`).
