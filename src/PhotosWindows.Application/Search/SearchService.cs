using System.Globalization;
using System.Text.RegularExpressions;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.Application.Search;

/// <summary>
/// Local search. Free text is split into structured filters the catalog understands
/// (years, year-months, ISO dates, media type and favourite words in English/Spanish);
/// the remaining words go to the SQLite FTS5 index (file name, title, description, keywords,
/// camera, lens, album names, format). Nothing leaves the machine.
/// </summary>
public sealed partial class SearchService
{
    private static readonly Dictionary<string, Func<AssetQuery, AssetQuery>> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video"] = q => q with { MediaType = MediaType.Video },
        ["videos"] = q => q with { MediaType = MediaType.Video },
        ["vídeo"] = q => q with { MediaType = MediaType.Video },
        ["vídeos"] = q => q with { MediaType = MediaType.Video },
        ["photo"] = q => q with { MediaType = MediaType.Photo },
        ["photos"] = q => q with { MediaType = MediaType.Photo },
        ["foto"] = q => q with { MediaType = MediaType.Photo },
        ["fotos"] = q => q with { MediaType = MediaType.Photo },
        ["favorite"] = q => q with { Favorite = true },
        ["favorites"] = q => q with { Favorite = true },
        ["favourite"] = q => q with { Favorite = true },
        ["favourites"] = q => q with { Favorite = true },
        ["favorito"] = q => q with { Favorite = true },
        ["favoritos"] = q => q with { Favorite = true },
        ["edited"] = q => q with { Edited = true },
        ["editadas"] = q => q with { Edited = true },
        ["editados"] = q => q with { Edited = true },
    };

    private readonly IAssetRepository _assets;

    public SearchService(IAssetRepository assets) => _assets = assets;

    public static AssetQuery Parse(string? text, AssetQuery? baseQuery = null)
    {
        var query = baseQuery ?? new AssetQuery();
        if (string.IsNullOrWhiteSpace(text)) return query with { Text = null };

        var remaining = new List<string>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Words.TryGetValue(token, out var apply))
            {
                query = apply(query);
                continue;
            }

            var m = DateTokenRegex().Match(token);
            if (m.Success)
            {
                var year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
                if (year is >= 1826 and <= 2200)
                {
                    if (m.Groups["d"].Success)
                    {
                        var month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
                        var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
                        if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
                        {
                            var from = new DateTime(year, month, day);
                            query = query with { From = from, To = from.AddDays(1) };
                            continue;
                        }
                    }
                    else if (m.Groups["m"].Success)
                    {
                        var month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
                        if (month is >= 1 and <= 12)
                        {
                            var from = new DateTime(year, month, 1);
                            query = query with { From = from, To = from.AddMonths(1) };
                            continue;
                        }
                    }
                    else
                    {
                        var from = new DateTime(year, 1, 1);
                        query = query with { From = from, To = from.AddYears(1) };
                        continue;
                    }
                }
            }

            remaining.Add(token);
        }

        return query with { Text = remaining.Count == 0 ? null : string.Join(' ', remaining) };
    }

    public Task<IReadOnlyList<AssetSummary>> SearchAsync(string? text, AssetQuery? filters, CancellationToken cancellationToken) =>
        _assets.QueryAsync(Parse(text, filters), cancellationToken);

    public Task<IReadOnlyList<string>> GetFilterValuesAsync(SmartField field, CancellationToken cancellationToken) =>
        _assets.GetDistinctValuesAsync(field, cancellationToken);

    [GeneratedRegex(@"^(?<y>\d{4})(?:[-/.](?<m>\d{1,2})(?:[-/.](?<d>\d{1,2}))?)?$")]
    private static partial Regex DateTokenRegex();
}
