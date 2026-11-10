using System.Globalization;
using PhotosWindows.Application.Places;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Memories;

/// <summary>
/// Turns a memory's facts into its title, subtitle and "why you are seeing this" text. It only formats what
/// <see cref="MemoryExplanation"/> holds: dates, counts, distances and coordinates. It never produces a place name.
/// Strings come from the app's resources (keys <c>Memory_*</c>), so the same code serves English and Spanish.
/// </summary>
public sealed class MemoryDescriber(Func<string, string> resource, CultureInfo culture)
{
    public string Title(MemoryCandidate m)
    {
        var why = m.Why;
        return m.Kind switch
        {
            MemoryKind.OnThisDay => F(why.YearsAgo == 1 ? "Memory_Title_OnThisDayOne" : "Memory_Title_OnThisDay", why.YearsAgo ?? 0),
            MemoryKind.Trip => F(why.Days == 1 ? "Memory_Title_TripOne" : "Memory_Title_Trip", why.Days ?? 1),
            MemoryKind.Event => F("Memory_Title_Event", Day(why.First)),
            MemoryKind.Month => MonthYear(why.First),
            MemoryKind.Season => why.Season is { } s
                ? F("Memory_Title_Season", resource("Memory_Season_" + s), SeasonYears(why))
                : why.First.Year == why.Last.Year && why.First.Month == why.Last.Month ? MonthYear(why.First) : $"{MonthYear(why.First)} – {MonthYear(why.Last)}",
            MemoryKind.Year => F("Memory_Title_Year", why.First.Year),
            MemoryKind.Person => F("Memory_Title_Person", why.PersonName ?? string.Empty),
            _ => string.Empty,
        };
    }

    /// <summary>Dates and item count, e.g. "1–4 Aug 2023 · 17 items".</summary>
    public string Subtitle(MemoryCandidate m) => $"{DateRange(m.Why.First, m.Why.Last)} · {Items(m.Why.Count)}";

    /// <summary>The explanation shown on the card: which rule matched and with which facts.</summary>
    public string Why(MemoryCandidate m)
    {
        var why = m.Why;
        var text = m.Kind switch
        {
            MemoryKind.OnThisDay => F(why.YearsAgo == 1 ? "Memory_Why_OnThisDayOne" : "Memory_Why_OnThisDay", Items(why.Matched), DateRange(why.First, why.Last), why.YearsAgo ?? 0),
            MemoryKind.Trip => F("Memory_Why_Trip", Items(why.Matched), Day(why.First), Day(why.Last), why.Days ?? 1,
                (why.DistanceFromUsualAreaKm ?? 0).ToString("N0", culture), Around(why)),
            MemoryKind.Event => F("Memory_Why_Event", Items(why.Matched), Day(why.First), Time(why.First), Time(why.Last)) + AroundSentence(why),
            MemoryKind.Month => F("Memory_Why_Month", Items(why.Matched), MonthYear(why.First)),
            MemoryKind.Season => F("Memory_Why_Season", Items(why.Matched), Day(why.First), Day(why.Last)) + " "
                + resource(why.Season is null ? "Memory_Why_SeasonUnknown" : "Memory_Why_SeasonKnown"),
            MemoryKind.Year => F("Memory_Why_Year", Items(why.Matched), why.First.Year),
            MemoryKind.Person => F("Memory_Why_Person", Items(why.Matched), why.PersonName ?? string.Empty),
            _ => string.Empty,
        };
        return why.IsSample ? text + " " + F("Memory_Why_Sample", why.Count) : text;
    }

    public string KindName(MemoryKind kind) => resource("Memory_Kind_" + kind);

    public string Items(int count) => F(count == 1 ? "Memory_OneItem" : "Memory_Items", count);

    public string DateRange(DateTime first, DateTime last) => first.Date == last.Date ? Day(first) : $"{Day(first)} – {Day(last)}";

    private string Around(MemoryExplanation why) =>
        why.Latitude is { } lat && why.Longitude is { } lon ? PlacesService.FormatCoordinate(lat, lon) : resource("Memory_UnknownArea");

    private string AroundSentence(MemoryExplanation why) =>
        why.Latitude is { } lat && why.Longitude is { } lon ? " " + F("Memory_Why_Around", PlacesService.FormatCoordinate(lat, lon)) : string.Empty;

    private string Day(DateTime d) => d.ToString("d MMM yyyy", culture);

    private string Time(DateTime d) => d.ToString("t", culture);

    private string MonthYear(DateTime d)
    {
        var text = d.ToString("Y", culture); // the culture's own month-year pattern ("June 2022", "junio de 2022")
        return char.ToUpper(text[0], culture) + text[1..];
    }

    private static string SeasonYears(MemoryExplanation why) =>
        why.First.Year == why.Last.Year ? why.First.Year.ToString(CultureInfo.InvariantCulture) : $"{why.First.Year}–{why.Last.Year}";

    private string F(string key, params object[] args) => string.Format(culture, resource(key), args);
}

/// <summary>
/// Slideshow position and play state, independent of any UI so it can be tested: play/pause, next/previous with
/// wrap-around, and the timer tick that advances only while playing.
/// </summary>
public sealed class SlideshowState
{
    public SlideshowState(int count, int startIndex = 0, bool playing = true)
    {
        Count = Math.Max(0, count);
        Index = Count == 0 ? 0 : Math.Clamp(startIndex, 0, Count - 1);
        IsPlaying = playing && Count > 1;
    }

    public int Count { get; }
    public int Index { get; private set; }
    public bool IsPlaying { get; private set; }

    /// <summary>"3 / 24" style position (1-based); 0 / 0 when empty.</summary>
    public (int Position, int Total) Position => (Count == 0 ? 0 : Index + 1, Count);

    public void Toggle() => IsPlaying = !IsPlaying && Count > 1;

    public void Pause() => IsPlaying = false;

    /// <summary>Manual step forward; wraps to the first item.</summary>
    public void Next()
    {
        if (Count > 0) Index = (Index + 1) % Count;
    }

    /// <summary>Manual step back; wraps to the last item.</summary>
    public void Previous()
    {
        if (Count > 0) Index = (Index - 1 + Count) % Count;
    }

    /// <summary>Timer tick: advances while playing. Returns whether the index changed.</summary>
    public bool Tick()
    {
        if (!IsPlaying || Count < 2) return false;
        Next();
        return true;
    }
}
