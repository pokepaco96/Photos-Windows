using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Organization;
using PhotosWindows.Application.People;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Memories;

/// <summary>What the Memories page shows: the ranked memories and whether people were used.</summary>
public sealed record MemoriesResult(IReadOnlyList<MemoryCandidate> Memories, DateOnly Today, int ItemsConsidered, bool PeopleUsed, DateTime GeneratedUtc);

/// <summary>
/// Local, deterministic memories. Reads catalog facts only (never the original files), applies the user's
/// exclusions, generates candidates, ranks them and remembers what the user hid. Nothing leaves the machine.
/// Results are cached in memory per library and day; any library change or an explicit request regenerates them.
/// </summary>
public sealed class MemoriesService
{
    private readonly IMemoryRepository _memories;
    private readonly PeopleService _people;
    private readonly OrganizationService _organization;
    private readonly ILibraryContext _context;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly Lock _lock = new();
    private (Guid Library, DateOnly Today, bool PreferFavorites, MemoriesResult Result)? _cache;

    public MemoriesService(IMemoryRepository memories, PeopleService people, OrganizationService organization, ILibraryContext context, ISettingsStore settings, IClock clock)
    {
        _memories = memories;
        _people = people;
        _organization = organization;
        _context = context;
        _settings = settings;
        _clock = clock;
        _organization.LibraryContentChanged += (_, _) => Invalidate();
        _people.Changed += (_, _) => Invalidate();
        _context.LibraryChanged += (_, _) => Invalidate();
    }

    /// <summary>Rules in use. Tests and future settings can replace them; <see cref="MemoryRules.PreferFavorites"/> follows the user's setting.</summary>
    public MemoryRules Rules { get; set; } = new();

    /// <summary>Today in the user's time zone (the day "On this day" refers to).</summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.UtcNow, _clock.LocalZone));

    public void Invalidate()
    {
        lock (_lock) _cache = null;
    }

    public async Task<MemoriesResult> GetAsync(bool regenerate, CancellationToken cancellationToken)
    {
        var library = _context.RequireCurrent();
        var today = Today;
        var rules = Rules with { PreferFavorites = _settings.Load().MemoriesPreferFavorites };
        if (!regenerate)
        {
            lock (_lock)
            {
                if (_cache is { } c && c.Library == library.Id && c.Today == today && c.PreferFavorites == rules.PreferFavorites) return c.Result;
            }
        }

        var items = await _memories.GetItemsAsync(cancellationToken).ConfigureAwait(false);
        var exclusions = await _memories.GetExclusionsAsync(cancellationToken).ConfigureAwait(false);
        var hidden = (await _memories.GetHiddenAsync(cancellationToken).ConfigureAwait(false)).Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var excludedKinds = exclusions.Where(e => e.Kind is not null).Select(e => e.Kind!.Value).ToHashSet();
        var ranges = exclusions.Where(e => e.Kind is null).ToArray();
        if (ranges.Length > 0) items = items.Where(i => !ranges.Any(r => r.Covers(i.CaptureLocal))).ToList();

        // People are used only when the user turned People on for this library (which records their consent)
        // and did not exclude people memories. Memories never turns face analysis on or runs it.
        IReadOnlyList<MemoryPerson>? people = null;
        if (!excludedKinds.Contains(MemoryKind.Person) && await _people.IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            people = await _memories.GetNamedPeopleAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await Task.Run(() =>
        {
            var candidates = MemoryCandidateGenerator.Generate(items, today, rules, people);
            var ranked = MemoryRanker.Rank(candidates, today, rules, hidden, excludedKinds);
            return new MemoriesResult(ranked, today, items.Count, people is not null, _clock.UtcNow);
        }, cancellationToken).ConfigureAwait(false);

        lock (_lock) _cache = (library.Id, today, rules.PreferFavorites, result);
        return result;
    }

    /// <summary>Hides a memory until the user restores it. Photos are not touched.</summary>
    public async Task HideAsync(MemoryCandidate memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memory);
        await _memories.HideAsync(new HiddenMemory(memory.Key, memory.Kind, memory.Start, memory.End, _clock.UtcNow), cancellationToken).ConfigureAwait(false);
        Invalidate();
    }

    /// <summary>Undo of <see cref="HideAsync"/>, also used from the list of hidden memories.</summary>
    public async Task UnhideAsync(string key, CancellationToken cancellationToken)
    {
        await _memories.UnhideAsync(key, cancellationToken).ConfigureAwait(false);
        Invalidate();
    }

    public Task<IReadOnlyList<HiddenMemory>> GetHiddenAsync(CancellationToken cancellationToken) => _memories.GetHiddenAsync(cancellationToken);

    public Task<IReadOnlyList<MemoryExclusion>> GetExclusionsAsync(CancellationToken cancellationToken) => _memories.GetExclusionsAsync(cancellationToken);

    public async Task<long> ExcludeKindAsync(MemoryKind kind, CancellationToken cancellationToken)
    {
        var id = await _memories.ExcludeKindAsync(kind, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        Invalidate();
        return id;
    }

    /// <summary>Photos taken between the two days (inclusive, either order) are never used in memories.</summary>
    public async Task<long> ExcludeRangeAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var id = await _memories.ExcludeRangeAsync(from, to, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        Invalidate();
        return id;
    }

    public async Task RemoveExclusionAsync(long id, CancellationToken cancellationToken)
    {
        await _memories.RemoveExclusionAsync(id, cancellationToken).ConfigureAwait(false);
        Invalidate();
    }

    /// <summary>Creates a regular album with the memory's photos. Returns the album id.</summary>
    public Task<long> CreateAlbumAsync(MemoryCandidate memory, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _organization.CreateAlbumAsync(name.Trim(), null, memory.AssetIds.ToList(), cancellationToken);
    }
}
