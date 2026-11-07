# ADR-0010 · Personas: detección y agrupación facial local, con modelos descargados por el usuario

Reemplaza la parte «Personas y mascotas» de [ADR-0008](0008-smart-features-deferred.md) (Lugares y Recuerdos siguen como allí).

## Contexto
La misión pide agrupar rostros localmente para organizar la biblioteca privada, sin servicios remotos ni nombres inferidos. ADR-0008 lo aplazaba hasta encontrar un modelo con licencia de pesos verificable. La revisión ([research/FACE_MODELS_REVIEW.md](../research/FACE_MODELS_REVIEW.md)) encontró YuNet (MIT) y SFace (Apache-2.0) en OpenCV Model Zoo; los modelos de InsightFace quedan excluidos (solo investigación no comercial). Los datos de entrenamiento de YuNet/SFace tienen restricciones o procedencia dudosa.

## Decisión
1. **Motor:** ONNX Runtime (MIT), CPU, en un proyecto propio `PhotosWindows.MachineLearning`. `Domain` y `Application` solo conocen los puertos `IFaceDetector`, `IFaceEmbedder`, `IFaceModelManager`, `IPeopleRepository`.
2. **Modelos no empaquetados.** El usuario los descarga expresamente (Ajustes › Personas) desde URLs fijadas a un commit de la réplica oficial en Hugging Face, o los instala desde archivos. Cada archivo se comprueba con un SHA-256 fijado en el código; uno que no coincide nunca se carga. Se guardan en `%LOCALAPPDATA%\PhotosWindows\models` y se pueden quitar.
3. **Opt-in por biblioteca** con un diálogo de consentimiento que explica qué se procesa, qué se guarda y cómo borrarlo. Sin modelos o sin consentimiento la sección no aparece.
4. **Datos:** migración v2 del catálogo (aditiva): `people`, `faces` (caja normalizada a la foto orientada, puntuación, descriptor de 128 `float32` en BLOB, versión de modelo, estado de revisión), `face_analysis` (por foto y versión de modelo) y `face_exclusions`. Los originales no se tocan; se decodifica a 1600 px.
5. **Agrupación determinista** por similitud coseno con umbrales más estrictos que el de verificación del modelo (unir a un grupo ≥ 0,42; nuevo grupo ≥ 0,45, mínimo 2 rostros). Solo se mueven rostros *sugeridos* y sin grupo: nombrar, mover, separar e ignorar son decisiones del usuario que nunca se deshacen automáticamente. Un grupo nuevo es «Persona sin identificar».
6. **Cambio de modelo:** un resultado con otra versión de modelo cuenta como pendiente; al reanalizar, un rostro nuevo que coincide en posición (IoU ≥ 0,5) con uno anterior conserva su persona y su estado.
7. **Borrado:** «Borrar todos los datos de reconocimiento facial» elimina rostros, descriptores, grupos, nombres y resultados con `secure_delete`, vacía el WAL y hace `VACUUM`; borra las miniaturas de rostros y desactiva la función. Las exclusiones (decisión del usuario) se conservan.
8. **Copias de seguridad:** sin datos faciales salvo que el usuario lo marque; `backup.json` registra `IncludesFaceData`.
9. **Mascotas:** no hay modelo de identidad de mascotas con licencia verificable; no se implementa reconocimiento individual. La detección por especie (YOLOX-s, Apache-2.0) queda para un hito posterior.

## Alternativas descartadas
- Empaquetar los modelos: riesgo jurídico por los datos de entrenamiento.
- InsightFace (ArcFace, SCRFD, RetinaFace): licencia no comercial.
- `Windows.Media.FaceAnalysis.FaceDetector`: solo detecta (sin descriptores) y solo en Windows.
- OpenCvSharp: decenas de MB nativos para lo que ONNX Runtime ya hace.

## Consecuencias
- Primera conexión de red de la app: una descarga iniciada por el usuario, con destino y tamaño mostrados; nada sobre la biblioteca se envía.
- Las pruebas de inferencia real descargan modelos y retratos de dominio público en CI; si no pueden, se marcan como bloqueadas.
- Rendimiento medido (CI, 2 vCPU): 164 ms por foto en Windows y 281 ms en Linux, decodificación incluida.
- Si cambian los modelos, los resultados existentes se regeneran sin perder nombres.
