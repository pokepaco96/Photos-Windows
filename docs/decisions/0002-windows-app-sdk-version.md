# ADR-0002 · Windows App SDK 1.8 para la primera versión

**Contexto.** En NuGet existen dos líneas estables: 1.8.x (última: 1.8.260921001, septiembre 2026, con mantenimiento activo) y 2.x (última: 2.6.5). El equipo de desarrollo no puede compilar WinUI localmente (contenedor Linux) y depende de la CI de Windows para validar la UI.

**Decisión.** Fijar `Microsoft.WindowsAppSDK` 1.8.260921001 (línea estable con parches de servicio) para la primera versión.

**Motivos.** Las API de 1.8 están ampliamente documentadas y probadas; minimiza el riesgo de errores de compilación que solo se detectan en CI. 2.x introduce cambios de empaquetado que conviene evaluar con calma.

**Consecuencias.** Se registra en `ROADMAP.md` la tarea de evaluar la migración a 2.x tras el primer release candidate, con su propia rama y validación en CI. La versión está centralizada en `Directory.Packages.props`.
