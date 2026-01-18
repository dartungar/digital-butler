using System.Text.Json;
using Dapper;
using DigitalButler.Common;

namespace DigitalButler.Data.Repositories;

public sealed class ObsidianWeeklySummaryRepository
{
    private readonly IButlerDb _db;

    public ObsidianWeeklySummaryRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<ObsidianWeeklySummary?> GetByWeekStartAsync(DateOnly weekStart, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT * FROM ObsidianWeeklySummaries WHERE WeekStart = @WeekStart;";
        var row = await conn.QuerySingleOrDefaultAsync<ObsidianWeeklySummaryRow>(sql, new { WeekStart = weekStart.ToString("yyyy-MM-dd") });
        return row is null ? null : Map(row);
    }

    public async Task<List<ObsidianWeeklySummary>> GetRecentAsync(int weeks = 8, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT * FROM ObsidianWeeklySummaries
            ORDER BY WeekStart DESC
            LIMIT @Weeks;
            """;
        var rows = await conn.QueryAsync<ObsidianWeeklySummaryRow>(sql, new { Weeks = weeks });
        return rows.Select(Map).ToList();
    }

    public async Task UpsertAsync(ObsidianWeeklySummary summary, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await _db.OpenAsync(ct);

        var weekStartStr = summary.WeekStart.ToString("yyyy-MM-dd");

        // Check if exists
        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM ObsidianWeeklySummaries WHERE WeekStart = @WeekStart",
            new { WeekStart = weekStartStr });

        if (exists > 0)
        {
            // Update
            summary.UpdatedAt = now;
            const string updateSql = """
                UPDATE ObsidianWeeklySummaries SET
                    AvgLifeSatisfaction = @AvgLifeSatisfaction, AvgSelfEsteem = @AvgSelfEsteem,
                    AvgPresence = @AvgPresence, AvgEnergy = @AvgEnergy, AvgMotivation = @AvgMotivation,
                    AvgOptimism = @AvgOptimism, AvgStress = @AvgStress, AvgIrritability = @AvgIrritability,
                    AvgObsession = @AvgObsession, AvgOfflineTime = @AvgOfflineTime,
                    TotalMeditationMinutes = @TotalMeditationMinutes,
                    TotalSoulCount = @TotalSoulCount, TotalBodyCount = @TotalBodyCount,
                    TotalAreasCount = @TotalAreasCount, TotalLifeCount = @TotalLifeCount,
                    TotalIndulgingCount = @TotalIndulgingCount,
                    TotalCompletedTasks = @TotalCompletedTasks, TotalPendingTasks = @TotalPendingTasks,
                    DaysWithData = @DaysWithData, Summary = @Summary, TopTags = @TopTags,
                    UpdatedAt = @UpdatedAt
                WHERE WeekStart = @WeekStart;
                """;
            await conn.ExecuteAsync(updateSql, ToParams(summary));
        }
        else
        {
            // Insert
            summary.CreatedAt = now;
            summary.UpdatedAt = now;
            const string insertSql = """
                INSERT INTO ObsidianWeeklySummaries (
                    WeekStart, AvgLifeSatisfaction, AvgSelfEsteem, AvgPresence, AvgEnergy, AvgMotivation,
                    AvgOptimism, AvgStress, AvgIrritability, AvgObsession, AvgOfflineTime,
                    TotalMeditationMinutes, TotalSoulCount, TotalBodyCount, TotalAreasCount, TotalLifeCount,
                    TotalIndulgingCount, TotalCompletedTasks, TotalPendingTasks,
                    DaysWithData, Summary, TopTags, CreatedAt, UpdatedAt
                ) VALUES (
                    @WeekStart, @AvgLifeSatisfaction, @AvgSelfEsteem, @AvgPresence, @AvgEnergy, @AvgMotivation,
                    @AvgOptimism, @AvgStress, @AvgIrritability, @AvgObsession, @AvgOfflineTime,
                    @TotalMeditationMinutes, @TotalSoulCount, @TotalBodyCount, @TotalAreasCount, @TotalLifeCount,
                    @TotalIndulgingCount, @TotalCompletedTasks, @TotalPendingTasks,
                    @DaysWithData, @Summary, @TopTags, @CreatedAt, @UpdatedAt
                );
                """;
            await conn.ExecuteAsync(insertSql, ToParams(summary));
        }
    }

    private static object ToParams(ObsidianWeeklySummary s) => new
    {
        WeekStart = s.WeekStart.ToString("yyyy-MM-dd"),
        s.AvgLifeSatisfaction,
        s.AvgSelfEsteem,
        s.AvgPresence,
        s.AvgEnergy,
        s.AvgMotivation,
        s.AvgOptimism,
        s.AvgStress,
        s.AvgIrritability,
        s.AvgObsession,
        s.AvgOfflineTime,
        s.TotalMeditationMinutes,
        s.TotalSoulCount,
        s.TotalBodyCount,
        s.TotalAreasCount,
        s.TotalLifeCount,
        s.TotalIndulgingCount,
        s.TotalCompletedTasks,
        s.TotalPendingTasks,
        s.DaysWithData,
        s.Summary,
        TopTags = s.TopTags is { Count: > 0 } ? JsonSerializer.Serialize(s.TopTags) : null,
        s.CreatedAt,
        s.UpdatedAt
    };

    private static ObsidianWeeklySummary Map(ObsidianWeeklySummaryRow r) => new()
    {
        WeekStart = DateOnly.Parse(r.WeekStart),
        AvgLifeSatisfaction = r.AvgLifeSatisfaction,
        AvgSelfEsteem = r.AvgSelfEsteem,
        AvgPresence = r.AvgPresence,
        AvgEnergy = r.AvgEnergy,
        AvgMotivation = r.AvgMotivation,
        AvgOptimism = r.AvgOptimism,
        AvgStress = r.AvgStress,
        AvgIrritability = r.AvgIrritability,
        AvgObsession = r.AvgObsession,
        AvgOfflineTime = r.AvgOfflineTime,
        TotalMeditationMinutes = r.TotalMeditationMinutes,
        TotalSoulCount = r.TotalSoulCount,
        TotalBodyCount = r.TotalBodyCount,
        TotalAreasCount = r.TotalAreasCount,
        TotalLifeCount = r.TotalLifeCount,
        TotalIndulgingCount = r.TotalIndulgingCount,
        TotalCompletedTasks = r.TotalCompletedTasks,
        TotalPendingTasks = r.TotalPendingTasks,
        DaysWithData = r.DaysWithData,
        Summary = r.Summary,
        TopTags = string.IsNullOrWhiteSpace(r.TopTags) ? null : JsonSerializer.Deserialize<List<string>>(r.TopTags),
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt
    };

    private sealed class ObsidianWeeklySummaryRow
    {
        public string WeekStart { get; set; } = string.Empty;
        public decimal? AvgLifeSatisfaction { get; set; }
        public decimal? AvgSelfEsteem { get; set; }
        public decimal? AvgPresence { get; set; }
        public decimal? AvgEnergy { get; set; }
        public decimal? AvgMotivation { get; set; }
        public decimal? AvgOptimism { get; set; }
        public decimal? AvgStress { get; set; }
        public decimal? AvgIrritability { get; set; }
        public decimal? AvgObsession { get; set; }
        public decimal? AvgOfflineTime { get; set; }
        public int? TotalMeditationMinutes { get; set; }
        public int? TotalSoulCount { get; set; }
        public int? TotalBodyCount { get; set; }
        public int? TotalAreasCount { get; set; }
        public int? TotalLifeCount { get; set; }
        public int? TotalIndulgingCount { get; set; }
        public int? TotalCompletedTasks { get; set; }
        public int? TotalPendingTasks { get; set; }
        public int DaysWithData { get; set; }
        public string? Summary { get; set; }
        public string? TopTags { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
