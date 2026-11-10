using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PhotosWindows.Application.Memories;
using PhotosWindows.Domain;

namespace PhotosWindows.Integration.Tests;

/// <summary>Titles and explanations built with the app's real string resources, in English and Spanish.</summary>
public sealed partial class MemoryTextTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotosWindows.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, string> Resources(string language) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "PhotosWindows.App", "Strings", language, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

    private static MemoryDescriber Describer(string language)
    {
        var strings = Resources(language);
        return new MemoryDescriber(key => strings[key], CultureInfo.GetCultureInfo(language)); // a missing key throws: every key must exist
    }

    private static MemoryCandidate M(MemoryKind kind, DateTime first, DateTime last, int count, int matched, Func<MemoryExplanation, MemoryExplanation>? extend = null)
    {
        var why = new MemoryExplanation { Kind = kind, Count = count, Matched = matched, First = first, Last = last };
        return new MemoryCandidate("k", kind, first, last, Enumerable.Range(1, count).Select(i => (long)i).ToArray(), 1, extend?.Invoke(why) ?? why);
    }

    private static IEnumerable<MemoryCandidate> Samples() =>
    [
        M(MemoryKind.OnThisDay, new DateTime(2021, 11, 10, 9, 0, 0), new DateTime(2021, 11, 10, 18, 0, 0), 4, 4, e => e with { YearsAgo = 5 }),
        M(MemoryKind.OnThisDay, new DateTime(2025, 11, 10, 9, 0, 0), new DateTime(2025, 11, 10, 18, 0, 0), 1, 1, e => e with { YearsAgo = 1 }),
        M(MemoryKind.Trip, new DateTime(2023, 8, 1, 10, 0, 0), new DateTime(2023, 8, 4, 12, 0, 0), 17, 17, e => e with { Days = 4, DistanceFromUsualAreaKm = 1503, Latitude = 38.72, Longitude = -9.14 }),
        M(MemoryKind.Trip, new DateTime(2023, 8, 1, 10, 0, 0), new DateTime(2023, 8, 4, 12, 0, 0), 12, 12, e => e with { Days = 4, DistanceFromUsualAreaKm = 300 }),
        M(MemoryKind.Event, new DateTime(2024, 6, 1, 19, 0, 0), new DateTime(2024, 6, 1, 19, 40, 0), 9, 9, e => e with { Latitude = -17.7134, Longitude = 178.065 }),
        M(MemoryKind.Event, new DateTime(2024, 6, 1, 19, 0, 0), new DateTime(2024, 6, 1, 19, 40, 0), 9, 9),
        M(MemoryKind.Month, new DateTime(2022, 6, 1), new DateTime(2022, 6, 30), 80, 220),
        M(MemoryKind.Season, new DateTime(2023, 12, 3), new DateTime(2024, 2, 20), 60, 60, e => e with { Season = SeasonOfYear.Winter }),
        M(MemoryKind.Season, new DateTime(2023, 12, 3), new DateTime(2024, 2, 20), 60, 60),
        M(MemoryKind.Year, new DateTime(2020, 1, 2), new DateTime(2020, 12, 30), 80, 900),
        M(MemoryKind.Person, new DateTime(2019, 1, 2), new DateTime(2024, 12, 30), 12, 12, e => e with { PersonName = "Ana" }),
    ];

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    public void Every_kind_has_title_subtitle_and_explanation_without_leftover_placeholders(string language)
    {
        var d = Describer(language);
        foreach (var m in Samples())
        {
            foreach (var text in new[] { d.Title(m), d.Subtitle(m), d.Why(m), d.KindName(m.Kind) })
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotMatch(Placeholder(), text);
                Assert.DoesNotContain("Memory_", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void English_texts_state_the_facts_and_never_a_place_name()
    {
        var d = Describer("en-US");
        var s = Samples().ToArray();
        Assert.Equal("On this day, 5 years ago", d.Title(s[0]));
        Assert.Equal("On this day, 1 year ago", d.Title(s[1]));
        Assert.Equal("10 Nov 2021 · 4 items", d.Subtitle(s[0]));
        Assert.Equal("10 Nov 2025 · 1 item", d.Subtitle(s[1]));
        Assert.Equal("4 items taken on 10 Nov 2021, 5 years ago today.", d.Why(s[0]));
        Assert.Equal("A 4-day trip", d.Title(s[2]));
        var trip = d.Why(s[2]);
        Assert.Contains("17 items taken between 1 Aug 2023 and 4 Aug 2023 (4 days), about 1,503 km", trip, StringComparison.Ordinal);
        Assert.Contains("around 38.72° N, 9.14° W", trip, StringComparison.Ordinal);
        Assert.Contains("no place names are looked up", trip, StringComparison.Ordinal);
        Assert.Contains("an area without enough coordinates", d.Why(s[3]), StringComparison.Ordinal);
        Assert.Equal("Moments of 1 Jun 2024", d.Title(s[4]));
        Assert.Contains("They are located around 17.71° S, 178.07° E.", d.Why(s[4]), StringComparison.Ordinal);
        Assert.DoesNotContain("located around", d.Why(s[5]), StringComparison.Ordinal);
        Assert.Equal("June 2022", d.Title(s[6]));
        Assert.EndsWith("Showing 80, spread across the days.", d.Why(s[6]), StringComparison.Ordinal);
        Assert.Equal("Winter 2023–2024", d.Title(s[7]));
        Assert.Equal("December 2023 – February 2024", d.Title(s[8])); // hemisphere unknown: the season is not named
        Assert.Contains("do not establish the hemisphere", d.Why(s[8]), StringComparison.Ordinal);
        Assert.Equal("2020 in review", d.Title(s[9]));
        Assert.Equal("With Ana", d.Title(s[10]));
        Assert.Contains("People is turned on for this library", d.Why(s[10]), StringComparison.Ordinal);
    }

    [Fact]
    public void Spanish_texts_use_spanish_dates_and_wording()
    {
        var d = Describer("es-ES");
        var s = Samples().ToArray();
        Assert.Equal("Tal día como hoy, hace 5 años", d.Title(s[0]));
        Assert.Equal("Un viaje de 4 días", d.Title(s[2]));
        Assert.Contains("1.503 km", d.Why(s[2]), StringComparison.Ordinal);
        Assert.Equal("Junio de 2022", d.Title(s[6]));
        Assert.Equal("Invierno de 2023–2024", d.Title(s[7]));
        Assert.Equal("Resumen de 2020", d.Title(s[9]));
        Assert.Equal("Con Ana", d.Title(s[10]));
        Assert.Equal("1 elemento", d.Items(1));
    }

    [Fact]
    public void English_and_spanish_resources_have_the_same_keys_and_placeholders()
    {
        var en = Resources("en-US");
        var es = Resources("es-ES");
        Assert.Empty(en.Keys.Except(es.Keys));
        Assert.Empty(es.Keys.Except(en.Keys));
        var mismatched = en.Keys.Where(k => !Placeholder().Matches(en[k]).Select(m => m.Value).Order().Distinct()
            .SequenceEqual(Placeholder().Matches(es[k]).Select(m => m.Value).Order().Distinct())).ToList();
        Assert.Empty(mismatched);
    }

    [Fact]
    public void Slideshow_plays_pauses_steps_wraps_and_handles_empty_and_single_lists()
    {
        var s = new SlideshowState(3);
        Assert.True(s.IsPlaying);
        Assert.Equal((1, 3), s.Position);
        Assert.True(s.Tick());
        Assert.Equal(1, s.Index);
        s.Toggle();                       // pause
        Assert.False(s.IsPlaying);
        Assert.False(s.Tick());           // paused: the timer does not advance
        Assert.Equal(1, s.Index);
        s.Next();
        s.Next();                         // wraps to the first
        Assert.Equal(0, s.Index);
        s.Previous();                     // wraps to the last
        Assert.Equal((3, 3), s.Position);
        s.Toggle();                       // play again
        Assert.True(s.Tick());
        Assert.Equal(0, s.Index);
        s.Pause();                        // exit pauses
        Assert.False(s.IsPlaying);

        var one = new SlideshowState(1);
        Assert.False(one.IsPlaying);      // nothing to advance to
        one.Toggle();
        Assert.False(one.IsPlaying);
        one.Next();
        Assert.Equal((1, 1), one.Position);

        var none = new SlideshowState(0);
        none.Next();
        none.Previous();
        Assert.False(none.Tick());
        Assert.Equal((0, 0), none.Position);
        Assert.Equal(2, new SlideshowState(3, startIndex: 99).Index);
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
