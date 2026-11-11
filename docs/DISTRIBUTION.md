# Distribución: estado y plan de MSIX y firma

**Estado actual (verificado en CI):** la CI publica la carpeta autocontenida `PhotosWindows-win-x64` (ADR-0009). No hay MSIX ni firma: **no se ha producido ningún paquete firmado** y la app no se ha probado en un Windows 11 limpio.

## Qué falta para una versión pública

| Paso | Estado | Notas |
|---|---|---|
| Licencia del proyecto | Pendiente (decisión del propietario) | Necesaria antes de distribuir. Los avisos de terceros están en `THIRD_PARTY_NOTICES.md`. |
| Certificado de firma de código | Pendiente | Opciones: Microsoft Store (firma la Store), Azure Trusted Signing o un certificado OV/EV. No se guardan claves en el repositorio ni en la CI sin un secreto configurado por el propietario. |
| Proyecto MSIX (`WindowsPackageType=MSIX`) | No iniciado | Da instalación y desinstalación limpias e identidad de paquete. Revisar al hacerlo: las rutas de datos (`%LOCALAPPDATA%`) se virtualizan en MSIX. Hay que comprobar que bibliotecas, copias y modelos de Personas siguen en rutas visibles para el usuario. |
| Capacidad para dispositivos portátiles | Por validar | Con identidad de paquete, el acceso WPD/`StorageDevice` puede requerir declarar `removableStorage`. Hay que probarlo con un iPhone real. |
| Firma en CI | No iniciado | `signtool sign /fd SHA256 /tr <TSA> /td SHA256` sobre el `.msix` y el `.exe`, con el certificado como secreto del repositorio. |
| Prueba en Windows 11 limpio | Pendiente (manual) | Instalar, abrir biblioteca, importar, desinstalar; comprobar que la desinstalación no borra bibliotecas ni copias. |
| SmartScreen | Pendiente | Una carpeta o un MSIX sin firmar muestra avisos. Sin firma no se debe publicar como versión estable. |

Ninguno de estos pasos se puede marcar como hecho desde la CI ni desde Windows Server: requieren una decisión o una prueba manual del propietario.
