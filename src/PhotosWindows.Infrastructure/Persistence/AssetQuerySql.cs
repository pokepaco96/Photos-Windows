using System.Globalization;
using System.Text;
using Dapper;
using PhotosWindows.Domain;

namespace PhotosWindows.Infrastructure.Persistence;

/// <summary>Translates <see cref="AssetQuery"/> (and smart album rules) into a parameterised SQL WHERE clause.</summary>
internal sealed class AssetQuerySql
{
    private readonly List<string> _where = [];
    private int _paramIndex;

    private AssetQuerySql(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public DynamicParameters Parameters { get; } = new();
    public string Joins { get; private set; } = string.Empty;
    public DateTime UtcNow { get; }

    public string WhereClause => _where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", _where);

    public static readonly TimeSpan RecentlyAddedWindow = TimeSpan.FromDays(30);

    public static AssetQuerySql Build(AssetQuery query, DateTime utcNow)
    {
        var b = new AssetQuerySql(utcNow);
        b.AddScope(query.Scope);

        if (query.MediaType is { } type) b._where.Add($"a.media_type = {b.Param((int)type)}");
        if (query.Favorite is { } fav) b._where.Add($"a.is_favorite = {(fav ? 1 : 0)}");
        if (query.Edited is { } edited) b._where.Add(edited ? "a.edit_version > 0" : "a.edit_version = 0");
        if (query.HasLocation is { } loc) b._where.Add(loc ? "a.latitude IS NOT NULL" : "a.latitude IS NULL");
        if (query.From is { } from) b._where.Add($"a.capture_local >= {b.Param(DbFormat.Local(from))}");
        if (query.To is { } to) b._where.Add($"a.capture_local < {b.Param(DbFormat.Local(to))}");
        if (!string.IsNullOrWhiteSpace(query.Camera)) b._where.Add($"(coalesce(a.camera_make,'') || ' ' || coalesce(a.camera_model,'')) LIKE {b.Param(Like(query.Camera!))} ESCAPE '\\'");
        if (!string.IsNullOrWhiteSpace(query.Lens)) b._where.Add($"a.lens LIKE {b.Param(Like(query.Lens!))} ESCAPE '\\'");
        if (!string.IsNullOrWhiteSpace(query.Format)) b._where.Add($"a.format = {b.Param(query.Format!)} COLLATE NOCASE");
        if (query.MinRating is { } rating) b._where.Add($"a.rating >= {b.Param(rating)}");
        if (query.PersonId is { } personId)
        {
            b._where.Add($"EXISTS (SELECT 1 FROM faces pf WHERE pf.asset_id = a.id AND pf.person_id = {b.Param(personId)})");
        }

        if (query.Area is { } area)
        {
            b._where.Add($"a.latitude BETWEEN {b.Param(area.MinLat)} AND {b.Param(area.MaxLat)}");
            b._where.Add($"a.longitude BETWEEN {b.Param(area.MinLon)} AND {b.Param(area.MaxLon)}");
        }

        foreach (var keyword in query.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            b._where.Add($"EXISTS (SELECT 1 FROM asset_keywords ak JOIN keywords k ON k.id = ak.keyword_id WHERE ak.asset_id = a.id AND k.name = {b.Param(keyword.Trim())})");
        }

        if (query.AlbumId is { } albumId)
        {
            b.Joins = $"JOIN album_assets aa ON aa.asset_id = a.id AND aa.album_id = {b.Param(albumId)}";
        }

        var fts = BuildFtsExpression(query.Text);
        if (fts is not null)
        {
            b._where.Add($"a.id IN (SELECT rowid FROM assets_fts WHERE assets_fts MATCH {b.Param(fts)})");
        }

        if (query.Smart is { Rules.Count: > 0 } smart)
        {
            var parts = smart.Rules.Select(b.RuleSql).ToList();
            b._where.Add("(" + string.Join(smart.Match == SmartMatch.All ? " AND " : " OR ", parts) + ")");
        }

        return b;
    }

    public static string OrderBy(AssetSort sort, bool hasAlbumJoin) => sort switch
    {
        AssetSort.CaptureDateAscending => "ORDER BY a.capture_local ASC, a.id ASC",
        AssetSort.ImportDateDescending => "ORDER BY a.imported_utc DESC, a.id DESC",
        AssetSort.FileNameAscending => "ORDER BY a.file_name COLLATE NOCASE ASC, a.id ASC",
        AssetSort.AlbumOrder when hasAlbumJoin => "ORDER BY aa.position ASC",
        _ => "ORDER BY a.capture_local DESC, a.id DESC",
    };

    /// <summary>
    /// Converts free text into a safe FTS5 expression: every token becomes a quoted prefix term, all ANDed.
    /// FTS syntax characters typed by the user can therefore never produce a syntax error or injection.
    /// </summary>
    public static string? BuildFtsExpression(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        if (tokens.Count == 0) return null;
        return string.Join(' ', tokens.Take(16).Select(t => "\"" + t + "\"*"));
    }

    public static string Like(string value) =>
        "%" + value.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private void AddScope(LibraryScope scope)
    {
        const string visible = "a.is_hidden = 0 AND a.deleted_utc IS NULL";
        switch (scope)
        {
            case LibraryScope.All: _where.Add(visible); break;
            case LibraryScope.Photos: _where.Add(visible + " AND a.media_type = 0"); break;
            case LibraryScope.Videos: _where.Add(visible + " AND a.media_type = 1"); break;
            case LibraryScope.Favorites: _where.Add(visible + " AND a.is_favorite = 1"); break;
            case LibraryScope.RecentlyAdded: _where.Add(visible + $" AND a.imported_utc >= {Param(DbFormat.Utc(UtcNow - RecentlyAddedWindow))}"); break;
            case LibraryScope.Hidden: _where.Add("a.is_hidden = 1 AND a.deleted_utc IS NULL"); break;
            case LibraryScope.RecentlyDeleted: _where.Add("a.deleted_utc IS NOT NULL"); break;
            case LibraryScope.Edited: _where.Add(visible + " AND a.edit_version > 0"); break;
            case LibraryScope.WithLocation: _where.Add(visible + " AND a.latitude IS NOT NULL"); break;
            case LibraryScope.LivePhotos: _where.Add(visible + " AND a.has_live_video = 1"); break;
            case LibraryScope.Raw: _where.Add(visible + " AND (a.has_raw_pair = 1 OR a.format = 'RAW')"); break;
            default: throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
        }
    }

    private string Param(object value)
    {
        var name = "@p" + _paramIndex++.ToString(CultureInfo.InvariantCulture);
        Parameters.Add(name, value);
        return name;
    }

    private string RuleSql(SmartRule rule)
    {
        var v = rule.Value?.Trim() ?? string.Empty;
        string Text(string column) => rule.Operator switch
        {
            SmartOperator.Is => $"{column} = {Param(v)} COLLATE NOCASE",
            SmartOperator.IsNot => $"coalesce({column},'') <> {Param(v)} COLLATE NOCASE",
            SmartOperator.Contains => $"{column} LIKE {Param(Like(v))} ESCAPE '\\'",
            SmartOperator.DoesNotContain => $"coalesce({column},'') NOT LIKE {Param(Like(v))} ESCAPE '\\'",
            _ => throw Unsupported(rule),
        };

        string Bool(string trueSql, string falseSql) => (rule.Operator, ParseBool(v)) switch
        {
            (SmartOperator.Is, true) or (SmartOperator.IsNot, false) => trueSql,
            (SmartOperator.Is, false) or (SmartOperator.IsNot, true) => falseSql,
            _ => throw Unsupported(rule),
        };

        string Date(string column, bool utcColumn)
        {
            switch (rule.Operator)
            {
                case SmartOperator.InLastDays:
                    var days = int.Parse(v, CultureInfo.InvariantCulture);
                    var cutoff = UtcNow.AddDays(-days);
                    return $"{column} >= {Param(utcColumn ? DbFormat.Utc(cutoff) : DbFormat.Local(cutoff.ToLocalTime()))}";
                case SmartOperator.Before:
                case SmartOperator.After:
                    var date = DateTime.ParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var bound = rule.Operator == SmartOperator.Before ? date : date.AddDays(1);
                    var text = utcColumn ? DbFormat.Utc(DateTime.SpecifyKind(bound, DateTimeKind.Utc)) : DbFormat.Local(bound);
                    return rule.Operator == SmartOperator.Before ? $"{column} < {Param(text)}" : $"{column} >= {Param(text)}";
                default:
                    throw Unsupported(rule);
            }
        }

        return rule.Field switch
        {
            SmartField.MediaType => (rule.Operator is SmartOperator.Is or SmartOperator.IsNot)
                ? $"a.media_type {(rule.Operator == SmartOperator.Is ? "=" : "<>")} {Param((int)Enum.Parse<MediaType>(v, ignoreCase: true))}"
                : throw Unsupported(rule),
            SmartField.Favorite => Bool("a.is_favorite = 1", "a.is_favorite = 0"),
            SmartField.Edited => Bool("a.edit_version > 0", "a.edit_version = 0"),
            SmartField.HasLocation => Bool("a.latitude IS NOT NULL", "a.latitude IS NULL"),
            SmartField.Keyword => rule.Operator switch
            {
                SmartOperator.Is => $"EXISTS (SELECT 1 FROM asset_keywords ak JOIN keywords k ON k.id = ak.keyword_id WHERE ak.asset_id = a.id AND k.name = {Param(v)})",
                SmartOperator.IsNot => $"NOT EXISTS (SELECT 1 FROM asset_keywords ak JOIN keywords k ON k.id = ak.keyword_id WHERE ak.asset_id = a.id AND k.name = {Param(v)})",
                SmartOperator.Contains => $"EXISTS (SELECT 1 FROM asset_keywords ak JOIN keywords k ON k.id = ak.keyword_id WHERE ak.asset_id = a.id AND k.name LIKE {Param(Like(v))} ESCAPE '\\')",
                SmartOperator.DoesNotContain => $"NOT EXISTS (SELECT 1 FROM asset_keywords ak JOIN keywords k ON k.id = ak.keyword_id WHERE ak.asset_id = a.id AND k.name LIKE {Param(Like(v))} ESCAPE '\\')",
                _ => throw Unsupported(rule),
            },
            SmartField.Album => rule.Operator switch
            {
                SmartOperator.Is => $"EXISTS (SELECT 1 FROM album_assets x WHERE x.asset_id = a.id AND x.album_id = {Param(long.Parse(v, CultureInfo.InvariantCulture))})",
                SmartOperator.IsNot => $"NOT EXISTS (SELECT 1 FROM album_assets x WHERE x.asset_id = a.id AND x.album_id = {Param(long.Parse(v, CultureInfo.InvariantCulture))})",
                _ => throw Unsupported(rule),
            },
            SmartField.Camera => Text("(coalesce(a.camera_make,'') || ' ' || coalesce(a.camera_model,''))"),
            SmartField.Lens => Text("a.lens"),
            SmartField.FileName => Text("a.file_name"),
            SmartField.Title => Text("a.title"),
            SmartField.Format => Text("a.format"),
            SmartField.Rating => rule.Operator switch
            {
                SmartOperator.GreaterOrEqual => $"a.rating >= {Param(int.Parse(v, CultureInfo.InvariantCulture))}",
                SmartOperator.LessOrEqual => $"a.rating <= {Param(int.Parse(v, CultureInfo.InvariantCulture))}",
                SmartOperator.Is => $"a.rating = {Param(int.Parse(v, CultureInfo.InvariantCulture))}",
                _ => throw Unsupported(rule),
            },
            SmartField.CaptureDate => Date("a.capture_local", utcColumn: false),
            SmartField.ImportDate => Date("a.imported_utc", utcColumn: true),
            _ => throw Unsupported(rule),
        };
    }

    private static bool ParseBool(string v) => v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static ArgumentException Unsupported(SmartRule rule) =>
        new($"Operator {rule.Operator} is not valid for field {rule.Field}.", nameof(rule));
}
