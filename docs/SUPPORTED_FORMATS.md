# Formatos compatibles

Leyenda: ✅ verificado con pruebas automáticas · 🪟 depende de un códec de Windows (WIC/Media Foundation), verificación manual pendiente en `QA_CHECKLIST.md` · ⚠️ limitado · ❌ no compatible.

La importación reconoce el archivo por **extensión + firma real**. Si el contenido no coincide con la extensión, se informa como «no compatible» y no se importa.

## Imágenes
| Formato | Extensiones | Firma | Decodificador | Miniatura/visor | Edición | Metadatos (lectura) | Notas |
|---|---|---|---|---|---|---|---|
| JPEG | .jpg .jpeg .jpe .jfif | `FF D8 FF` | SkiaSharp | ✅ | ✅ | ✅ EXIF/IPTC/XMP/GPS | Decodificación escalada; JPEG truncado se muestra parcialmente |
| PNG | .png | `89 50 4E 47…` | SkiaSharp | ✅ | ✅ | ✅ | Transparencia; al exportar a JPEG se compone sobre blanco |
| GIF | .gif | `GIF8` | SkiaSharp | ✅ (1.er fotograma) | ⚠️ estática | ✅ | Animación no se reproduce en el MVP |
| BMP | .bmp .dib | `BM` | SkiaSharp | ✅ | ✅ | ⚠️ | |
| WebP | .webp | `RIFF….WEBP` | SkiaSharp | ✅ | ✅ | ✅ | |
| HEIF/HEIC | .heic .heif .hif | `ftyp` heic/mif1… | WIC | 🪟 | 🪟 | ✅ EXIF | Requiere «Extensiones de imagen HEIF» (Microsoft Store) |
| AVIF | .avif | `ftyp` avif | WIC | 🪟 | 🪟 | ✅ | Requiere «Extensión de vídeo AV1» |
| TIFF | .tif .tiff | `II*\0` / `MM\0*` | WIC | 🪟 | 🪟 | ✅ | Multipágina: primera página |
| DNG y RAW | .dng .cr2 .cr3 .nef .nrw .arw .srf .sr2 .raf .orf .rw2 .pef .srw .x3f .3fr .iiq .rwl | TIFF / `ftyp crx` / `FUJI` / `FOVb` | WIC | 🪟 | 🪟 (sobre la vista revelada por el códec) | ✅ EXIF | Requiere «Extensión de imagen RAW»; cobertura según cámara. RAW+JPEG se agrupan (el JPEG es el principal) |

## Vídeo
| Formato | Extensiones | Reproducción | Fotograma/miniatura | Metadatos | Notas |
|---|---|---|---|---|---|
| MP4 / MOV / M4V / 3GP | .mp4 .mov .m4v .3gp | 🪟 Media Foundation | 🪟 | ✅ duración, fecha (UTC→zona), resolución, GPS Apple | HEVC requiere «Extensiones de vídeo HEVC» |
| AVI / WMV / ASF | .avi .wmv | 🪟 | 🪟 | ⚠️ | |
| MKV / WebM | .mkv .webm | 🪟 (Windows 11 incluye soporte básico) | 🪟 | ⚠️ | |
| MPEG-TS | .mts .m2ts | 🪟 | 🪟 | ⚠️ | |

**Clips de movimiento** (pares foto+vídeo tipo «Live Photo»): mismo nombre base en la misma carpeta, `.MOV` de ≤ 4,5 s o mismo identificador de contenido → se adjunta a la foto. Si no, se importa como vídeo independiente.

## Archivos asociados
| Tipo | Extensiones | Soporte |
|---|---|---|
| Sidecar XMP | `IMG.xmp`, `IMG.CR2.xmp` | ✅ Lectura de título, descripción, palabras clave (dc:subject / jerárquicas), valoración y fecha; se adjunta al asset |

## Desde un iPhone por USB
Se importan los archivos que iOS expone por USB, tal como llegan. Con *Conservar originales* llegan HEIC/HEIF y vídeo HEVC (MOV); con la conversión automática de iOS llegan JPEG y H.264, y la app lo detecta comparando la firma real del archivo con su extensión y lo indica en el resultado. Las Live Photos se importan con su clip MOV. Ver [`research/IPHONE_USB_REVIEW.md`](research/IPHONE_USB_REVIEW.md).

## Exportación
| Formato | Metadatos escritos |
|---|---|
| JPEG | ✅ EXIF propio (fecha+zona, cámara, objetivo, exposición, dimensiones, orientación=1, GPS opcional) + XMP (título, descripción, palabras clave, valoración) |
| PNG / WebP | ❌ sin metadatos (limitación conocida) |
| Original | Copia exacta byte a byte |
