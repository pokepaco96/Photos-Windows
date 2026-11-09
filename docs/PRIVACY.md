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
| Personas: rostros (posición), descriptores numéricos (128 valores por rostro), grupos y nombres | Catálogo (`faces`, `people`, `face_analysis`) | No. Fuera de las copias de seguridad salvo que el usuario lo marque |
| Miniaturas de rostros | `cache/faces/` de la biblioteca | No |
| Modelos de rostros (YuNet, SFace) | `%LOCALAPPDATA%\PhotosWindows\models` | Se **descargan** solo si el usuario lo pide (ver abajo) |

## Funciones inteligentes
Todas locales y opt-in.
- **Fotos similares:** hash perceptual (dHash); no identifica personas ni contenido.
- **Personas** ([ADR-0010](decisions/0010-people-local-face-grouping.md)): desactivada hasta que el usuario la activa en una biblioteca tras un diálogo que explica qué se procesa, qué se guarda y cómo borrarlo. Detecta rostros y agrupa los parecidos **en el equipo**. Los grupos son sugerencias: los nombres solo los escribe el usuario; la app nunca deduce quién es alguien, no consulta contactos, redes ni servicios, y no infiere atributos (edad, sexo, origen, emociones…). Se puede excluir cualquier foto, corregir o borrar grupos y **borrar todos los datos de reconocimiento facial** (Ajustes › Personas), que se eliminan físicamente del catálogo (`secure_delete`, WAL vaciado, `VACUUM`) junto con las miniaturas de rostros.
- **Descarga de modelos:** la única conexión de red de la app. La inicia el usuario tras ver proveedor (réplica oficial de OpenCV Model Zoo en Hugging Face), tamaño y licencias. Es una petición HTTP GET de dos archivos con un agente genérico; no se envía ninguna foto, rostro, nombre, identificador ni dato de la biblioteca. Los archivos se comprueban con SHA-256 y después todo funciona sin conexión. También se pueden instalar desde archivos sin red.

## Lugares
- El mapa se dibuja con datos incluidos en la aplicación (Natural Earth, dominio público). **No se descargan teselas** ni se hace ninguna petición de red al abrir, mover o ampliar el mapa.
- **No hay geocodificación** (ni directa ni inversa): las coordenadas nunca salen del equipo y las zonas se muestran como coordenadas aproximadas, no como direcciones.
- Los filtros de Lugares y la preferencia de mapa se guardan en la configuración local del usuario; esa configuración no contiene coordenadas.
- «Quitar ubicación» solo modifica el catálogo; los archivos originales no se tocan (verificado por SHA-256 en las pruebas de UI).

## Registros
Los logs registran nombres de archivo y errores, no contenido de imágenes ni metadatos personales. No se incluyen miniaturas en ningún informe. El análisis de Personas solo registra recuentos y el tipo de error: nunca rostros, posiciones, descriptores ni nombres.
