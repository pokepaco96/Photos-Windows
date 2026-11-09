# Privacidad

**Photos Windows no envía nada a Internet.** No hay cuentas, telemetría, analítica, sincronización en la nube ni servicios de IA remotos. No hay integración con iCloud ni con ningún servicio de Apple.

| Dato | Dónde se guarda | Sale del equipo |
|---|---|---|
| Fotos y vídeos originales | Donde estaban (Añadir sin mover) o en `originals/` de la biblioteca | No |
| Catálogo (metadatos, álbumes, palabras clave, recetas de edición) | `catalog.db` en la carpeta de la biblioteca | No |
| Miniaturas | `cache/` (reubicable y borrable) | No |
| Ajustes de la app y bibliotecas recientes | `%LOCALAPPDATA%\PhotosWindows\settings.json` | No |
| Registros de diagnóstico | `%LOCALAPPDATA%\PhotosWindows\logs` (7 días) | No (el usuario puede abrirlos y compartirlos manualmente) |
| Coordenadas GPS | Catálogo | No. Al exportar, la ubicación **no** se incluye salvo que el usuario lo active |

## Funciones inteligentes
Todas locales y opt-in. Actualmente solo existe el hash perceptual (dHash) para sugerir fotos similares; no identifica personas ni contenido. El reconocimiento facial (futuro) se activará explícitamente, procesará en el dispositivo y tendrá un botón para borrar todos los datos derivados.

## Lugares
- El mapa se dibuja con datos incluidos en la aplicación (Natural Earth, dominio público). **No se descargan teselas** ni se hace ninguna petición de red al abrir, mover o ampliar el mapa.
- **No hay geocodificación** (ni directa ni inversa): las coordenadas nunca salen del equipo y las zonas se muestran como coordenadas aproximadas, no como direcciones.
- Los filtros de Lugares y la preferencia de mapa se guardan en la configuración local del usuario; esa configuración no contiene coordenadas.
- «Quitar ubicación» solo modifica el catálogo; los archivos originales no se tocan (verificado por SHA-256 en las pruebas de UI).

## Registros
Los logs registran nombres de archivo y errores, no contenido de imágenes ni metadatos personales. No se incluyen miniaturas en ningún informe.
