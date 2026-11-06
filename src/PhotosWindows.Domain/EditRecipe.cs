namespace PhotosWindows.Domain;

/// <summary>Crop rectangle normalised to [0,1] in the coordinate space of the oriented image.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public static NormalizedRect Full { get; } = new(0, 0, 1, 1);

    public bool IsFull => X <= 1e-6 && Y <= 1e-6 && Width >= 1 - 1e-6 && Height >= 1 - 1e-6;

    public NormalizedRect Clamp()
    {
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        var w = Math.Clamp(Width, 0.01, 1 - x);
        var h = Math.Clamp(Height, 0.01, 1 - y);
        if (w < 0.01) { x = 0.99; w = 0.01; }
        if (h < 0.01) { y = 0.99; h = 0.01; }
        return new NormalizedRect(x, y, w, h);
    }
}

/// <summary>
/// Non-destructive edit description. It is stored in the catalog and applied at display/export time;
/// the original file is never modified. Values are expressed in perceptual, UI-friendly units:
/// most sliders are in [-1, 1] where 0 means "no change".
/// </summary>
public sealed record EditRecipe
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    // ---- Geometry (applied in this order: EXIF orientation, quarter turns, flips, straighten, crop) ----
    /// <summary>Clockwise quarter turns, 0..3.</summary>
    public int QuarterTurns { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    /// <summary>Straighten angle in degrees, [-45, 45].</summary>
    public double StraightenDegrees { get; init; }
    public NormalizedRect Crop { get; init; } = NormalizedRect.Full;
    /// <summary>Informational aspect constraint used by the editor ("free", "original", "1:1", "4:3", "16:9"...).</summary>
    public string AspectRatio { get; init; } = "free";

    // ---- Light ----
    /// <summary>Exposure in EV stops, [-3, 3].</summary>
    public double Exposure { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }

    // ---- Colour ----
    public double Saturation { get; init; }
    public double Vibrance { get; init; }
    /// <summary>Warmth: negative = cooler (blue), positive = warmer (amber).</summary>
    public double Temperature { get; init; }
    /// <summary>Negative = green, positive = magenta.</summary>
    public double Tint { get; init; }
    /// <summary>Black and white conversion amount, [0, 1].</summary>
    public double BlackAndWhite { get; init; }

    // ---- Detail & effects ----
    /// <summary>[0, 1].</summary>
    public double Sharpness { get; init; }
    /// <summary>[0, 1]: darkening strength towards the corners.</summary>
    public double Vignette { get; init; }

    /// <summary>Name of the preset the user applied last (informational).</summary>
    public string? PresetName { get; init; }

    public static EditRecipe Identity { get; } = new();

    public bool HasGeometry =>
        QuarterTurns % 4 != 0 || FlipHorizontal || FlipVertical || Math.Abs(StraightenDegrees) > 1e-6 || !Crop.IsFull;

    public bool HasTone =>
        Exposure != 0 || Brightness != 0 || Contrast != 0 || Highlights != 0 || Shadows != 0 ||
        Saturation != 0 || Vibrance != 0 || Temperature != 0 || Tint != 0 || BlackAndWhite != 0;

    public bool IsIdentity => !HasGeometry && !HasTone && Sharpness == 0 && Vignette == 0;

    /// <summary>Returns a copy with every value inside its valid range. Guards against corrupt or hand-edited catalogs.</summary>
    public EditRecipe Normalize()
    {
        static double C(double v, double min = -1, double max = 1) => double.IsFinite(v) ? Math.Clamp(v, min, max) : 0;
        return this with
        {
            FormatVersion = CurrentFormatVersion,
            QuarterTurns = ((QuarterTurns % 4) + 4) % 4,
            StraightenDegrees = C(StraightenDegrees, -45, 45),
            Crop = Crop.Clamp(),
            AspectRatio = string.IsNullOrWhiteSpace(AspectRatio) ? "free" : AspectRatio,
            Exposure = C(Exposure, -3, 3),
            Brightness = C(Brightness),
            Contrast = C(Contrast),
            Highlights = C(Highlights),
            Shadows = C(Shadows),
            Saturation = C(Saturation),
            Vibrance = C(Vibrance),
            Temperature = C(Temperature),
            Tint = C(Tint),
            BlackAndWhite = C(BlackAndWhite, 0, 1),
            Sharpness = C(Sharpness, 0, 1),
            Vignette = C(Vignette, 0, 1),
        };
    }

    /// <summary>Resets a single adjustment by its property name (as used by the editor UI).</summary>
    public EditRecipe Reset(string adjustment) => adjustment switch
    {
        nameof(Exposure) => this with { Exposure = 0 },
        nameof(Brightness) => this with { Brightness = 0 },
        nameof(Contrast) => this with { Contrast = 0 },
        nameof(Highlights) => this with { Highlights = 0 },
        nameof(Shadows) => this with { Shadows = 0 },
        nameof(Saturation) => this with { Saturation = 0 },
        nameof(Vibrance) => this with { Vibrance = 0 },
        nameof(Temperature) => this with { Temperature = 0 },
        nameof(Tint) => this with { Tint = 0 },
        nameof(BlackAndWhite) => this with { BlackAndWhite = 0 },
        nameof(Sharpness) => this with { Sharpness = 0 },
        nameof(Vignette) => this with { Vignette = 0 },
        nameof(StraightenDegrees) => this with { StraightenDegrees = 0 },
        nameof(Crop) => this with { Crop = NormalizedRect.Full, AspectRatio = "free" },
        "Geometry" => this with { QuarterTurns = 0, FlipHorizontal = false, FlipVertical = false, StraightenDegrees = 0, Crop = NormalizedRect.Full, AspectRatio = "free" },
        _ => throw new ArgumentException($"Unknown adjustment '{adjustment}'.", nameof(adjustment)),
    };

    public EditRecipe RotateClockwise() => this with { QuarterTurns = (QuarterTurns + 1) % 4, Crop = RotateCropClockwise(Crop) };

    public EditRecipe RotateCounterClockwise() => this with { QuarterTurns = (QuarterTurns + 3) % 4, Crop = RotateCropCounterClockwise(Crop) };

    /// <summary>Mirrors left/right. The crop and straighten angle are mirrored too so the framing is preserved.</summary>
    public EditRecipe ToggleFlipHorizontal() => this with
    {
        FlipHorizontal = !FlipHorizontal,
        StraightenDegrees = -StraightenDegrees,
        Crop = Crop with { X = 1 - Crop.X - Crop.Width },
    };

    public EditRecipe ToggleFlipVertical() => this with
    {
        FlipVertical = !FlipVertical,
        StraightenDegrees = -StraightenDegrees,
        Crop = Crop with { Y = 1 - Crop.Y - Crop.Height },
    };

    private static NormalizedRect RotateCropClockwise(NormalizedRect r) => new(1 - r.Y - r.Height, r.X, r.Height, r.Width);

    private static NormalizedRect RotateCropCounterClockwise(NormalizedRect r) => new(r.Y, 1 - r.X - r.Width, r.Height, r.Width);
}

/// <summary>Built-in, original looks. Each preset is just a recipe delta over the tonal parameters.</summary>
public static class EditPresets
{
    public static IReadOnlyList<(string Name, Func<EditRecipe, EditRecipe> Apply)> All { get; } =
    [
        ("Original", r => r with { Exposure = 0, Brightness = 0, Contrast = 0, Highlights = 0, Shadows = 0, Saturation = 0, Vibrance = 0, Temperature = 0, Tint = 0, BlackAndWhite = 0, Vignette = 0, PresetName = null }),
        ("Vivid", r => r with { Contrast = 0.15, Saturation = 0.2, Vibrance = 0.3, PresetName = "Vivid" }),
        ("Warm", r => r with { Temperature = 0.35, Vibrance = 0.1, PresetName = "Warm" }),
        ("Cool", r => r with { Temperature = -0.35, Tint = -0.05, PresetName = "Cool" }),
        ("Soft", r => r with { Contrast = -0.25, Highlights = -0.3, Shadows = 0.25, Saturation = -0.1, PresetName = "Soft" }),
        ("Drama", r => r with { Contrast = 0.4, Highlights = -0.4, Shadows = -0.15, Vignette = 0.35, PresetName = "Drama" }),
        ("Mono", r => r with { BlackAndWhite = 1, Contrast = 0.1, PresetName = "Mono" }),
        ("Noir", r => r with { BlackAndWhite = 1, Contrast = 0.5, Shadows = -0.2, Vignette = 0.45, PresetName = "Noir" }),
        ("Fade", r => r with { Contrast = -0.35, Shadows = 0.35, Saturation = -0.3, PresetName = "Fade" }),
    ];
}
