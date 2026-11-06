# ADR-0006 · Ciclo de borrado y protección de originales

**Decisión.**
1. **Ocultar**: solo se retira de las vistas normales (sección «Ocultos»).
2. **Mover a Eliminados recientemente**: papelera lógica; recuperable. No hay purga automática: el usuario vacía la papelera explícitamente (se muestra la antigüedad de cada elemento). Se prioriza no perder nada sobre imitar la purga a 30 días.
3. **Eliminar definitivamente**: solo elementos que ya están en la papelera. Primero se genera un `DeletionPlan` que se muestra al usuario (nº de elementos, archivos físicos, bytes, rutas). Copias gestionadas: se eliminan. Originales referenciados: **se conservan** salvo que el usuario marque explícitamente esa opción para ese lote. En Windows los archivos van a la **Papelera de reciclaje** (recuperables desde el Explorador).
4. Defensa en profundidad: una copia gestionada solo se borra si su ruta resuelta está dentro de `originals/`.
5. Duplicados: resolver un grupo solo mueve a «Eliminados recientemente»; nunca borra.
6. Importación, indexación, miniaturas y edición **solo leen** originales. Las copias se escriben en `*.pwpartial`, se vuelcan a disco y se renombran sin sobrescribir.

**Consecuencias.** Más pasos para liberar espacio, a cambio de que ningún error de software o de usuario borre originales sin confirmación informada.
