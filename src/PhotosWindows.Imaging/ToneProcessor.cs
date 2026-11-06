using PhotosWindows.Domain;

namespace PhotosWindows.Imaging;

/// <summary>
/// Per-pixel tone and colour pipeline on BGRA8 buffers (premultiplied or opaque).
/// Order (fixed, documented in docs/ARCHITECTURE.md):
/// sRGB→linear, exposure, white balance → sRGB, highlights/shadows, brightness, contrast,
/// vibrance, saturation, black &amp; white, then vignette and sharpening.
/// Work is done in float with 8-bit quantisation only at the end of the pass to avoid banding
/// from repeated conversions.
/// </summary>
public static class ToneProcessor
{
    private const float LumaR = 0.2126f, LumaG = 0.7152f, LumaB = 0.0722f;
    private const int EncodeLutSize = 4096;
    private static readonly float[] SrgbToLinear = BuildDecodeLut();
    private static readonly float[] LinearToSrgb = BuildEncodeLut();

    public static void Apply(Span<byte> bgra, int width, int height, EditRecipe recipe)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("Buffer too small.", nameof(bgra));
        var r = recipe.Normalize();
        if (r.HasTone)
        {
            ApplyTone(bgra, width, height, r);
        }

        if (r.Vignette > 0)
        {
            ApplyVignette(bgra, width, height, (float)r.Vignette);
        }

        if (r.Sharpness > 0)
        {
            ApplySharpen(bgra, width, height, (float)r.Sharpness);
        }
    }

    private static unsafe void ApplyTone(Span<byte> bgra, int width, int height, EditRecipe r)
    {
        var p = new ToneParams(r);
        fixed (byte* basePtr = bgra)
        {
            var ptr = (IntPtr)basePtr;
            Parallel.For(0, height, y =>
            {
                var row = (byte*)ptr + (long)y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var px = row + x * 4;
                    var a = px[3];
                    if (a == 0) continue;
                    float bf = px[0], gf = px[1], rf = px[2];
                    if (a < 255)
                    {
                        // Unpremultiply so colour maths see true colours.
                        var inv = 255f / a;
                        bf *= inv; gf *= inv; rf *= inv;
                    }

                    var (ro, go, bo) = Process(Math.Min(rf, 255f), Math.Min(gf, 255f), Math.Min(bf, 255f), in p);
                    var scale = a / 255f;
                    px[0] = ToByte(bo * scale);
                    px[1] = ToByte(go * scale);
                    px[2] = ToByte(ro * scale);
                }
            });
        }
    }

    /// <summary>Processes one straight-alpha sRGB pixel in 0..255; returns 0..255 floats.</summary>
    internal static (float R, float G, float B) Process(float r8, float g8, float b8, in ToneParams p)
    {
        // 1) Linear light: exposure and white balance are multiplicative there.
        var r = SrgbToLinear[(int)r8] * p.ExposureGain * p.GainR;
        var g = SrgbToLinear[(int)g8] * p.ExposureGain * p.GainG;
        var b = SrgbToLinear[(int)b8] * p.ExposureGain * p.GainB;

        // 2) Back to perceptual (sRGB) space for the remaining, perceptually tuned controls.
        r = Encode(r); g = Encode(g); b = Encode(b);

        // 3) Highlights / shadows: luminance-masked, hue-preserving.
        if (p.Shadows != 0 || p.Highlights != 0)
        {
            var l = Luma(r, g, b);
            var shadowMask = 1f - SmoothStep(0f, 0.55f, l);
            var highlightMask = SmoothStep(0.45f, 1f, l);
            var target = l + 0.35f * p.Shadows * shadowMask * (p.Shadows > 0 ? (1f - l) : l)
                           + 0.35f * p.Highlights * highlightMask * (p.Highlights > 0 ? (1f - l) : l);
            target = Math.Clamp(target, 0f, 1f);
            if (l > 1e-4f)
            {
                var k = target / l;
                r *= k; g *= k; b *= k;
            }
            else
            {
                r = g = b = target;
            }
        }

        // 4) Brightness: midtone gamma keeps pure black and white anchored.
        if (p.BrightnessGamma != 1f)
        {
            r = MathF.Pow(Math.Clamp(r, 0f, 1f), p.BrightnessGamma);
            g = MathF.Pow(Math.Clamp(g, 0f, 1f), p.BrightnessGamma);
            b = MathF.Pow(Math.Clamp(b, 0f, 1f), p.BrightnessGamma);
        }

        // 5) Contrast around mid grey.
        if (p.Contrast != 1f)
        {
            r = (r - 0.5f) * p.Contrast + 0.5f;
            g = (g - 0.5f) * p.Contrast + 0.5f;
            b = (b - 0.5f) * p.Contrast + 0.5f;
        }

        // 6) Colour: vibrance favours muted colours, saturation is uniform.
        if (p.Vibrance != 0 || p.Saturation != 1f)
        {
            var l = Luma(r, g, b);
            var max = MathF.Max(r, MathF.Max(g, b));
            var min = MathF.Min(r, MathF.Min(g, b));
            var currentSat = Math.Clamp(max - min, 0f, 1f);
            var factor = p.Saturation * (1f + p.Vibrance * (1f - currentSat));
            factor = MathF.Max(0f, factor);
            r = l + (r - l) * factor;
            g = l + (g - l) * factor;
            b = l + (b - l) * factor;
        }

        // 7) Black & white.
        if (p.BlackAndWhite > 0)
        {
            var l = Luma(r, g, b);
            r += (l - r) * p.BlackAndWhite;
            g += (l - g) * p.BlackAndWhite;
            b += (l - b) * p.BlackAndWhite;
        }

        return (Math.Clamp(r, 0f, 1f) * 255f, Math.Clamp(g, 0f, 1f) * 255f, Math.Clamp(b, 0f, 1f) * 255f);
    }

    private static unsafe void ApplyVignette(Span<byte> bgra, int width, int height, float amount)
    {
        var cx = (width - 1) / 2f;
        var cy = (height - 1) / 2f;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy);
        if (maxDist <= 0) return;
        fixed (byte* basePtr = bgra)
        {
            var ptr = (IntPtr)basePtr;
            Parallel.For(0, height, y =>
            {
                var row = (byte*)ptr + (long)y * width * 4;
                var dy = y - cy;
                for (var x = 0; x < width; x++)
                {
                    var dx = x - cx;
                    var d = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                    var factor = 1f - amount * 0.85f * SmoothStep(0.35f, 1.05f, d);
                    var px = row + x * 4;
                    px[0] = ToByte(px[0] * factor);
                    px[1] = ToByte(px[1] * factor);
                    px[2] = ToByte(px[2] * factor);
                }
            });
        }
    }

    /// <summary>3×3 unsharp-style sharpening. Edges are left untouched.</summary>
    private static void ApplySharpen(Span<byte> bgra, int width, int height, float amount)
    {
        if (width < 3 || height < 3) return;
        var source = bgra.ToArray();
        var k = amount * 0.9f;
        var dest = bgra.ToArray();
        Parallel.For(1, height - 1, y =>
        {
            var stride = width * 4;
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * stride + x * 4;
                for (var c = 0; c < 3; c++)
                {
                    var center = source[i + c];
                    var neighbours = source[i + c - 4] + source[i + c + 4] + source[i + c - stride] + source[i + c + stride];
                    var value = center * (1 + 4 * k) - k * neighbours;
                    // Never exceed alpha in premultiplied buffers.
                    dest[i + c] = (byte)Math.Clamp((int)MathF.Round(value), 0, source[i + 3]);
                }
            }
        });
        dest.CopyTo(bgra);
    }

    internal readonly struct ToneParams
    {
        public ToneParams(EditRecipe r)
        {
            ExposureGain = MathF.Pow(2f, (float)r.Exposure);
            var t = (float)r.Temperature;
            var m = (float)r.Tint;
            var gr = 1f + 0.22f * t;
            var gb = 1f - 0.22f * t;
            var gg = 1f - 0.18f * m;
            // Keep overall luminance roughly constant when changing white balance.
            var norm = LumaR * gr + LumaG * gg + LumaB * gb;
            GainR = gr / norm;
            GainG = gg / norm;
            GainB = gb / norm;
            Highlights = (float)r.Highlights;
            Shadows = (float)r.Shadows;
            BrightnessGamma = MathF.Pow(2f, -(float)r.Brightness * 0.8f);
            Contrast = 1f + (float)r.Contrast * (r.Contrast > 0 ? 0.8f : 0.6f);
            Saturation = 1f + (float)r.Saturation;
            Vibrance = (float)r.Vibrance * 0.8f;
            BlackAndWhite = (float)r.BlackAndWhite;
        }

        public float ExposureGain { get; }
        public float GainR { get; }
        public float GainG { get; }
        public float GainB { get; }
        public float Highlights { get; }
        public float Shadows { get; }
        public float BrightnessGamma { get; }
        public float Contrast { get; }
        public float Saturation { get; }
        public float Vibrance { get; }
        public float BlackAndWhite { get; }
    }

    private static float Luma(float r, float g, float b) => LumaR * r + LumaG * g + LumaB * b;

    private static float SmoothStep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Encode(float linear)
    {
        if (linear <= 0f) return 0f;
        if (linear >= 1f) return 1f;
        return LinearToSrgb[(int)(linear * (EncodeLutSize - 1) + 0.5f)];
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    private static float[] BuildDecodeLut()
    {
        var lut = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255.0;
            lut[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }

        return lut;
    }

    private static float[] BuildEncodeLut()
    {
        var lut = new float[EncodeLutSize];
        for (var i = 0; i < EncodeLutSize; i++)
        {
            var l = i / (double)(EncodeLutSize - 1);
            lut[i] = (float)(l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055);
        }

        return lut;
    }
}
