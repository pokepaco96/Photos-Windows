using System.Globalization;

namespace PhotosWindows.Domain;

/// <summary>
/// Builds memory candidates from catalog facts only (capture date, coordinates, favourite/edited flags and, when the
/// user enabled People, the people they named). Pure and deterministic: the same items, date and rules always give
/// the same candidates. Days are the local wall-clock days stored with each photo, so daylight-saving changes and
/// photos without a UTC offset never move a photo to another day.
/// </summary>
public static class MemoryCandidateGenerator
{
    public static IReadOnlyList<MemoryCandidate> Generate(
        IReadOnlyList<MemoryItem> items,
        DateOnly today,
        MemoryRules rules,
        IReadOnlyList<MemoryPerson>? people = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(rules);
        var todayStart = today.ToDateTime(TimeOnly.MinValue);
        // Photos dated in the future (wrong camera clock) cannot be memories.
        var sorted = items.Where(i => i.CaptureLocal < todayStart.AddDays(1)).OrderBy(i => i.CaptureLocal).ThenBy(i => i.AssetId).ToArray();
        var result = new List<MemoryCandidate>();
        if (sorted.Length == 0) return result;

        result.AddRange(OnThisDay(sorted, today, rules));
        result.AddRange(Trips(sorted, todayStart, rules));
        result.AddRange(Events(sorted, todayStart, rules));
        result.AddRange(Months(sorted, today, rules));
        result.AddRange(Seasons(sorted, today, rules));
        result.AddRange(Years(sorted, today, rules));
        if (people is not null) result.AddRange(People(sorted, people, rules));
        return result;
    }

    // ---------------------------------------------------------------- On this day

    private static IEnumerable<MemoryCandidate> OnThisDay(MemoryItem[] sorted, DateOnly today, MemoryRules rules)
    {
        var firstYear = sorted[0].CaptureLocal.Year;
        for (var year = today.Year - 1; year >= firstYear; year--)
        {
            var days = AnniversaryDays(today, year);
            if (days.Count == 0) continue;
            var from = days[0].AddDays(-rules.OnThisDayWindowDays).ToDateTime(TimeOnly.MinValue);
            var to = days[^1].AddDays(rules.OnThisDayWindowDays + 1).ToDateTime(TimeOnly.MinValue);
            var matched = Range(sorted, from, to);
            if (matched.Length < rules.OnThisDayMinItems) continue;
            yield return Build($"onthisday:{year:0000}-{today.Month:00}-{today.Day:00}", MemoryKind.OnThisDay, matched, rules,
                e => e with { YearsAgo = today.Year - year });
        }
    }

    /// <summary>
    /// The day(s) of <paramref name="year"/> that correspond to today. 29 February exists only in leap years: on a
    /// 29 Feb only leap years match; on 28 Feb of a non-leap year, 29 Feb of leap years is included too, so those
    /// photos get their day every year.
    /// </summary>
    internal static IReadOnlyList<DateOnly> AnniversaryDays(DateOnly today, int year)
    {
        if (year < 1 || year > 9999) return [];
        if (today is { Month: 2, Day: 29 })
        {
            return DateTime.IsLeapYear(year) ? [new DateOnly(year, 2, 29)] : [];
        }

        if (today is { Month: 2, Day: 28 } && !DateTime.IsLeapYear(today.Year) && DateTime.IsLeapYear(year))
        {
            return [new DateOnly(year, 2, 28), new DateOnly(year, 2, 29)];
        }

        return [new DateOnly(year, today.Month, today.Day)];
    }

    // ---------------------------------------------------------------- Events

    private static IEnumerable<MemoryCandidate> Events(MemoryItem[] sorted, DateTime todayStart, MemoryRules rules)
    {
        var start = 0;
        for (var i = 1; i <= sorted.Length; i++)
        {
            var split = i == sorted.Length;
            if (!split)
            {
                var (a, b) = (sorted[i - 1], sorted[i]);
                split = (b.CaptureLocal - a.CaptureLocal).TotalHours > rules.EventGapHours
                    || (a.HasLocation && b.HasLocation && Geo.DistanceKm(a.Latitude!.Value, a.Longitude!.Value, b.Latitude!.Value, b.Longitude!.Value) > rules.EventRadiusKm);
            }

            if (!split) continue;
            var session = sorted[start..i];
            start = i;
            if (session.Length < rules.EventMinItems) continue;
            if ((session[^1].CaptureLocal - session[0].CaptureLocal).TotalHours > rules.EventMaxHours) continue;
            if (session[^1].CaptureLocal >= todayStart) continue; // today's photos are not a memory yet
            yield return Build($"event:{session[0].CaptureLocal:yyyyMMddHHmm}", MemoryKind.Event, session, rules, e => e);
        }
    }

    // ---------------------------------------------------------------- Trips

    /// <summary>
    /// The usual area: the ~28 km cell where located photos were taken on the most different days. Returned only when
    /// there is enough evidence (see <see cref="MemoryRules.HomeMinDays"/> and <see cref="MemoryRules.HomeMinShare"/>).
    /// </summary>
    internal static (double Latitude, double Longitude)? UsualArea(IReadOnlyList<MemoryItem> items, MemoryRules rules)
    {
        var cells = new Dictionary<(long, long), HashSet<DateTime>>();
        var allDays = new HashSet<DateTime>();
        foreach (var i in items)
        {
            if (!i.HasLocation) continue;
            var cell = ((long)Math.Floor(i.Latitude!.Value * 4), (long)Math.Floor(i.Longitude!.Value * 4));
            if (!cells.TryGetValue(cell, out var days)) cells[cell] = days = [];
            days.Add(i.CaptureLocal.Date);
            allDays.Add(i.CaptureLocal.Date);
        }

        if (allDays.Count < rules.HomeMinDays) return null;
        var best = cells.OrderByDescending(c => c.Value.Count).ThenBy(c => c.Key.Item1).ThenBy(c => c.Key.Item2).First();
        if (best.Value.Count < rules.HomeMinShare * allDays.Count) return null;
        return Geo.Center(items.Where(i => i.HasLocation
                && (long)Math.Floor(i.Latitude!.Value * 4) == best.Key.Item1 && (long)Math.Floor(i.Longitude!.Value * 4) == best.Key.Item2)
            .Select(i => (i.Latitude!.Value, i.Longitude!.Value)));
    }

    private static IEnumerable<MemoryCandidate> Trips(MemoryItem[] sorted, DateTime todayStart, MemoryRules rules)
    {
        if (UsualArea(sorted, rules) is not { } home) yield break;

        // Classify each day that has located photos: away when most of them are far from the usual area.
        var days = sorted.Where(i => i.HasLocation).GroupBy(i => i.CaptureLocal.Date).OrderBy(g => g.Key)
            .Select(g =>
            {
                var distances = g.Select(i => Geo.DistanceKm(home.Latitude, home.Longitude, i.Latitude!.Value, i.Longitude!.Value)).ToArray();
                var away = distances.Count(d => d > rules.TripMinDistanceKm) >= 0.6 * distances.Length;
                return (Day: g.Key, Away: away, Median: Median(distances));
            })
            .ToArray();

        var run = new List<(DateTime Day, bool Away, double Median)>();
        foreach (var day in days.Append((Day: DateTime.MaxValue, Away: false, Median: 0d)))
        {
            var continues = day.Away && (run.Count == 0 || (day.Day - run[^1].Day).TotalDays <= rules.TripMaxGapDays + 1);
            if (continues)
            {
                run.Add(day);
                continue;
            }

            if (run.Count >= rules.TripMinDays)
            {
                var from = run[0].Day;
                var to = run[^1].Day.AddDays(1);
                var matched = Range(sorted, from, to);
                if (matched.Length >= rules.TripMinItems && to <= todayStart)
                {
                    var distance = Median(run.Select(r => r.Median).ToArray());
                    var totalDays = (int)(run[^1].Day - run[0].Day).TotalDays + 1;
                    yield return Build($"trip:{from:yyyyMMdd}-{run[^1].Day:yyyyMMdd}", MemoryKind.Trip, matched, rules,
                        e => e with { DistanceFromUsualAreaKm = Math.Round(distance), Days = totalDays });
                }
            }

            run.Clear();
            if (day.Away) run.Add(day);
        }
    }

    // ---------------------------------------------------------------- Months, seasons, years

    private static IEnumerable<MemoryCandidate> Months(MemoryItem[] sorted, DateOnly today, MemoryRules rules)
    {
        foreach (var g in sorted.GroupBy(i => (i.CaptureLocal.Year, i.CaptureLocal.Month)))
        {
            if (g.Key.Year == today.Year && g.Key.Month == today.Month) continue; // the current month is not finished
            var matched = g.ToArray();
            if (matched.Length < rules.MonthMinItems) continue;
            yield return Build($"month:{g.Key.Year:0000}-{g.Key.Month:00}", MemoryKind.Month, matched, rules, e => e);
        }
    }

    /// <summary>Meteorological quarter: 0 = Dec–Feb (belongs to the year of its January), 1 = Mar–May, 2 = Jun–Aug, 3 = Sep–Nov.</summary>
    internal static (int Year, int Quarter) Quarter(DateTime date) => date.Month switch
    {
        12 => (date.Year + 1, 0),
        1 or 2 => (date.Year, 0),
        <= 5 => (date.Year, 1),
        <= 8 => (date.Year, 2),
        _ => (date.Year, 3),
    };

    private static IEnumerable<MemoryCandidate> Seasons(MemoryItem[] sorted, DateOnly today, MemoryRules rules)
    {
        var current = Quarter(today.ToDateTime(TimeOnly.MinValue));
        foreach (var g in sorted.GroupBy(i => Quarter(i.CaptureLocal)))
        {
            if (g.Key == current) continue;
            var matched = g.ToArray();
            if (matched.Length < rules.SeasonMinItems) continue;
            // Name the season only when the photos' own coordinates establish the hemisphere; never assume it.
            SeasonOfYear? season = null;
            var located = matched.Where(i => i.HasLocation).ToArray();
            if (located.Length >= 0.5 * matched.Length && located.Length > 0)
            {
                var north = located.Count(i => i.Latitude > 0);
                if (north >= 0.9 * located.Length) season = (SeasonOfYear)g.Key.Quarter;
                else if (located.Length - north >= 0.9 * located.Length) season = (SeasonOfYear)((g.Key.Quarter + 2) % 4);
            }

            yield return Build($"season:{g.Key.Year:0000}-{g.Key.Quarter}", MemoryKind.Season, matched, rules, e => e with { Season = season });
        }
    }

    private static IEnumerable<MemoryCandidate> Years(MemoryItem[] sorted, DateOnly today, MemoryRules rules)
    {
        foreach (var g in sorted.GroupBy(i => i.CaptureLocal.Year))
        {
            if (g.Key >= today.Year) continue;
            var matched = g.ToArray();
            if (matched.Length < rules.YearMinItems) continue;
            if (matched.Select(i => i.CaptureLocal.Month).Distinct().Count() < rules.YearMinMonths) continue;
            yield return Build($"year:{g.Key:0000}", MemoryKind.Year, matched, rules, e => e);
        }
    }

    // ---------------------------------------------------------------- People (only with consent)

    private static IEnumerable<MemoryCandidate> People(MemoryItem[] sorted, IReadOnlyList<MemoryPerson> people, MemoryRules rules)
    {
        var byId = sorted.ToDictionary(i => i.AssetId);
        foreach (var person in people.OrderBy(p => p.PersonId))
        {
            if (string.IsNullOrWhiteSpace(person.Name)) continue; // never invent a name for an unnamed group
            var matched = person.AssetIds.Distinct().Where(byId.ContainsKey).Select(id => byId[id])
                .OrderBy(i => i.CaptureLocal).ThenBy(i => i.AssetId).ToArray();
            if (matched.Length < rules.PersonMinItems) continue;
            yield return Build($"person:{person.PersonId.ToString(CultureInfo.InvariantCulture)}", MemoryKind.Person, matched, rules,
                e => e with { PersonName = person.Name.Trim() });
        }
    }

    // ---------------------------------------------------------------- Shared

    private static MemoryItem[] Range(MemoryItem[] sorted, DateTime fromInclusive, DateTime toExclusive)
    {
        var lo = LowerBound(sorted, fromInclusive);
        var hi = LowerBound(sorted, toExclusive);
        return sorted[lo..hi];
    }

    private static int LowerBound(MemoryItem[] sorted, DateTime value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (sorted[mid].CaptureLocal < value) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy.Length % 2 == 1 ? copy[copy.Length / 2] : (copy[copy.Length / 2 - 1] + copy[copy.Length / 2]) / 2;
    }

    private static MemoryCandidate Build(string key, MemoryKind kind, MemoryItem[] matched, MemoryRules rules, Func<MemoryExplanation, MemoryExplanation> extend)
    {
        var chosen = Sample(matched, rules);
        var located = matched.Where(i => i.HasLocation).Select(i => (i.Latitude!.Value, i.Longitude!.Value)).ToArray();
        var center = located.Length > 0 && located.Length >= 0.5 * matched.Length ? Geo.Center(located) : null;
        var why = extend(new MemoryExplanation
        {
            Kind = kind,
            Count = chosen.Length,
            Matched = matched.Length,
            First = matched[0].CaptureLocal,
            Last = matched[^1].CaptureLocal,
            Latitude = center?.Latitude,
            Longitude = center?.Longitude,
        });
        return new MemoryCandidate(key, kind, matched[0].CaptureLocal, matched[^1].CaptureLocal, chosen.Select(i => i.AssetId).ToArray(), Cover(chosen, rules), why)
        {
            FavoriteShare = matched.Count(i => i.IsFavorite) / (double)matched.Length,
        };
    }

    /// <summary>Favourite first (then edited) when allowed; otherwise the photo in the middle of the memory.</summary>
    private static long Cover(MemoryItem[] chosen, MemoryRules rules)
    {
        if (rules.PreferFavorites)
        {
            foreach (var i in chosen) if (i.IsFavorite && !i.IsVideo) return i.AssetId;
            foreach (var i in chosen) if (i.IsEdited && !i.IsVideo) return i.AssetId;
        }

        var photos = chosen.Where(i => !i.IsVideo).ToArray();
        var pool = photos.Length > 0 ? photos : chosen;
        return pool[pool.Length / 2].AssetId;
    }

    /// <summary>
    /// Everything when it fits; otherwise a sample spread over the days of the memory (round-robin by day, favourites
    /// first when allowed), skipping bursts. The result is in chronological order.
    /// </summary>
    internal static MemoryItem[] Sample(MemoryItem[] matched, MemoryRules rules)
    {
        var max = Math.Max(1, rules.MaxItemsPerMemory);
        if (matched.Length <= max) return matched;

        var days = matched.GroupBy(i => i.CaptureLocal.Date).OrderBy(g => g.Key).Select(g =>
        {
            var ordered = rules.PreferFavorites
                ? g.OrderByDescending(i => i.IsFavorite).ThenByDescending(i => i.IsEdited).ThenBy(i => i.CaptureLocal).ThenBy(i => i.AssetId)
                : g.OrderBy(i => i.CaptureLocal).ThenBy(i => i.AssetId);
            return new Queue<MemoryItem>(ordered);
        }).ToList();

        var chosen = new List<MemoryItem>(max);
        var lastPerDay = new Dictionary<DateTime, List<DateTime>>();
        var skipped = new List<MemoryItem>();
        while (chosen.Count < max && days.Count > 0)
        {
            for (var d = 0; d < days.Count && chosen.Count < max; d++)
            {
                var queue = days[d];
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    var day = item.CaptureLocal.Date;
                    if (!lastPerDay.TryGetValue(day, out var times)) lastPerDay[day] = times = [];
                    if (times.Any(t => Math.Abs((t - item.CaptureLocal).TotalSeconds) < rules.BurstSeconds))
                    {
                        skipped.Add(item);
                        continue;
                    }

                    times.Add(item.CaptureLocal);
                    chosen.Add(item);
                    break;
                }
            }

            days.RemoveAll(q => q.Count == 0);
        }

        // Only bursts left: fill up with them rather than showing fewer photos than allowed.
        foreach (var item in skipped.OrderBy(i => i.CaptureLocal).ThenBy(i => i.AssetId))
        {
            if (chosen.Count >= max) break;
            chosen.Add(item);
        }

        return chosen.OrderBy(i => i.CaptureLocal).ThenBy(i => i.AssetId).ToArray();
    }
}

/// <summary>
/// Orders candidates and removes repetition. Deterministic for a given date: ties are broken by key, and the only
/// variation over time is a weekly rotation derived from the key and the ISO week, so the page is not identical forever.
/// </summary>
public static class MemoryRanker
{
    private static readonly Dictionary<MemoryKind, (double Base, int Cap)> Kinds = new()
    {
        [MemoryKind.OnThisDay] = (1.00, 5),
        [MemoryKind.Trip] = (0.80, 4),
        [MemoryKind.Event] = (0.70, 4),
        [MemoryKind.Person] = (0.60, 3),
        [MemoryKind.Year] = (0.55, 3),
        [MemoryKind.Season] = (0.50, 2),
        [MemoryKind.Month] = (0.45, 3),
    };

    public static int CapFor(MemoryKind kind) => Kinds[kind].Cap;

    public static IReadOnlyList<MemoryCandidate> Rank(
        IEnumerable<MemoryCandidate> candidates,
        DateOnly today,
        MemoryRules rules,
        IReadOnlySet<string>? hiddenKeys = null,
        IReadOnlySet<MemoryKind>? excludedKinds = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rules);
        var week = ISOWeek.GetYear(today.ToDateTime(TimeOnly.MinValue)) * 100 + ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue));
        var scored = candidates
            .Where(c => c.AssetIds.Count > 0)
            .Where(c => hiddenKeys is null || !hiddenKeys.Contains(c.Key))
            .Where(c => excludedKinds is null || !excludedKinds.Contains(c.Kind))
            .Select(c => c with { Score = Score(c, today, rules, week) })
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.Start).ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        var picked = new List<MemoryCandidate>();
        var sets = new List<HashSet<long>>();
        var perKind = new Dictionary<MemoryKind, int>();
        foreach (var c in scored)
        {
            if (picked.Count >= rules.MaxMemories) break;
            if (perKind.GetValueOrDefault(c.Kind) >= Kinds[c.Kind].Cap) continue;
            // An event that happened during an already chosen trip is part of that trip.
            if (c.Kind == MemoryKind.Event && picked.Any(p => p.Kind == MemoryKind.Trip && p.Start <= c.Start && c.End <= p.End.Date.AddDays(1))) continue;
            var set = c.AssetIds.ToHashSet();
            var repeats = false;
            for (var i = 0; i < picked.Count && !repeats; i++)
            {
                var smaller = Math.Min(set.Count, sets[i].Count);
                repeats = sets[i].Count(set.Contains) > rules.MaxOverlap * smaller;
            }

            if (repeats) continue;
            picked.Add(c);
            sets.Add(set);
            perKind[c.Kind] = perKind.GetValueOrDefault(c.Kind) + 1;
        }

        return picked;
    }

    internal static double Score(MemoryCandidate c, DateOnly today, MemoryRules rules, int week)
    {
        var score = Kinds[c.Kind].Base;
        score += 0.10 * Math.Min(1, Math.Log10(Math.Max(1, c.Why.Matched)) / 2);
        if (rules.PreferFavorites) score += 0.10 * c.FavoriteShare;

        // Things that happened around this time of year come first.
        if (c.Kind is MemoryKind.Trip or MemoryKind.Event)
        {
            var d = Math.Min(DayOfYearDistance(today, DateOnly.FromDateTime(c.Start)), DayOfYearDistance(today, DateOnly.FromDateTime(c.End)));
            if (d <= 7) score += 0.30 * (1 - d / 7.0);
        }
        else if (c.Kind == MemoryKind.Month && c.Start.Month == today.Month)
        {
            score += 0.25;
        }
        else if (c.Kind == MemoryKind.Season && MemoryCandidateGenerator.Quarter(c.Start).Quarter == MemoryCandidateGenerator.Quarter(today.ToDateTime(TimeOnly.MinValue)).Quarter)
        {
            score += 0.15;
        }

        // Weekly rotation (±0.05): stable within a week, different across weeks. FNV-1a, not string.GetHashCode,
        // so it is the same on every machine and run.
        if (c.Kind != MemoryKind.OnThisDay) score += (Fnv(c.Key, week) % 1000 / 1000.0 - 0.5) * 0.10;
        return score;
    }

    private static int DayOfYearDistance(DateOnly a, DateOnly b)
    {
        var d = Math.Abs(a.DayOfYear - b.DayOfYear);
        return Math.Min(d, 366 - d);
    }

    private static uint Fnv(string key, int salt)
    {
        var hash = 2166136261u;
        foreach (var ch in key)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        hash = (hash ^ (uint)salt) * 16777619u;
        return hash ^ (hash >> 15);
    }
}
