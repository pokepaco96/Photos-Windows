# Ejecución #25 de *UI automation (Windows)* — 37/37 (incluye Personas)

- Workflow: https://github.com/pokepaco96/Photos-Windows/actions/runs/42469755937 (artefacto completo `ui-automation-evidence`, conservado por GitHub hasta 2027-02-06).
- Commit: `93551f9` (rama `claude/smart-people`). Windows Server 2025 (`windows-latest`), sesión interactiva, 1920×1080, 100 % DPI, `en-US`.
- Resultado: **37 superados, 0 fallidos, 0 bloqueados**: los 28 escenarios anteriores y los 9 de Personas (S29–S37).

Personas, en `logs/run.log`: descarga real de los modelos (verificados); consentimiento; análisis **cancelado de verdad** («Cancelled. Work done so far has been kept.») y reanudado: 14 fotos analizadas, 6 rostros, 2 grupos sin nombre y 2 rostros sin agrupar; nombres puestos por la interfaz; separar y fusionar; excluir e incluir una foto; persistencia tras reiniciar; borrado total con la biblioteca intacta; SHA-256 de los 22 archivos del dataset sin cambios.

Las fotos de Personas son retratos de la NASA de dominio público (ver `QaDataset.PeoplePhotos`). Las capturas de esta carpeta están recortadas a la ventana y en JPEG.
