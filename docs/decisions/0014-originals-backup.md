# ADR-0014 · Copia de originales en carpetas AÑO/MES, separada de la copia de biblioteca

**Contexto.** La copia de biblioteca existente (`BackupService`) guarda catálogo y, opcionalmente, los originales gestionados. No incluye los archivos «añadidos sin mover», que pueden ser la mayoría. Se pide una copia de **todos** los originales, sin transformar, en carpetas `AÑO/MES`.

**Decisión.**
- Servicio nuevo `OriginalsBackupService`; `BackupService` y su formato no cambian.
- Origen: una sola consulta al catálogo con todos los archivos registrados (`Original`, `RawPair`, `LiveVideo`, `Sidecar`) de todos los activos que siguen en él, incluidos ocultos y en «Eliminados recientemente».
- Destino: `AÑO/MES` según `capture_local` y `date_source` del catálogo. Si la fecha es desconocida, `Sin fecha/00`. Los acompañantes van con su activo y comparten sufijo ` (n)` si hay colisión. Los nombres se adaptan a Windows y nunca escapan de la carpeta.
- Copia byte a byte a `*.pwpartial`, relectura y SHA-256, comprobación de cambios en origen (tamaño y fecha antes y después; hash del catálogo) y renombrado. La carpeta pasa de `.partial` a su nombre final solo con cero incidencias.
- Manifiesto `backup-manifest.json` versionado, sin rutas absolutas, GPS ni datos biométricos.
- Si faltan originales, no se empieza salvo que el usuario acepte una exportación incompleta, que se nombra y marca como tal.
- Una ruta física registrada dos veces se copia dos veces (una por activo), sin sobrescribir; queda reflejado en el manifiesto.

**Consecuencias.** La copia es una carpeta normal, útil fuera de la app; no restaura álbumes ni ediciones (para eso, la copia de biblioteca). Reanudar es seguro porque todo lo ya copiado se vuelve a verificar.
