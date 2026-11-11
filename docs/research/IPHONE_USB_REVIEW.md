# Revisión: iPhone y otros dispositivos por USB

Fecha: 11-11-2026. Fuentes: documentación pública de Microsoft (WPD y `Windows.Devices.Portable`) y de Apple (transferir fotos a un PC; HEIF/HEVC y «Conservar originales»). **No se ha probado todavía con un iPhone físico**: todo lo que depende del hardware real queda marcado como pendiente.

## Cómo expone Windows un iPhone
- Con el iPhone conectado por USB, **desbloqueado** y tras aceptar **«Confiar en este ordenador»**, Windows lo ve como dispositivo portátil (WPD) que habla **PTP** (Picture Transfer Protocol). Apple indica instalar **Dispositivos Apple** (Apple Devices) de Microsoft Store en Windows para que el controlador esté presente.
- Bloqueado o sin confianza, el dispositivo aparece pero **no expone almacenamiento**. La app lo detecta así (raíz sin elementos) y lo explica.
- Por PTP solo se ven los elementos de la carpeta `DCIM` que iOS ofrece. Las fotos que están **solo en iCloud** (Fotos en iCloud con «Optimizar almacenamiento») no se pueden leer por USB.
- iOS puede **convertir** HEIF→JPEG y HEVC→H.264 al transferir, salvo que el usuario elija *Ajustes › Fotos › Transferir a Mac o PC › Conservar originales*. El texto del menú puede variar con la versión de iOS.

## API elegida
- **`Windows.Devices.Portable.StorageDevice`** (envoltorio WinRT de WPD, documentado y público). Da una `StorageFolder` por dispositivo MTP/PTP que se recorre con la API de almacenamiento normal: enumerar, propiedades básicas, miniatura y stream de lectura.
- Alternativa evaluada: las interfaces COM de WPD (`IPortableDevice`, `IPortableDeviceContent`). Exponen más propiedades y la operación `Delete`, pero exigen interop COM propio. Se deja como siguiente paso **solo si** las pruebas con un iPhone real muestran que hace falta (por ejemplo, para borrar).
- No se usan protocolos privados de Apple, no se instala nada en silencio y no se lee ni guarda ningún registro de confianza, código o cuenta.

## Borrado desde el dispositivo
WPD contempla borrar objetos, pero depende del dispositivo y del controlador. **No se ha verificado en un iPhone real**, así que el proveedor de Windows declara `CanDelete = false`: la opción «Importar y eliminar» aparece **desactivada con su explicación**. El coordinador ya tiene la lógica segura de borrado (copia verificada por SHA-256, captura completa, sin conversión, reidentificación del objeto, consentimiento para la selección exacta), probada con un dispositivo simulado. Activarla requiere:
1. Probar el borrado con WPD en uno o varios iPhone reales (modelo e iOS anotados).
2. Confirmar que se borra exactamente el objeto pedido y que un error no deja estados intermedios.
3. Cambiar `CanDelete` solo para la combinación verificada y documentarla aquí.

## Compatibilidad
| Dispositivo | iOS | Explorar | Importar | Borrar | Estado |
|---|---|---|---|---|---|
| iPhone (cualquiera) | — | sin probar | sin probar | desactivado | **Pendiente de prueba con hardware real** |
| Dispositivo simulado (carpeta, solo QA) | — | ✅ UI | ✅ UI | ✅ UI (solo simulado) | Pruebas automáticas |

## Prueba manual pendiente (puerta de aceptación)
Ver `QA_CHECKLIST.md`, filas 27–34: modelo/iOS, Dispositivos Apple instalado, desbloqueo y Confiar, número de elementos de DCIM, HEIC original con su firma, vídeo HEVC/MOV, Live Photo, importar solo, explorar sin importar, desconexión durante la copia, reimportar duplicados, iCloud con Optimizar almacenamiento, Conservar originales frente a conversión automática, y que el modo no destructivo deja el iPhone intacto.
