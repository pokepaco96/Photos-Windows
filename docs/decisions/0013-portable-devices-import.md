# ADR-0013 · Dispositivos por USB (iPhone): explorar e importar; borrar solo verificado

**Contexto.** Se pide conectar un iPhone por USB, explorar su contenido, importar conservándolo en el teléfono y, si es seguro, importar y eliminarlo del teléfono. La importación existente garantiza que nunca borra ni modifica su origen. Detalle técnico en [`research/IPHONE_USB_REVIEW.md`](../research/IPHONE_USB_REVIEW.md).

**Decisión.**
- Puerto `IPortableMediaDeviceProvider` / `IPortableDeviceSession` en Application, sin tipos COM ni WinRT. Implementación Windows con `Windows.Devices.Portable` (WPD, MTP/PTP). Sin soporte de plataforma, un proveedor vacío.
- **Explorar** lee del dispositivo (lista, miniaturas, vista previa) sin escribir en la biblioteca ni en el dispositivo. Al desconectar, lo no importado deja de estar disponible.
- **Importar**: `PhoneImportCoordinator` copia cada elemento a una carpeta de *staging* privada fuera de la biblioteca, ejecuta la importación normal en modo copia (la garantía de `ImportService` no cambia) y verifica cada elemento contra un archivo gestionado releído del disco (SHA-256). Los componentes de una misma captura (foto + clip de Live Photo + RAW) viajan juntos. El *staging* se borra al terminar.
- **Importar y eliminar** solo con: sesión que declare borrado verificado, consentimiento para el número exacto de elementos, captura completa verificada, formato recibido coherente con el declarado (si iOS lo convirtió, se conserva en el teléfono), y el objeto vuelto a consultar por id con el mismo nombre, carpeta, tamaño y fecha. Cancelar o perder el dispositivo detiene el borrado al instante.
- Mientras no se pruebe en un iPhone real, el proveedor Windows **no borra** (`CanDelete = false`) y la UI lo explica.
- Proveedor simulado por carpeta solo para QA (variable `PW_QA_SIMULATED_DEVICE`), etiquetado como simulado.

**Consecuencias.** Funciona sin nube ni cuentas. La importación desde iPhone produce activos gestionados normales, que entran en Lugares, Recuerdos y la copia de originales. El soporte real de iPhone queda **sin declarar** hasta la prueba manual.
