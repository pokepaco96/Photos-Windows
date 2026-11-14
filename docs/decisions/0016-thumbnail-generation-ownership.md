# ADR-0016 · Quién es dueño de cada decodificación de miniatura

**Contexto.** Incidencia del 14-11-2026. El usuario abrió `IMG_0671.HEIC`, la foto 4589 de 8685. El visor se quedó en negro con el mensaje «Cargando vista previa…». El proceso estaba al ~90 % de CPU, con 2,35 GB de memoria y 31,8 MB/s de disco, y el equipo al 100 %. Ya estaba aplicado el ADR-0015 (carriles interactivo y de fondo, límite de 30 s).

Al revisar el código aparecieron tres defectos. Los dos primeros se demuestran con pruebas:

1. **La cuadrícula decodificaba miniaturas que ya nadie quería.** Al desplazarse, cada mosaico pide su miniatura y la cancela cuando se recicla. Pero la generación ya se había lanzado (`Lazy<Task>`) y esperaba un hueco sin token, así que se ejecutaba igualmente. `GridScrollStarvationTests` lo reproduce con el decodificador real más 100 ms por decodificación: tras 199 peticiones abandonadas, la vista previa del visor tardó **11,1 s** y se hicieron las 199 decodificaciones. El tiempo crece de forma lineal con lo que se desplaza.
2. **El visor compartía carril con la cuadrícula.** El carril «interactivo» del ADR-0015 servía a los dos, así que el visor esperaba detrás de toda esa cola.
3. **El límite de 30 s no cancelaba nada.** Usaba `Task.WaitAsync(timeout)`, que solo deja de esperar: el hueco se liberaba mientras la decodificación nativa seguía en marcha. Las decodificaciones atascadas podían acumularse sin límite.

**Decisión.**

- **Cada generación cuenta quién la espera.** Si el último que la espera se va antes de que empiece, no se decodifica nada. Si ya empezó, se cancela su token, que llega a Skia y a las llamadas asíncronas de WinRT/WIC.
- **Tres carriles con capacidad propia.**
  - *Visor*: 2 huecos.
  - *Cuadrícula*: el mayor entre 2 y `MaxBackgroundWorkers`.
  - *Fondo*: `MaxBackgroundWorkers`.
- **Un hueco se libera solo cuando el trabajo ha terminado de verdad.** Si una decodificación supera el límite, quien la espera recibe «sin miniatura». La decodificación sigue contada y conserva su hueco, y el mismo archivo no se vuelve a lanzar mientras siga viva. Si todos los huecos de un carril están ocupados por decodificaciones que superaron el límite, el carril se declara bloqueado: responde de inmediato y no encola más trabajo. Así, el número de decodificaciones vivas nunca supera la capacidad total de los carriles.
- **El fondo cede al primer plano.** La pasada de fondo y los hashes perceptuales no toman trabajo nuevo mientras el visor carga (y 5 s después) ni mientras la cuadrícula pide miniaturas.
- **El visor decodifica una sola vez cuando basta.** Si la ventana necesita 2048 px o menos, la vista previa de 2048 px es la imagen final y no hay una segunda decodificación completa del original. El render de calidad se ajusta al tamaño de la ventana × la escala de pantalla, y se pide más resolución al hacer zoom. Solo hay un render completo a la vez en toda la app, y su hueco se libera cuando termina de verdad.
- **«Lista» significa imagen en pantalla.** El visor solo pasa a `Ready` cuando WinUI dispara `ImageOpened`.

**Alternativas.**
- *Proceso aislado para decodificar, que se pueda matar.* Sería la única forma de abortar de verdad un códec nativo atascado. Se descarta de momento por el coste en empaquetado, memoria y comunicación entre procesos, y porque las mediciones no muestran códecs colgados, sino colas de trabajo inútil. Queda como siguiente paso si los informes de diagnóstico muestran decodificaciones vivas tras el límite.
- *Bajar `MaxBackgroundWorkers`*: no arregla la cola de trabajo abandonado.

**Consecuencias.**
- Si un códec se cuelga de verdad, sus hilos nativos siguen ocupados y su hueco no se reutiliza. La app lo muestra (`ThumbnailStats`, registro) y no lanza más trabajo de ese tipo.
- Los elementos que quedaron en cola al desplazar siguen pendientes y los completa la pasada de fondo cuando el usuario no está esperando.
