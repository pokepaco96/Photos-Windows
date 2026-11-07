# Revisión de modelos para Personas y mascotas

Fecha de consulta de las fuentes: **2026-11-07**. Todas las licencias se han leído en la fuente primaria (archivo `LICENSE`/`README` del repositorio o de la réplica oficial); se citan las URL. Esto **no es asesoramiento jurídico**: antes de distribuir comercialmente Photos Windows hace falta revisión legal (la licencia del propio proyecto también está pendiente, ver `PROGRESS.md`).

## Requisitos
- Inferencia **local** en Windows x64 (ARM64 deseable), sin red tras la instalación del modelo.
- Licencia de **código** y de **pesos** que permita uso y redistribución, idealmente comercial.
- Procedencia verificable (URL fija, versión, SHA-256).
- Tamaño razonable y rendimiento aceptable en CPU.
- Solo detección y agrupación de rostros para organizar la biblioteca privada. **Nada de atributos sensibles** (edad, género, etnia, emociones…) ni identificación contra personas externas.

## Motor de inferencia

| Opción | Licencia | Plataformas | Comentario |
|---|---|---|---|
| **ONNX Runtime** (`Microsoft.ML.OnnxRuntime` 1.31.1) | MIT ([LICENSE](https://github.com/microsoft/onnxruntime/blob/main/LICENSE)) | win-x64, win-arm64, linux-x64, osx | Paquete NuGet oficial con binarios nativos; CPU por defecto, DirectML opcional. Permite ejecutar las pruebas de inferencia real también en Linux (CI). **Elegido.** |
| OpenCV (`FaceDetectorYN`/`FaceRecognizerSF`) vía OpenCvSharp | OpenCV Apache-2.0; OpenCvSharp Apache-2.0 | x64 (ARM64 limitado en Windows) | Añade decenas de MB de binarios nativos solo para dos modelos que ONNX Runtime ya ejecuta. Descartado. |
| `Windows.Media.FaceAnalysis.FaceDetector` (WinRT) | Parte de Windows | Windows | Sin distribución de modelo, pero **solo detección** (sin puntos de referencia ni descriptores) y solo Windows (no testeable en Linux). Útil como referencia; no permite agrupar. |
| Windows ML / DirectML | MIT (DirectML) | Windows | Posible acelerador futuro detrás de ONNX Runtime; no necesario para empezar. |

## Detección de rostros

| Modelo | Código | Pesos | Datos de entrenamiento | Tamaño | Veredicto |
|---|---|---|---|---|---|
| **YuNet** 2023mar (opencv_zoo) | MIT ([libfacedetection](https://github.com/ShiqiYu/libfacedetection) / entrenamiento BSD-3 en [libfacedetection.train](https://github.com/ShiqiYu/libfacedetection.train)) | **MIT**: «All files in this directory are licensed under MIT License» ([README](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet)) | WIDER FACE, cuya web declara licencia **CC BY-NC-ND** ([WIDER FACE](http://shuoyang1213.me/WIDERFACE/)) | 232 KB (`face_detection_yunet_2023mar.onnx`, SHA-256 `8f2383e4…2552fa4`) | **Recomendado**, con la salvedad de datos de entrenamiento (ver Riesgos). Devuelve 5 puntos (ojos, nariz, comisuras) necesarios para alinear. |
| UltraFace (Ultra-Light-Fast-Generic-Face-Detector-1MB) | MIT ([LICENSE](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/LICENSE)) | MIT (mismo repositorio) | WIDER FACE | ~1 MB | Alternativa válida, pero sin puntos de referencia: obligaría a un segundo modelo de landmarks. |
| RetinaFace / SCRFD (InsightFace) | MIT | **Solo investigación no comercial**: «ALL models are available for non-commercial research purposes only» ([model_zoo](https://github.com/deepinsight/insightface/tree/master/model_zoo)) | WIDER FACE | — | **Excluido.** |
| BlazeFace (MediaPipe) | Apache-2.0 (MediaPipe) | **No verificada en esta revisión** | — | <1 MB | Formato TFLite; las conversiones a ONNX son de terceros con licencias propias. No se ha evaluado porque YuNet cubre la necesidad. |
| dlib MMOD | Boost (dlib) | «public domain» según el autor ([dlib-models](https://github.com/davisking/dlib-models)) | Mezcla de ImageNet, AFLW, VOC, VGG, WIDER, FaceScrub | 0,7 MB | Formato propio de dlib (no ONNX); obligaría a integrar dlib nativo. Descartado. |

## Descriptores (embeddings) para agrupar

| Modelo | Código | Pesos | Datos de entrenamiento | Tamaño | Veredicto |
|---|---|---|---|---|---|
| **SFace** 2021dec (MobileFaceNet, opencv_zoo) | Apache-2.0 ([SFace](https://github.com/zhongyy/SFace)) | **Apache-2.0**: «All files in this directory are licensed under Apache 2.0 License» ([README](https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface)) | El artículo entrena con CASIA-WebFace, VGGFace2 y MS-Celeb-1M; la ficha del modelo del zoo **no indica** cuál se usó. MS-Celeb-1M fue retirado por Microsoft en 2019 | 38,7 MB fp32 (`…2021dec.onnx`, SHA-256 `0ba9fbfa…7c34e79`); 9,9 MB int8 | **Recomendado**, con la salvedad de procedencia. Entrada 112×112 alineada con 5 puntos, salida 128 valores. |
| ArcFace (InsightFace, también en ONNX Model Zoo) | MIT | **Solo investigación no comercial** (misma política de InsightFace) | MS1M | 250 MB | **Excluido.** |
| dlib ResNet (face recognition v1) | Boost | «public domain» | VGG Face, FaceScrub y «a large number of images I scraped from the internet» | 22 MB | No ONNX; procedencia aún menos clara. Descartado. |
| FaceNet (davidsandberg) | MIT | MIT | VGGFace2 / CASIA-WebFace | 90 MB+ | TensorFlow 1; sin conversión oficial. Descartado. |

## Mascotas

| Modelo | Código | Pesos | Datos | Tamaño | Veredicto |
|---|---|---|---|---|---|
| **YOLOX-s** (opencv_zoo) | Apache-2.0 | Apache-2.0 ([README](https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox)) | COCO 2017 (anotaciones CC BY 4.0; imágenes de Flickr con sus licencias) | 35,9 MB fp32 / 9,1 MB int8 | Detecta las clases «cat» y «dog» de COCO. Permite **agrupar por especie**, no reconocer individuos. |
| Identidad de mascotas | — | — | — | — | No se ha encontrado un modelo de identidad de mascotas con licencia de pesos verificable. **No se implementa reconocimiento individual de mascotas.** |
| YOLOv5/v8/v11 (Ultralytics) | AGPL-3.0 | AGPL-3.0 | COCO | — | Excluido (AGPL incompatible con una app que no publique todo bajo AGPL). |

## Riesgos
1. **Datos de entrenamiento.** Las licencias de los pesos (MIT/Apache-2.0) permiten uso y redistribución, pero los conjuntos de datos tienen restricciones (WIDER FACE CC BY-NC-ND) o historia problemática (MS-Celeb-1M retirado). Si esas condiciones se transmiten a los pesos es una cuestión jurídica no resuelta.
2. **Datos biométricos.** Los descriptores faciales son datos biométricos (RGPD art. 9). El tratamiento lo hace el usuario sobre su propia biblioteca, en local; la app debe pedir consentimiento explícito, permitir borrar todo y no exportar nada.
3. **Sesgo.** Los modelos de rostros funcionan peor con algunos grupos de población; la agrupación es una sugerencia siempre corregible.

## Decisión
- **Motor:** ONNX Runtime (MIT), CPU, en un proyecto propio `PhotosWindows.MachineLearning`. El dominio no depende de él.
- **Modelos:** YuNet 2023mar (detección + 5 puntos) y SFace 2021dec (descriptor de 128 valores).
- **No se empaquetan** en el repositorio ni en la app publicada por el riesgo 1. El usuario los **descarga expresamente** desde Ajustes › Personas: la app muestra proveedor (opencv_zoo en Hugging Face), URL fija, tamaño, licencia y aviso de datos de entrenamiento; descarga solo tras confirmación, verifica **SHA-256** fijado en el código y guarda los archivos en `%LOCALAPPDATA%\PhotosWindows\models`. También se pueden instalar a mano desde un archivo (misma verificación). Se pueden borrar.
- **Sin modelo**, la función no se ofrece (la sección no aparece) y nada falla.
- **Mascotas:** detección por especie con YOLOX-s en un hito posterior, con el mismo mecanismo de descarga. Sin reconocimiento individual.
- **Pruebas de inferencia real** en CI: descargan los modelos (no datos de usuarios) y usan retratos de **dominio público** (NASA); si la descarga no es posible, la prueba queda **bloqueada**, nunca superada.

## Fuentes (consultadas el 2026-11-07)
- ONNX Runtime: https://github.com/microsoft/onnxruntime/blob/main/LICENSE · NuGet `Microsoft.ML.OnnxRuntime` 1.31.1
- YuNet: https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet · https://huggingface.co/opencv/face_detection_yunet · https://github.com/ShiqiYu/libfacedetection.train
- SFace: https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface · https://huggingface.co/opencv/face_recognition_sface · https://github.com/zhongyy/SFace
- YOLOX: https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox · https://huggingface.co/opencv/object_detection_yolox
- InsightFace: https://github.com/deepinsight/insightface (README «License») · https://github.com/deepinsight/insightface/tree/master/model_zoo
- UltraFace: https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB
- dlib-models: https://github.com/davisking/dlib-models
- WIDER FACE: http://shuoyang1213.me/WIDERFACE/
- COCO: https://cocodataset.org/#termsofuse

SHA-256 completos (Hugging Face LFS, rama `main`):
| Archivo | Bytes | SHA-256 |
|---|---|---|
| `face_detection_yunet_2023mar.onnx` | 232 589 | `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4` |
| `face_recognition_sface_2021dec.onnx` | 38 696 353 | `0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79` |
| `object_detection_yolox_2022nov.onnx` | 35 858 002 | `c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063` |
