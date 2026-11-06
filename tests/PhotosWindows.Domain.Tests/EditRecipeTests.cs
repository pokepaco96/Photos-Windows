using PhotosWindows.Domain;

namespace PhotosWindows.Domain.Tests;

public class EditRecipeTests
{
    [Fact]
    public void Identity_has_no_changes()
    {
        Assert.True(EditRecipe.Identity.IsIdentity);
        Assert.False(EditRecipe.Identity.HasGeometry);
        Assert.False(EditRecipe.Identity.HasTone);
    }

    [Fact]
    public void Normalize_clamps_out_of_range_and_non_finite_values()
    {
        var r = new EditRecipe { Exposure = 10, Contrast = double.NaN, Saturation = -5, QuarterTurns = -1, StraightenDegrees = 90, Vignette = 3, Crop = new NormalizedRect(-1, 0.5, 5, 5) }.Normalize();
        Assert.Equal(3, r.Exposure);
        Assert.Equal(0, r.Contrast);
        Assert.Equal(-1, r.Saturation);
        Assert.Equal(3, r.QuarterTurns);
        Assert.Equal(45, r.StraightenDegrees);
        Assert.Equal(1, r.Vignette);
        Assert.Equal(0, r.Crop.X);
        Assert.Equal(0.5, r.Crop.Height, 6);
    }

    [Fact]
    public void Reset_single_adjustment_keeps_the_others()
    {
        var r = new EditRecipe { Exposure = 1, Contrast = 0.5 }.Reset(nameof(EditRecipe.Exposure));
        Assert.Equal(0, r.Exposure);
        Assert.Equal(0.5, r.Contrast);
        Assert.Throws<ArgumentException>(() => r.Reset("Nope"));
    }

    [Fact]
    public void Four_clockwise_rotations_return_to_the_same_crop()
    {
        var crop = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        var r = new EditRecipe { Crop = crop };
        var rotated = r.RotateClockwise().RotateClockwise().RotateClockwise().RotateClockwise();
        Assert.Equal(0, rotated.QuarterTurns);
        Assert.Equal(crop.X, rotated.Crop.X, 9);
        Assert.Equal(crop.Y, rotated.Crop.Y, 9);
        Assert.Equal(crop.Width, rotated.Crop.Width, 9);
        Assert.Equal(crop.Height, rotated.Crop.Height, 9);
    }

    [Fact]
    public void Clockwise_then_counter_clockwise_is_identity()
    {
        var r = new EditRecipe { Crop = new NormalizedRect(0.05, 0.1, 0.5, 0.25) };
        var back = r.RotateClockwise().RotateCounterClockwise();
        Assert.Equal(r.Crop.X, back.Crop.X, 9);
        Assert.Equal(r.Crop.Y, back.Crop.Y, 9);
        Assert.Equal(0, back.QuarterTurns);
    }

    [Fact]
    public void Clockwise_rotation_moves_left_strip_to_top()
    {
        // A crop of the left 20% becomes the top 20% after a clockwise quarter turn.
        var r = new EditRecipe { Crop = new NormalizedRect(0, 0, 0.2, 1) }.RotateClockwise();
        Assert.Equal(0, r.Crop.X, 9);
        Assert.Equal(0, r.Crop.Y, 9);
        Assert.Equal(1, r.Crop.Width, 9);
        Assert.Equal(0.2, r.Crop.Height, 9);
    }

    [Fact]
    public void Flip_mirrors_crop_and_straighten()
    {
        var r = new EditRecipe { Crop = new NormalizedRect(0.1, 0, 0.3, 1), StraightenDegrees = 5 }.ToggleFlipHorizontal();
        Assert.True(r.FlipHorizontal);
        Assert.Equal(0.6, r.Crop.X, 9);
        Assert.Equal(-5, r.StraightenDegrees);
    }

    [Fact]
    public void Presets_only_change_tone_not_geometry()
    {
        var baseRecipe = new EditRecipe { QuarterTurns = 1, Crop = new NormalizedRect(0.1, 0.1, 0.5, 0.5) };
        foreach (var (name, apply) in EditPresets.All)
        {
            var r = apply(baseRecipe);
            Assert.Equal(1, r.QuarterTurns);
            Assert.Equal(baseRecipe.Crop, r.Crop);
            if (name == "Original") Assert.False(r.HasTone);
        }
    }

    [Fact]
    public void Records_with_equal_values_are_equal()
    {
        Assert.Equal(new EditRecipe { Exposure = 0.5 }, new EditRecipe { Exposure = 0.5 });
    }
}
