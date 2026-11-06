# ADR-0005 · Pipeline de imagen

**Decisión.**
- SkiaSharp (MIT) decodifica JPEG, PNG, GIF (primer fotograma), BMP y WebP, y codifica JPEG/PNG/WebP. Decodificación JPEG escalada (1/2, 1/4, 1/8) para miniaturas rápidas.
- Otros formatos (HEIF/HEIC, AVIF, TIFF, RAW) mediante `IRasterDecoder` enchufables. En Windows: `WicRasterDecoder` (Windows Imaging Component), que usa los códecs instalados (HEIF/AV1/Raw Image Extensions de Microsoft Store).
- **Geometría en una sola pasada**: orientación EXIF → giros de 90° → volteos → enderezado (con zoom de cobertura para no dejar esquinas vacías) → recorte → escala, compuestos en una única matriz afín (`Affine`), con remuestreo Mitchell o mipmaps al reducir mucho. Evita pérdidas por remuestreos repetidos.
- **Tono y color** en C# determinista sobre BGRA8: sRGB→lineal (exposición y balance de blancos multiplicativos), vuelta a sRGB, luces/sombras con máscaras de luminancia, brillo (gamma de medios tonos), contraste, vibrancia, saturación, B/N, viñeta, nitidez 3×3. Cálculo en `float`; una sola cuantización a 8 bits por pasada.
- Espacio de color de trabajo: sRGB, 8 bits por canal. Los perfiles ICC no-sRGB se convierten a sRGB al decodificar (comportamiento de Skia/WIC). Limitación documentada: sin edición en 16 bits ni gamas amplias en el MVP.
- Límites anti-bomba: 200 MP y 2 GiB por imagen; archivos truncados se muestran parcialmente.

**Consecuencias.** El mismo pipeline sirve para miniaturas, visor, vista previa del editor y exportación (lo que se ve es lo que se exporta). Testeable en Linux con imágenes sintéticas.
