using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>The background thumbnail pass in the navigation pane: shown for a large import, with honest counts, then gone.</summary>
public sealed partial class EndToEndTests
{
    [GeneratedRegex(@"([\d,. ]+) of ([\d,. ]+)")]
    private static partial Regex ThumbsCountRegex();

    private static int ParseCount(string s) => int.Parse(Regex.Replace(s, @"[^\d]", string.Empty), CultureInfo.InvariantCulture);

    [Fact, Step(80)]
    public void S80_Thumbnail_progress_shows_counts_and_disappears_when_done() => Run(80, "Thumbnails: a 120-video import shows the progress bar with counts and an accessible name, then hides it when done", ["library"], null, () =>
    {
        if (!qa.Dataset.HasBulk) throw new StepBlockedException("the bulk video clips could not be made (ffmpeg not available)");
        Assert.False(App.Exists("ThumbsProgressBar"), "the thumbnail progress bar is shown before there is work");
        ImportFolder(qa.Inside(qa.Dataset.BulkFolder), copy: false);

        // The pass shows from 50 pending; it is sampled every 3 s. (The StackPanel around the bar is not in the UIA tree:
        // look for the bar and its text.) Everything is read at the moment the bar is seen: a fast pass may end soon after.
        string text = string.Empty, name = string.Empty;
        double value = -1, maximum = 0;
        App.WaitUntil(() =>
        {
            try
            {
                if (App.TryFindById("ThumbsProgressBar") is not { } bar || App.TryFindById("ThumbsProgressText") is not { } label) return false;
                name = bar.Properties.Name.ValueOrDefault ?? string.Empty;
                value = bar.Patterns.RangeValue.Pattern.Value.Value;
                maximum = bar.Patterns.RangeValue.Pattern.Maximum.Value;
                text = label.Properties.Name.ValueOrDefault ?? string.Empty;
                App.Screenshot("80_thumbs_progress");
                return text.Length > 0;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or FlaUI.Core.Exceptions.PropertyNotSupportedException or FlaUI.Core.Exceptions.PatternNotSupportedException)
            {
                return false; // the bar went away while it was being read
            }
        }, "the thumbnail progress bar", TimeSpan.FromSeconds(90));

        qa.Evidence.Log($"progress text: {text}; bar '{name}' value {value:0.0} of {maximum}");
        var m = ThumbsCountRegex().Match(text);
        if (!m.Success) throw new InvalidOperationException("No 'done of total' count in: " + text);
        var (done, total) = (ParseCount(m.Groups[1].Value), ParseCount(m.Groups[2].Value));
        if (done < 0 || done > total || total < 50 || total > QaDataset.BulkCount + 20)
        {
            throw new InvalidOperationException($"Implausible counts {done} of {total} for a {QaDataset.BulkCount}-clip import.");
        }

        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("The progress bar has no accessible name.");
        if (value is < 0 or > 100 || maximum != 100) throw new InvalidOperationException($"Progress value out of range: {value} of {maximum}");

        // Done: the bar goes away by itself.
        App.WaitUntil(() => !App.Exists("ThumbsProgressBar"), "the thumbnail progress bar to hide when the pass ends", TimeSpan.FromMinutes(8));
        App.Screenshot("80_thumbs_done");
        DismissActivity();
    });
}
