# Ejecución #22 de *UI automation (Windows)* — 28/28

- Workflow: https://github.com/pokepaco96/Photos-Windows/actions/runs/42402678065 (artefacto completo `ui-automation-evidence`, conservado por GitHub hasta 2027-02-05).
- Commit: `f2f3315`. Windows Server 2025 Datacenter (build 26100), sesión interactiva 2, 1920×1080, 100 % DPI, `en-US`.
- Resultado: **28 superados, 0 fallidos, 0 bloqueados** ([`report.md`](report.md), [`results.json`](results.json)).

Contenido de esta copia (seleccionada para el repositorio):
- `report.md`, `results.json`, `dataset-manifest.json`: sin cambios. Los enlaces de `report.md` apuntan a los PNG del artefacto completo.
- `logs/run.log`: cada clic, texto, medición y resultado; `logs/app-*.log`: log de la app (perfil aislado); `logs/startup-smoke.txt`: arranque del ejecutable publicado.
- `screenshots/`: 22 capturas clave recortadas a la ventana de la app y en JPEG (las originales son PNG de pantalla completa en el artefacto).

Mediciones de las comprobaciones de regresión en `run.log`: foto del visor 782×522 centrada en 783×696 (y 1043×696 en 1123×696 tras «Siguiente»); posiciones 3 → 4 → 3 de 10 con el botón y la flecha izquierda; miniatura de vídeo con saturación máxima 255; «Reset crop» dentro del panel; luminancia de los botones de la ventana 32–227 (oscuro) y 30–243 (claro); etiquetas de almacenamiento de 16 px; recuentos antes de la copia y después de restaurar: 9 fotos, 1 vídeo, 2 álbumes.

Todos los datos son sintéticos (generados por `QaDataset.cs`).
