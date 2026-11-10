using PhotosWindows.Domain;

namespace PhotosWindows.Domain.Tests;

public sealed class MemoryGeneratorTests
{
    private static readonly MemoryRules Rules = new();
    private static long _id;

    private static MemoryItem P(DateTime t, double? lat = null, double? lon = null, bool fav = false, bool video = false) =>
        new(Interlocked.Increment(ref _id), t, lat, lon, fav, false, video);

    private static List<MemoryItem> Burst(DateTime start, int n, double? lat = null, double? lon = null, int minutes = 10) =>
        Enumerable.Range(0, n).Select(i => P(start.AddMinutes(i * minutes), lat, lon)).ToList();

    private static IReadOnlyList<MemoryCandidate> Of(IReadOnlyList<MemoryCandidate> all, MemoryKind kind) => all.Where(c => c.Kind == kind).ToList();

    // ------------------------------------------------------------ On this day

    [Fact]
    public void On_this_day_groups_previous_years_and_explains_how_long_ago()
    {
        var items = new List<MemoryItem>();
        items.AddRange(Burst(new DateTime(2021, 11, 10, 9, 0, 0), 4));
        items.AddRange(Burst(new DateTime(2019, 11, 10, 18, 0, 0), 3));
        items.AddRange(Burst(new DateTime(2020, 11, 10, 18, 0, 0), 2));  // below the minimum
        items.AddRange(Burst(new DateTime(2026, 11, 10, 8, 0, 0), 5));   // today: not a memory
        items.AddRange(Burst(new DateTime(2021, 11, 11, 0, 0, 0), 5));   // the day after
        var found = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules), MemoryKind.OnThisDay);
        Assert.Equal(["onthisday:2021-11-10", "onthisday:2019-11-10"], found.Select(c => c.Key));
        Assert.Equal(5, found[0].Why.YearsAgo);
        Assert.Equal(7, found[1].Why.YearsAgo);
        Assert.Equal((4, 4), (found[0].Why.Count, found[0].Why.Matched));
        Assert.All(found[0].AssetIds, id => Assert.Equal(new DateTime(2021, 11, 10), items.Single(i => i.AssetId == id).CaptureLocal.Date));
    }

    [Fact]
    public void Day_boundaries_use_the_local_wall_clock_including_dst_nights_and_year_ends()
    {
        var items = new List<MemoryItem>
        {
            P(new DateTime(2023, 3, 26, 0, 0, 0)), P(new DateTime(2023, 3, 26, 2, 30, 0)), // 02:30 does not exist in Madrid that night
            P(new DateTime(2023, 3, 26, 23, 59, 59)),
            P(new DateTime(2023, 3, 25, 23, 59, 59)), P(new DateTime(2023, 3, 27, 0, 0, 0)),
            P(new DateTime(2022, 12, 31, 23, 59, 59)), P(new DateTime(2022, 12, 31, 12, 0, 0)), P(new DateTime(2022, 12, 31, 0, 0, 0)),
            P(new DateTime(2023, 1, 1, 0, 0, 0)),
        };
        var dst = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 3, 26), Rules), MemoryKind.OnThisDay).Single();
        Assert.Equal(3, dst.Why.Count);
        var nye = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 12, 31), Rules), MemoryKind.OnThisDay).Single();
        Assert.Equal(3, nye.Why.Count);
        Assert.Empty(Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 1, 1), Rules), MemoryKind.OnThisDay));
    }

    [Fact]
    public void Leap_day_photos_appear_on_29_feb_and_on_28_feb_of_ordinary_years()
    {
        var items = Burst(new DateTime(2024, 2, 29, 10, 0, 0), 3);
        items.AddRange(Burst(new DateTime(2023, 2, 28, 10, 0, 0), 3));
        // 28 Feb 2026 (not a leap year): 2023-02-28 and both days of 2024.
        var ordinary = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 2, 28), Rules), MemoryKind.OnThisDay);
        Assert.Equal(["onthisday:2024-02-28", "onthisday:2023-02-28"], ordinary.Select(c => c.Key));
        // 29 Feb 2028: only leap years have that day.
        var leap = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2028, 2, 29), Rules), MemoryKind.OnThisDay);
        Assert.Equal(["onthisday:2024-02-29"], leap.Select(c => c.Key));
        // 28 Feb 2028 (leap year): 29 Feb will have its own day tomorrow.
        var before = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2028, 2, 28), Rules), MemoryKind.OnThisDay);
        Assert.Equal(["onthisday:2023-02-28"], before.Select(c => c.Key));
        Assert.Equal([new DateOnly(2023, 2, 28)], MemoryCandidateGenerator.AnniversaryDays(new DateOnly(2027, 2, 28), 2023));
        Assert.Empty(MemoryCandidateGenerator.AnniversaryDays(new DateOnly(2028, 2, 29), 2023));
    }

    [Fact]
    public void Window_widens_on_this_day()
    {
        var items = new List<MemoryItem> { P(new DateTime(2022, 6, 9, 10, 0, 0)), P(new DateTime(2022, 6, 10, 10, 0, 0)), P(new DateTime(2022, 6, 11, 10, 0, 0)) };
        Assert.Empty(Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 6, 10), Rules), MemoryKind.OnThisDay));
        Assert.Single(Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 6, 10), Rules with { OnThisDayWindowDays = 1 }), MemoryKind.OnThisDay));
    }

    // ------------------------------------------------------------ Events

    [Fact]
    public void Events_split_on_time_gaps_and_on_distance()
    {
        var items = new List<MemoryItem>();
        items.AddRange(Burst(new DateTime(2024, 5, 4, 17, 0, 0), 9, 40.4168, -3.7038));           // one evening in one place
        items.AddRange(Burst(new DateTime(2024, 5, 5, 11, 0, 0), 5, 40.4168, -3.7038));           // too few
        items.AddRange(Burst(new DateTime(2024, 7, 1, 10, 0, 0), 8, 41.3874, 2.1686));            // same time span...
        items.AddRange(Burst(new DateTime(2024, 7, 1, 11, 20, 0), 8, 41.9794, 2.8214));           // ...but 90 km away: another event
        var events = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules), MemoryKind.Event);
        Assert.Equal(["event:202405041700", "event:202407011000", "event:202407011120"], events.Select(e => e.Key));
        Assert.Equal(40.4168, events[0].Why.Latitude!.Value, 3);
    }

    [Fact]
    public void Noisy_small_groups_and_photos_without_gps_do_not_make_events_or_trips_up()
    {
        var rnd = new Random(3);
        var items = Enumerable.Range(0, 60).Select(i => P(new DateTime(2022, 1, 1).AddDays(i * 6).AddHours(rnd.Next(8, 20)))).ToList(); // one photo every six days
        var all = MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules);
        Assert.Empty(Of(all, MemoryKind.Event));
        Assert.Empty(Of(all, MemoryKind.Trip));
        Assert.All(all, c => Assert.Null(c.Why.Latitude)); // no coordinates: nothing about places is claimed
    }

    [Fact]
    public void Event_without_gps_is_still_found_by_time_alone()
    {
        var e = Of(MemoryCandidateGenerator.Generate(Burst(new DateTime(2023, 2, 2, 20, 0, 0), 10), new DateOnly(2026, 11, 10), Rules), MemoryKind.Event).Single();
        Assert.Null(e.Why.Latitude);
        Assert.Equal(10, e.Why.Count);
    }

    // ------------------------------------------------------------ Trips

    private static List<MemoryItem> HomeLife(int days = 30)
    {
        var items = new List<MemoryItem>();
        for (var d = 0; d < days; d++) items.Add(P(new DateTime(2023, 1, 1, 12, 0, 0).AddDays(d * 5), 40.41 + d % 3 * 0.01, -3.70));
        return items;
    }

    [Fact]
    public void Trip_needs_a_usual_area_several_days_away_and_enough_photos()
    {
        var items = HomeLife();
        for (var d = 0; d < 4; d++) items.AddRange(Burst(new DateTime(2023, 8, 1, 10, 0, 0).AddDays(d), 4, 38.72, -9.14, 30)); // Lisbon, 4 days
        items.Add(P(new DateTime(2023, 8, 2, 22, 0, 0)));                                                                   // no GPS, during the trip
        items.AddRange(Burst(new DateTime(2023, 9, 9, 10, 0, 0), 12, 41.38, 2.17));                                          // one day away: an outing, not a trip
        var all = MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules);
        var trip = Of(all, MemoryKind.Trip).Single();
        Assert.Equal("trip:20230801-20230804", trip.Key);
        Assert.Equal(17, trip.Why.Count);
        Assert.Equal(4, trip.Why.Days);
        Assert.InRange(trip.Why.DistanceFromUsualAreaKm!.Value, 480, 520); // Madrid–Lisbon ≈ 502 km
        Assert.Equal(38.72, trip.Why.Latitude!.Value, 1);
    }

    [Fact]
    public void No_trips_are_inferred_without_a_clear_usual_area()
    {
        // Photos spread evenly over five far-apart places: no place holds 40 % of the days.
        var places = new[] { (40.4, -3.7), (48.8, 2.3), (52.5, 13.4), (41.9, 12.5), (38.7, -9.1) };
        var items = new List<MemoryItem>();
        for (var d = 0; d < 40; d++) items.AddRange(Burst(new DateTime(2023, 1, 1, 10, 0, 0).AddDays(d), 4, places[d / 2 % 5].Item1, places[d / 2 % 5].Item2));
        Assert.Null(MemoryCandidateGenerator.UsualArea(items, Rules));
        Assert.Empty(Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules), MemoryKind.Trip));
        // Too few located days: no usual area either.
        Assert.Null(MemoryCandidateGenerator.UsualArea(HomeLife(5), Rules));
    }

    [Fact]
    public void Trip_across_the_antimeridian_is_one_trip_centred_near_180()
    {
        var items = HomeLife();
        items.AddRange(Burst(new DateTime(2023, 8, 1, 9, 0, 0), 6, -17.7134, 178.065, 30));  // Fiji
        items.AddRange(Burst(new DateTime(2023, 8, 2, 9, 0, 0), 6, -16.5, 179.9, 30));
        items.AddRange(Burst(new DateTime(2023, 8, 3, 9, 0, 0), 6, -14.0, -179.5, 30));      // Samoa side
        var trip = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules), MemoryKind.Trip).Single();
        Assert.Equal("trip:20230801-20230803", trip.Key);
        Assert.True(Math.Abs(trip.Why.Longitude!.Value) > 178, $"centre longitude {trip.Why.Longitude}");
        Assert.InRange(trip.Why.Latitude!.Value, -18, -14);
    }

    // ------------------------------------------------------------ Months, seasons, years

    [Fact]
    public void Months_seasons_and_years_need_enough_photos_and_skip_the_unfinished_period()
    {
        var items = new List<MemoryItem>();
        items.AddRange(Burst(new DateTime(2024, 7, 1, 9, 0, 0), 30, 40.4, -3.7, 24 * 60));  // July 2024, north
        items.AddRange(Burst(new DateTime(2024, 1, 2, 9, 0, 0), 30, -33.9, 151.2, 24 * 60)); // January 2024, Sydney
        items.AddRange(Burst(new DateTime(2024, 10, 1, 9, 0, 0), 30, minutes: 24 * 60));     // October 2024, no GPS
        items.AddRange(Burst(new DateTime(2026, 11, 1, 9, 0, 0), 30, minutes: 60));          // current month
        var all = MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules);
        Assert.Equal(["month:2024-01", "month:2024-07", "month:2024-10"], Of(all, MemoryKind.Month).Select(c => c.Key).Order());
        var seasons = Of(all, MemoryKind.Season).ToDictionary(c => c.Key);
        Assert.Equal(SeasonOfYear.Summer, seasons["season:2024-2"].Why.Season);   // Jun–Aug in the north
        Assert.Equal(SeasonOfYear.Summer, seasons["season:2024-0"].Why.Season);   // Dec–Feb in the south is summer
        Assert.Null(seasons["season:2024-3"].Why.Season);                         // no coordinates: the hemisphere is not assumed
        Assert.Equal(["year:2024"], Of(all, MemoryKind.Year).Select(c => c.Key));
        // The unfinished month, season and year are skipped (a finished event earlier this month is still valid).
        Assert.DoesNotContain(all, c => (c.Kind is MemoryKind.Month or MemoryKind.Season or MemoryKind.Year) && c.Key.Contains("2026", StringComparison.Ordinal));
    }

    [Fact]
    public void December_belongs_to_the_winter_of_the_following_january()
    {
        Assert.Equal((2024, 0), MemoryCandidateGenerator.Quarter(new DateTime(2023, 12, 31)));
        Assert.Equal((2024, 0), MemoryCandidateGenerator.Quarter(new DateTime(2024, 2, 29)));
        Assert.Equal((2024, 1), MemoryCandidateGenerator.Quarter(new DateTime(2024, 3, 1)));
        Assert.Equal((2024, 3), MemoryCandidateGenerator.Quarter(new DateTime(2024, 11, 30)));
    }

    [Fact]
    public void Large_groups_are_sampled_across_days_with_favourites_first_and_bursts_skipped()
    {
        var items = new List<MemoryItem>();
        for (var d = 0; d < 20; d++)
        {
            var day = new DateTime(2024, 7, 1, 9, 0, 0).AddDays(d);
            items.AddRange(Enumerable.Range(0, 10).Select(i => P(day.AddSeconds(i))));  // a burst
            items.Add(P(day.AddHours(5), fav: d == 3));
        }

        var month = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules with { MaxItemsPerMemory = 40 }), MemoryKind.Month).Single();
        Assert.Equal((40, 220), (month.Why.Count, month.Why.Matched));
        Assert.True(month.Why.IsSample);
        var chosen = month.AssetIds.Select(id => items.Single(i => i.AssetId == id)).ToList();
        Assert.Equal(20, chosen.Select(i => i.CaptureLocal.Date).Distinct().Count());   // every day represented
        Assert.Equal(chosen.OrderBy(i => i.CaptureLocal).Select(i => i.AssetId), month.AssetIds);
        var favourite = items.Single(i => i.IsFavorite);
        Assert.Equal(favourite.AssetId, month.CoverAssetId);
        // Without the preference the cover is the middle photo, not the favourite.
        var plain = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules with { MaxItemsPerMemory = 40, PreferFavorites = false }), MemoryKind.Month).Single();
        Assert.NotEqual(favourite.AssetId, plain.CoverAssetId);
    }

    // ------------------------------------------------------------ People, edge cases

    [Fact]
    public void People_memories_only_exist_when_people_are_supplied_and_named()
    {
        var items = Burst(new DateTime(2022, 3, 1, 9, 0, 0), 30, minutes: 24 * 60);
        var ids = items.Select(i => i.AssetId).ToList();
        Assert.Empty(Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules), MemoryKind.Person));
        var people = new[]
        {
            new MemoryPerson(7, "Ana", ids.Take(12).ToList()),
            new MemoryPerson(8, " ", ids.Take(20).ToList()),         // unnamed group: never shown
            new MemoryPerson(9, "Luis", ids.Take(4).ToList()),       // too few
            new MemoryPerson(10, "Eva", [.. ids.Skip(15).Take(10), 999_999]), // an id that is not visible is ignored
        };
        var found = Of(MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules, people), MemoryKind.Person);
        Assert.Equal(["person:7", "person:10"], found.Select(c => c.Key));
        Assert.Equal("Ana", found[0].Why.PersonName);
        Assert.Equal(10, found[1].Why.Count);
    }

    [Fact]
    public void Empty_tiny_and_future_dated_libraries_give_no_candidates_and_never_empty_ones()
    {
        Assert.Empty(MemoryCandidateGenerator.Generate([], new DateOnly(2026, 11, 10), Rules));
        Assert.Empty(MemoryCandidateGenerator.Generate([P(new DateTime(2020, 11, 10))], new DateOnly(2026, 11, 10), Rules));
        Assert.Empty(MemoryCandidateGenerator.Generate(Burst(new DateTime(2030, 11, 10, 9, 0, 0), 50), new DateOnly(2026, 11, 10), Rules));
        var rnd = new Random(11);
        var items = Enumerable.Range(0, 3000).Select(_ => P(new DateTime(2015, 1, 1).AddMinutes(rnd.Next(0, 6_000_000)),
            rnd.Next(3) == 0 ? null : 40 + rnd.NextDouble(), rnd.Next(3) == 0 ? null : -3 + rnd.NextDouble(), rnd.Next(10) == 0)).ToList();
        var all = MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules);
        Assert.NotEmpty(all);
        Assert.All(all, c =>
        {
            Assert.NotEmpty(c.AssetIds);
            Assert.Contains(c.CoverAssetId, c.AssetIds);
            Assert.Equal(c.AssetIds.Count, c.AssetIds.Distinct().Count());
            Assert.True(c.Why.Count <= Rules.MaxItemsPerMemory);
            Assert.True(c.Start <= c.End);
        });
        Assert.Equal(all.Count, all.Select(c => c.Key).Distinct().Count());
    }

    [Fact]
    public void Generation_is_deterministic_regardless_of_input_order()
    {
        var rnd = new Random(5);
        var items = Enumerable.Range(0, 2000).Select(_ => P(new DateTime(2018, 1, 1).AddMinutes(rnd.Next(0, 4_000_000)), 40 + rnd.NextDouble(), -3 + rnd.NextDouble())).ToList();
        var a = MemoryCandidateGenerator.Generate(items, new DateOnly(2026, 11, 10), Rules);
        var shuffled = items.OrderBy(_ => rnd.Next()).ToList();
        var b = MemoryCandidateGenerator.Generate(shuffled, new DateOnly(2026, 11, 10), Rules);
        Assert.Equal(a.Select(c => (c.Key, c.CoverAssetId, string.Join(',', c.AssetIds))), b.Select(c => (c.Key, c.CoverAssetId, string.Join(',', c.AssetIds))));
    }
}

public sealed class MemoryRankerTests
{
    private static readonly MemoryRules Rules = new();

    private static MemoryCandidate C(string key, MemoryKind kind, DateTime start, DateTime end, params long[] ids) =>
        new(key, kind, start, end, ids, ids[0], new MemoryExplanation { Kind = kind, Count = ids.Length, Matched = ids.Length, First = start, Last = end });

    private static long[] Ids(int from, int count) => Enumerable.Range(from, count).Select(i => (long)i).ToArray();

    [Fact]
    public void On_this_day_comes_first_and_kinds_are_capped()
    {
        var today = new DateOnly(2026, 11, 10);
        var list = new List<MemoryCandidate> { C("onthisday:2020-11-10", MemoryKind.OnThisDay, new DateTime(2020, 11, 10), new DateTime(2020, 11, 10), Ids(1, 5)) };
        for (var m = 1; m <= 12; m++) list.Add(C($"month:2024-{m:00}", MemoryKind.Month, new DateTime(2024, m, 1), new DateTime(2024, m, 28), Ids(1000 * m, 30)));
        var ranked = MemoryRanker.Rank(list, today, Rules);
        Assert.Equal("onthisday:2020-11-10", ranked[0].Key);
        Assert.Equal(MemoryRanker.CapFor(MemoryKind.Month), ranked.Count(c => c.Kind == MemoryKind.Month));
        Assert.Contains(ranked, c => c.Key == "month:2024-11"); // the same month as today is boosted
        Assert.True(ranked.Zip(ranked.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    [Fact]
    public void Overlapping_memories_are_deduplicated_and_events_inside_a_chosen_trip_are_dropped()
    {
        var today = new DateOnly(2026, 8, 2);
        var trip = C("trip:20230801-20230805", MemoryKind.Trip, new DateTime(2023, 8, 1, 9, 0, 0), new DateTime(2023, 8, 5, 20, 0, 0), Ids(1, 40));
        var inside = C("event:202308021000", MemoryKind.Event, new DateTime(2023, 8, 2, 10, 0, 0), new DateTime(2023, 8, 2, 13, 0, 0), Ids(500, 10));
        var month = C("month:2023-08", MemoryKind.Month, new DateTime(2023, 8, 1), new DateTime(2023, 8, 31), Ids(1, 45)); // mostly the trip
        var other = C("event:202208020900", MemoryKind.Event, new DateTime(2022, 8, 2, 9, 0, 0), new DateTime(2022, 8, 2, 12, 0, 0), Ids(900, 10));
        var ranked = MemoryRanker.Rank([month, inside, other, trip], today, Rules);
        Assert.Equal(["trip:20230801-20230805", "event:202208020900"], ranked.Select(c => c.Key).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public void Hidden_keys_and_excluded_kinds_are_left_out_and_others_take_their_place()
    {
        var today = new DateOnly(2026, 11, 10);
        var list = Enumerable.Range(1, 6).Select(m => C($"month:2024-{m:00}", MemoryKind.Month, new DateTime(2024, m, 1), new DateTime(2024, m, 28), Ids(1000 * m, 30))).ToList();
        list.Add(C("year:2024", MemoryKind.Year, new DateTime(2024, 1, 1), new DateTime(2024, 12, 31), Ids(50_000, 60)));
        var first = MemoryRanker.Rank(list, today, Rules);
        var hidden = first.First(c => c.Kind == MemoryKind.Month).Key;
        var second = MemoryRanker.Rank(list, today, Rules, new HashSet<string> { hidden });
        Assert.DoesNotContain(second, c => c.Key == hidden);
        Assert.Equal(first.Count, second.Count); // another month fills the slot
        var noMonths = MemoryRanker.Rank(list, today, Rules, null, new HashSet<MemoryKind> { MemoryKind.Month });
        Assert.Equal(["year:2024"], noMonths.Select(c => c.Key));
    }

    [Fact]
    public void Ranking_is_stable_within_a_week_and_rotates_between_weeks()
    {
        var list = Enumerable.Range(2010, 12).Select(y => C($"year:{y}", MemoryKind.Year, new DateTime(y, 1, 1), new DateTime(y, 12, 31), Ids(y * 100, 50))).ToList();
        var monday = MemoryRanker.Rank(list, new DateOnly(2026, 11, 9), Rules).Select(c => c.Key).ToList();
        var sunday = MemoryRanker.Rank(list.AsEnumerable().Reverse(), new DateOnly(2026, 11, 15), Rules).Select(c => c.Key).ToList();
        Assert.Equal(monday, sunday);
        var weeks = Enumerable.Range(0, 8).Select(w => string.Join(',', MemoryRanker.Rank(list, new DateOnly(2026, 11, 9).AddDays(7 * w), Rules).Select(c => c.Key))).Distinct().Count();
        Assert.True(weeks > 1, "the selection never changes from week to week");
    }

    [Fact]
    public void Favourites_only_count_when_the_user_allows_it()
    {
        var a = C("year:2020", MemoryKind.Year, new DateTime(2020, 1, 1), new DateTime(2020, 12, 31), Ids(1, 50)) with { FavoriteShare = 1 };
        var with = MemoryRanker.Score(a, new DateOnly(2026, 11, 10), Rules, 1);
        var without = MemoryRanker.Score(a, new DateOnly(2026, 11, 10), Rules with { PreferFavorites = false }, 1);
        Assert.Equal(0.10, with - without, 6);
    }

    [Fact]
    public void Limits_are_respected()
    {
        var list = new List<MemoryCandidate>();
        for (var i = 0; i < 40; i++) list.Add(C($"event:{i:000}", MemoryKind.Event, new DateTime(2020, 1, 1).AddDays(i * 9), new DateTime(2020, 1, 1).AddDays(i * 9).AddHours(2), Ids(i * 100, 10)));
        Assert.Equal(MemoryRanker.CapFor(MemoryKind.Event), MemoryRanker.Rank(list, new DateOnly(2026, 11, 10), Rules).Count);
        Assert.Equal(2, MemoryRanker.Rank(list, new DateOnly(2026, 11, 10), Rules with { MaxMemories = 2 }).Count);
    }
}

public sealed class GeoMathTests
{
    [Fact]
    public void Distances_and_centres_are_right_across_the_antimeridian()
    {
        Assert.InRange(Geo.DistanceKm(40.4168, -3.7038, 38.7223, -9.1393), 500, 505);
        Assert.InRange(Geo.DistanceKm(0, 179.5, 0, -179.5), 110, 112); // 1° at the equator, not 359°
        var c = Geo.Center([(-17.0, 179.0), (-17.0, -179.0)])!.Value;
        Assert.Equal(180, Math.Abs(c.Longitude), 6);
        Assert.Null(Geo.Center([]));
        Assert.Null(Geo.Center([(0, 0), (0, 180)]));
    }
}
