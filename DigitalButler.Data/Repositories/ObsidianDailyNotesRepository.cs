using System.Text.Json;
using Dapper;
using DigitalButler.Common;

namespace DigitalButler.Data.Repositories;

public sealed class ObsidianDailyNotesRepository
{
    private readonly IButlerDb _db;

    public ObsidianDailyNotesRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<ObsidianDailyNote?> GetByDateAsync(DateOnly date, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT * FROM ObsidianDailyNotes WHERE Date = @Date;";
        var row = await conn.QuerySingleOrDefaultAsync<ObsidianDailyNoteRow>(sql, new { Date = date.ToString("yyyy-MM-dd") });
        return row is null ? null : Map(row);
    }

    public async Task<List<ObsidianDailyNote>> GetRangeAsync(DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT * FROM ObsidianDailyNotes
            WHERE Date >= @From AND Date <= @To
            ORDER BY Date DESC;
            """;
        var rows = await conn.QueryAsync<ObsidianDailyNoteRow>(sql, new
        {
            From = fromInclusive.ToString("yyyy-MM-dd"),
            To = toInclusive.ToString("yyyy-MM-dd")
        });
        return rows.Select(Map).ToList();
    }

    public async Task<List<ObsidianDailyNote>> GetRecentAsync(int days = 30, CancellationToken ct = default)
    {
        var to = DateOnly.FromDateTime(DateTime.Today);
        var from = to.AddDays(-days);
        return await GetRangeAsync(from, to, ct);
    }

    public async Task<(int Added, int Updated, int Unchanged)> UpsertManyAsync(
        IEnumerable<ObsidianDailyNote> notes,
        CancellationToken ct = default)
    {
        int added = 0, updated = 0, unchanged = 0;
        var now = DateTimeOffset.UtcNow;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var note in notes)
        {
            var dateStr = note.Date.ToString("yyyy-MM-dd");

            var existingRow = await conn.QuerySingleOrDefaultAsync<ObsidianDailyNoteRow>(
                "SELECT * FROM ObsidianDailyNotes WHERE Date = @Date",
                new { Date = dateStr },
                tx);

            if (existingRow is null)
            {
                // Insert new
                note.CreatedAt = now;
                note.UpdatedAt = now;
                await InsertAsync(conn, tx, note);
                added++;
            }
            else
            {
                var existing = Map(existingRow);
                if (HasChanged(note, existing))
                {
                    // Update changed
                    note.CreatedAt = existing.CreatedAt;
                    note.UpdatedAt = now;
                    await UpdateAsync(conn, tx, note);
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }
        }

        await tx.CommitAsync(ct);
        return (added, updated, unchanged);
    }

    private static async Task InsertAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx, ObsidianDailyNote note)
    {
        const string sql = """
            INSERT INTO ObsidianDailyNotes (
                Date, LifeSatisfaction, SelfEsteem, Presence, Energy, Motivation, Optimism,
                Stress, Irritability, Obsession, OfflineTime, MeditationMinutes, Weight,
                SoulCount, SoulItems, BodyCount, BodyItems, AreasCount, AreasItems,
                LifeCount, LifeItems, IndulgingCount, IndulgingItems, WeatherItems,
                CompletedTasks, PendingTasks, InQuestionTasks, PartiallyCompleteTasks,
                RescheduledTasks, CancelledTasks, StarredTasks, AttentionTasks,
                InformationTasks, IdeaTasks, Notes, Tags,
                FilePath, ContentHash, FileModifiedAt, CreatedAt, UpdatedAt
            ) VALUES (
                @Date, @LifeSatisfaction, @SelfEsteem, @Presence, @Energy, @Motivation, @Optimism,
                @Stress, @Irritability, @Obsession, @OfflineTime, @MeditationMinutes, @Weight,
                @SoulCount, @SoulItems, @BodyCount, @BodyItems, @AreasCount, @AreasItems,
                @LifeCount, @LifeItems, @IndulgingCount, @IndulgingItems, @WeatherItems,
                @CompletedTasks, @PendingTasks, @InQuestionTasks, @PartiallyCompleteTasks,
                @RescheduledTasks, @CancelledTasks, @StarredTasks, @AttentionTasks,
                @InformationTasks, @IdeaTasks, @Notes, @Tags,
                @FilePath, @ContentHash, @FileModifiedAt, @CreatedAt, @UpdatedAt
            );
            """;

        await conn.ExecuteAsync(sql, ToParams(note), tx);
    }

    private static async Task UpdateAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx, ObsidianDailyNote note)
    {
        const string sql = """
            UPDATE ObsidianDailyNotes SET
                LifeSatisfaction = @LifeSatisfaction, SelfEsteem = @SelfEsteem, Presence = @Presence,
                Energy = @Energy, Motivation = @Motivation, Optimism = @Optimism,
                Stress = @Stress, Irritability = @Irritability, Obsession = @Obsession,
                OfflineTime = @OfflineTime, MeditationMinutes = @MeditationMinutes, Weight = @Weight,
                SoulCount = @SoulCount, SoulItems = @SoulItems, BodyCount = @BodyCount, BodyItems = @BodyItems,
                AreasCount = @AreasCount, AreasItems = @AreasItems, LifeCount = @LifeCount, LifeItems = @LifeItems,
                IndulgingCount = @IndulgingCount, IndulgingItems = @IndulgingItems, WeatherItems = @WeatherItems,
                CompletedTasks = @CompletedTasks, PendingTasks = @PendingTasks,
                InQuestionTasks = @InQuestionTasks, PartiallyCompleteTasks = @PartiallyCompleteTasks,
                RescheduledTasks = @RescheduledTasks, CancelledTasks = @CancelledTasks,
                StarredTasks = @StarredTasks, AttentionTasks = @AttentionTasks,
                InformationTasks = @InformationTasks, IdeaTasks = @IdeaTasks,
                Notes = @Notes, Tags = @Tags,
                FilePath = @FilePath, ContentHash = @ContentHash,
                FileModifiedAt = @FileModifiedAt, UpdatedAt = @UpdatedAt
            WHERE Date = @Date;
            """;

        await conn.ExecuteAsync(sql, ToParams(note), tx);
    }

    private static object ToParams(ObsidianDailyNote note) => new
    {
        Date = note.Date.ToString("yyyy-MM-dd"),
        note.LifeSatisfaction,
        note.SelfEsteem,
        note.Presence,
        note.Energy,
        note.Motivation,
        note.Optimism,
        note.Stress,
        note.Irritability,
        note.Obsession,
        note.OfflineTime,
        note.MeditationMinutes,
        note.Weight,
        note.SoulCount,
        SoulItems = SerializeList(note.SoulItems),
        note.BodyCount,
        BodyItems = SerializeList(note.BodyItems),
        note.AreasCount,
        AreasItems = SerializeList(note.AreasItems),
        note.LifeCount,
        LifeItems = SerializeList(note.LifeItems),
        note.IndulgingCount,
        IndulgingItems = SerializeList(note.IndulgingItems),
        WeatherItems = SerializeList(note.WeatherItems),
        CompletedTasks = SerializeList(note.CompletedTasks),
        PendingTasks = SerializeList(note.PendingTasks),
        InQuestionTasks = SerializeList(note.InQuestionTasks),
        PartiallyCompleteTasks = SerializeList(note.PartiallyCompleteTasks),
        RescheduledTasks = SerializeList(note.RescheduledTasks),
        CancelledTasks = SerializeList(note.CancelledTasks),
        StarredTasks = SerializeList(note.StarredTasks),
        AttentionTasks = SerializeList(note.AttentionTasks),
        InformationTasks = SerializeList(note.InformationTasks),
        IdeaTasks = SerializeList(note.IdeaTasks),
        note.Notes,
        Tags = SerializeList(note.Tags),
        note.FilePath,
        note.ContentHash,
        note.FileModifiedAt,
        note.CreatedAt,
        note.UpdatedAt
    };

    private static bool HasChanged(ObsidianDailyNote incoming, ObsidianDailyNote existing)
    {
        return incoming.LifeSatisfaction != existing.LifeSatisfaction ||
            incoming.SelfEsteem != existing.SelfEsteem ||
            incoming.Presence != existing.Presence ||
            incoming.Energy != existing.Energy ||
            incoming.Motivation != existing.Motivation ||
            incoming.Optimism != existing.Optimism ||
            incoming.Stress != existing.Stress ||
            incoming.Irritability != existing.Irritability ||
            incoming.Obsession != existing.Obsession ||
            incoming.OfflineTime != existing.OfflineTime ||
            incoming.MeditationMinutes != existing.MeditationMinutes ||
            incoming.Weight != existing.Weight ||
            incoming.SoulCount != existing.SoulCount ||
            !ListsEqual(incoming.SoulItems, existing.SoulItems) ||
            incoming.BodyCount != existing.BodyCount ||
            !ListsEqual(incoming.BodyItems, existing.BodyItems) ||
            incoming.AreasCount != existing.AreasCount ||
            !ListsEqual(incoming.AreasItems, existing.AreasItems) ||
            incoming.LifeCount != existing.LifeCount ||
            !ListsEqual(incoming.LifeItems, existing.LifeItems) ||
            incoming.IndulgingCount != existing.IndulgingCount ||
            !ListsEqual(incoming.IndulgingItems, existing.IndulgingItems) ||
            !ListsEqual(incoming.WeatherItems, existing.WeatherItems) ||
            !ListsEqual(incoming.CompletedTasks, existing.CompletedTasks) ||
            !ListsEqual(incoming.PendingTasks, existing.PendingTasks) ||
            !ListsEqual(incoming.InQuestionTasks, existing.InQuestionTasks) ||
            !ListsEqual(incoming.PartiallyCompleteTasks, existing.PartiallyCompleteTasks) ||
            !ListsEqual(incoming.RescheduledTasks, existing.RescheduledTasks) ||
            !ListsEqual(incoming.CancelledTasks, existing.CancelledTasks) ||
            !ListsEqual(incoming.StarredTasks, existing.StarredTasks) ||
            !ListsEqual(incoming.AttentionTasks, existing.AttentionTasks) ||
            !ListsEqual(incoming.InformationTasks, existing.InformationTasks) ||
            !ListsEqual(incoming.IdeaTasks, existing.IdeaTasks) ||
            !string.Equals(incoming.Notes, existing.Notes, StringComparison.Ordinal) ||
            !ListsEqual(incoming.Tags, existing.Tags) ||
            !string.Equals(incoming.FilePath, existing.FilePath, StringComparison.Ordinal) ||
            !string.Equals(incoming.ContentHash, existing.ContentHash, StringComparison.Ordinal) ||
            !Nullable.Equals(incoming.FileModifiedAt, existing.FileModifiedAt);
    }

    private static bool ListsEqual(List<string>? left, List<string>? right)
    {
        var leftEmpty = left is null || left.Count == 0;
        var rightEmpty = right is null || right.Count == 0;
        if (leftEmpty || rightEmpty)
        {
            return leftEmpty == rightEmpty;
        }

        return left!.SequenceEqual(right!, StringComparer.Ordinal);
    }

    private static string? SerializeList(List<string>? list)
    {
        if (list is null || list.Count == 0)
            return null;
        return JsonSerializer.Serialize(list);
    }

    private static List<string>? DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return JsonSerializer.Deserialize<List<string>>(json);
    }

    private static ObsidianDailyNote Map(ObsidianDailyNoteRow row) => new()
    {
        Date = DateOnly.Parse(row.Date),
        LifeSatisfaction = row.LifeSatisfaction,
        SelfEsteem = row.SelfEsteem,
        Presence = row.Presence,
        Energy = row.Energy,
        Motivation = row.Motivation,
        Optimism = row.Optimism,
        Stress = row.Stress,
        Irritability = row.Irritability,
        Obsession = row.Obsession,
        OfflineTime = row.OfflineTime,
        MeditationMinutes = row.MeditationMinutes,
        Weight = row.Weight,
        SoulCount = row.SoulCount,
        SoulItems = DeserializeList(row.SoulItems),
        BodyCount = row.BodyCount,
        BodyItems = DeserializeList(row.BodyItems),
        AreasCount = row.AreasCount,
        AreasItems = DeserializeList(row.AreasItems),
        LifeCount = row.LifeCount,
        LifeItems = DeserializeList(row.LifeItems),
        IndulgingCount = row.IndulgingCount,
        IndulgingItems = DeserializeList(row.IndulgingItems),
        WeatherItems = DeserializeList(row.WeatherItems),
        CompletedTasks = DeserializeList(row.CompletedTasks),
        PendingTasks = DeserializeList(row.PendingTasks),
        InQuestionTasks = DeserializeList(row.InQuestionTasks),
        PartiallyCompleteTasks = DeserializeList(row.PartiallyCompleteTasks),
        RescheduledTasks = DeserializeList(row.RescheduledTasks),
        CancelledTasks = DeserializeList(row.CancelledTasks),
        StarredTasks = DeserializeList(row.StarredTasks),
        AttentionTasks = DeserializeList(row.AttentionTasks),
        InformationTasks = DeserializeList(row.InformationTasks),
        IdeaTasks = DeserializeList(row.IdeaTasks),
        Notes = row.Notes,
        Tags = DeserializeList(row.Tags),
        FilePath = row.FilePath,
        ContentHash = row.ContentHash ?? string.Empty,
        FileModifiedAt = row.FileModifiedAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };

    private sealed class ObsidianDailyNoteRow
    {
        public string Date { get; set; } = string.Empty;
        public int? LifeSatisfaction { get; set; }
        public int? SelfEsteem { get; set; }
        public int? Presence { get; set; }
        public int? Energy { get; set; }
        public int? Motivation { get; set; }
        public int? Optimism { get; set; }
        public int? Stress { get; set; }
        public int? Irritability { get; set; }
        public int? Obsession { get; set; }
        public int? OfflineTime { get; set; }
        public int? MeditationMinutes { get; set; }
        public decimal? Weight { get; set; }
        public int? SoulCount { get; set; }
        public string? SoulItems { get; set; }
        public int? BodyCount { get; set; }
        public string? BodyItems { get; set; }
        public int? AreasCount { get; set; }
        public string? AreasItems { get; set; }
        public int? LifeCount { get; set; }
        public string? LifeItems { get; set; }
        public int? IndulgingCount { get; set; }
        public string? IndulgingItems { get; set; }
        public string? WeatherItems { get; set; }
        public string? CompletedTasks { get; set; }
        public string? PendingTasks { get; set; }
        public string? InQuestionTasks { get; set; }
        public string? PartiallyCompleteTasks { get; set; }
        public string? RescheduledTasks { get; set; }
        public string? CancelledTasks { get; set; }
        public string? StarredTasks { get; set; }
        public string? AttentionTasks { get; set; }
        public string? InformationTasks { get; set; }
        public string? IdeaTasks { get; set; }
        public string? Notes { get; set; }
        public string? Tags { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? ContentHash { get; set; }
        public DateTimeOffset? FileModifiedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
