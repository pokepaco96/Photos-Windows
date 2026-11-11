# ADR-0015 · Carriles de miniaturas: interactivo y de fondo

**Contexto.** Un usuario abrió la foto 529 de 8.685 diez minutos después de importar, y el visor se quedó en negro con el indicador de carga girando. Antes de mostrar nada, el visor pedía la miniatura grande a `ThumbnailService`. Esa petición esperaba sin límite al mismo semáforo que usa la pasada de fondo, que genera las miniaturas y los hashes de toda la biblioteca. El render de calidad completa solo empezaba después. Desde #12 la pasada de fondo es paralela y puede ocupar todos los huecos. La extracción de fotogramas de vídeo de Windows y algunos decodificadores nativos no se pueden cancelar. Si esos trabajos no terminan, los huecos quedan ocupados para siempre. `ViewerPriorityTests` reproduce ese patrón con un extractor que no responde. Con el código anterior, la vista previa del visor no llegaba nunca.

**Decisión.**
- **Dos carriles con capacidad propia.** El carril *interactivo* (visor y cuadrícula) tiene al menos 2 huecos. El carril *de fondo* tiene `MaxBackgroundWorkers` huecos. Una petición interactiva nunca espera detrás del fondo.
- **Límite de 30 s por generación.** Al vencer, se libera el hueco y el elemento se marca como fallido, así que no se reintenta. La llamada nativa no se aborta y puede seguir ejecutándose; el registro lo indica.
- **Peticiones en curso.** Se deduplican por ruta y carril. La entrada se elimina al terminar la generación, aunque nadie la espere ya.
- **Orden del visor.** Primero muestra la miniatura pequeña de la caché, luego la grande y después el render completo. El inspector se carga después de la primera imagen. Toda carga termina en imagen, error visible o cancelación por navegación.

**Alternativas.** Pausar el fondo mientras el visor está abierto: no protege frente a un trabajo ya atascado. Un único semáforo con prioridad: más complejo, y un hueco atascado seguiría bloqueado.

**Consecuencias.** Puede haber trabajo duplicado si la misma miniatura se pide a la vez en los dos carriles. Es raro, y el nombre temporal único evita colisiones. Con un elemento atascado, en el peor caso quedan hilos nativos ocupados, pero la interfaz ya no depende de ellos.
