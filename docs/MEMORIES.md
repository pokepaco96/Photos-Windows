# Recuerdos: reglas, explicación y control

Recuerdos sugiere colecciones a partir de datos que **ya están en el catálogo local**: fecha de captura, coordenadas GPS, favoritas/editadas y, solo si Personas está activada en la biblioteca, los grupos a los que el usuario ha puesto nombre. No hay red, ni IA remota, ni geocodificación, ni «comprensión» del contenido de las fotos. Con los mismos datos, la misma fecha y las mismas reglas, el resultado es siempre el mismo.

Código: `MemoryCandidateGenerator` y `MemoryRanker` (dominio, puros), `MemoriesService` (aplicación), `MemoryRepository` (SQLite), `MemoryDescriber` (textos).

## Qué fotos se usan
Solo elementos visibles (ni ocultos ni en «Eliminados recientemente»), con el original disponible y cuya miniatura no ha fallado. Nunca se leen ni decodifican los originales para generar recuerdos. Los elementos con fecha futura (reloj de cámara mal puesto) se ignoran. Las coordenadas (0,0) o fuera de rango cuentan como «sin ubicación», igual que en el resto de la app.

**Días.** Se usa el día de calendario local guardado con cada foto (`capture_local`). Una foto sin desfase UTC en su EXIF conserva su hora de pared; un vídeo con hora UTC se convierte a la zona del equipo al importarlo. Por eso los cambios de hora (DST) no mueven fotos de día. «Hoy» es la fecha del equipo en su zona horaria.

## Tipos y umbrales (`MemoryRules`)
| Tipo | Regla | Valores por defecto |
|---|---|---|
| Tal día como hoy | Fotos del mismo día y mes de años anteriores, un recuerdo por año | mínimo 3 elementos; ventana ±0 días |
| Evento | Fotos seguidas: se corta cuando pasan más de 4 h o dos fotos consecutivas con GPS distan más de 5 km | mínimo 8 elementos; duración máxima 36 h |
| Viaje | Días consecutivos lejos de la **zona habitual** | ≥ 100 km, ≥ 2 días, ≥ 10 elementos, hasta 2 días sin GPS en medio |
| Mes | Todas las fotos de un mes terminado | mínimo 20 |
| Estación | Trimestre meteorológico terminado (dic–feb, mar–may, jun–ago, sep–nov) | mínimo 30 |
| Año | Año terminado con fotos en varios meses | mínimo 40 elementos y 3 meses distintos |
| Persona | Grupo de Personas **con nombre puesto por el usuario** | mínimo 10; solo con Personas activada |

**29 de febrero.** El 29-F solo coincide con años bisiestos. El 28-F de un año no bisiesto incluye también el 29-F de los bisiestos, para que esas fotos tengan su día todos los años.

**Zona habitual (para viajes).** Es la celda de ~28 km donde hay fotos con GPS en más días distintos. Solo se usa si hay al menos 10 días con GPS y esa celda reúne al menos el 40 % de ellos; si no, **no se sugieren viajes**. Un día cuenta como «fuera» cuando al menos el 60 % de sus fotos con GPS está a más de 100 km. La app nunca muestra ni guarda esa zona como «casa»: solo sirve para calcular la distancia.

**Estaciones.** El nombre (invierno, verano…) solo se pone cuando al menos la mitad de las fotos del trimestre tiene GPS y el 90 % está en el mismo hemisferio. Si no, el título es el intervalo de meses («Diciembre de 2023 – Febrero de 2024»). No se supone el hemisferio.

**Grupos grandes.** Un recuerdo muestra como máximo 80 elementos. Si hay más, se toma una muestra repartida por días (uno por día en rondas), saltando ráfagas (fotos a menos de 2 s) y, si la preferencia está activa, empezando por favoritas y editadas. La portada es la primera favorita (o editada) o, sin ellas, la foto central.

## Orden, repetición y límites
1. Puntuación base por tipo (tal día como hoy 1,00 · viaje 0,80 · evento 0,70 · persona 0,60 · año 0,55 · estación 0,50 · mes 0,45).
2. Sumas: tamaño (hasta +0,10), proporción de favoritas (hasta +0,10, si la preferencia está activa), cercanía en el calendario (viajes y eventos a ±7 días de hoy hasta +0,30; mismo mes +0,25; misma estación +0,15).
3. **Rotación semanal** (±0,05): un valor fijo calculado de la clave del recuerdo y la semana ISO, para que la página no sea idéntica siempre. Es el mismo en cualquier equipo.
4. Máximos por tipo (5 / 4 / 4 / 3 / 3 / 2 / 3 en el orden anterior) y 24 en total.
5. Sin repeticiones: si un recuerdo comparte más de la mitad de sus fotos con otro ya elegido, se descarta; un evento ocurrido dentro de un viaje elegido se considera parte del viaje.

Nunca se muestra un recuerdo vacío ni uno por debajo de su mínimo.

## Explicación
Cada tarjeta incluye «Por qué aparece», construido solo con los hechos del recuerdo: número de elementos, fechas, años transcurridos, días, distancia aproximada y coordenadas aproximadas («alrededor de 38,72° N, 9,14° W»). **Nunca aparece un nombre de lugar**, y los nombres de personas son exactamente los que escribió el usuario.

## Control del usuario
- **Ocultar** un recuerdo, con **Deshacer**; los ocultos se listan en Opciones y se pueden restaurar.
- **Excluir un tipo** de recuerdo (por ejemplo, Personas o Viajes).
- **Excluir días**: las fotos tomadas entre dos fechas no se usan en ningún recuerdo.
- **Preferencia** de favoritas/editadas (activada por defecto, se puede desactivar).
- **Crear álbum** con las fotos del recuerdo y **pase de diapositivas** (reproducir/pausar, anterior/siguiente, salir; Espacio, flechas y Esc).

Ocultar y excluir se guardan en el catálogo de la biblioteca (tablas `memory_hidden` y `memory_exclusions`), persisten tras reiniciar y **no borran ni modifican ninguna foto**.

## Personas
Recuerdos solo consulta Personas si el usuario la activó en esa biblioteca (lo que registra su consentimiento) y no ha excluido el tipo «Personas». Nunca activa Personas, nunca lanza un análisis facial y nunca muestra grupos sin nombre.

## Caché y regeneración
Los recuerdos se calculan al entrar en la página y se guardan **en memoria** por biblioteca y día. Volver desde un recuerdo o del pase reutiliza ese resultado; cualquier cambio de la biblioteca hecho en la app, cambiar de biblioteca, activar/desactivar Personas o el botón **Regenerar** lo recalculan. No se escribe ninguna caché en disco.

## Límites conocidos
- Sin nombres de lugares, por diseño.
- Los umbrales no se editan aún desde la interfaz (solo la preferencia de favoritas y las exclusiones).
- Las fotos sin fecha fiable (fecha tomada del archivo) pueden agruparse por la fecha de copia.
- El pase muestra los vídeos como imagen fija (su fotograma de portada).
