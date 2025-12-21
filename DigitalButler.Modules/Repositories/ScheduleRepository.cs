using Dapper;
using DigitalButler.Modules.Data;

namespace DigitalButler.Modules.Repositories;

public sealed class ScheduleRepository
{
    private readonly IButlerDb _db;

    public ScheduleRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<List<ScheduleConfig>> GetAllUpdateSchedulesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, Source, CronOrInterval, Enabled
            FROM Schedules
            ORDER BY Source;
            """;

        var rows = await conn.QueryAsync<ScheduleConfigRow>(sql);
        return rows.Select(Map).ToList();
    }

    public async Task<List<ScheduleConfig>> GetEnabledUpdateSchedulesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, Source, CronOrInterval, Enabled
            FROM Schedules
            WHERE Enabled = 1
            ORDER BY Source;
            """;

        var rows = await conn.QueryAsync<ScheduleConfigRow>(sql);
        return rows.Select(Map).ToList();
    }

    public async Task<List<SummarySchedule>> GetAllSummarySchedulesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, IsWeekly, DayOfWeek, Time, Enabled
            FROM SummarySchedules
            ORDER BY IsWeekly, DayOfWeek;
            """;

        var rows = await conn.QueryAsync<SummaryScheduleRow>(sql);
        return rows.Select(Map).ToList();
    }

    public async Task<List<SummarySchedule>> GetEnabledDailySummarySchedulesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, IsWeekly, DayOfWeek, Time, Enabled
            FROM SummarySchedules
            WHERE Enabled = 1 AND IsWeekly = 0
            ORDER BY Time;
            """;

        var rows = await conn.QueryAsync<SummaryScheduleRow>(sql);
        return rows.Select(Map).ToList();
    }

    public async Task<List<SummarySchedule>> GetEnabledWeeklySummarySchedulesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, IsWeekly, DayOfWeek, Time, Enabled
            FROM SummarySchedules
            WHERE Enabled = 1 AND IsWeekly = 1
            ORDER BY DayOfWeek, Time;
            """;

        var rows = await conn.QueryAsync<SummaryScheduleRow>(sql);
        return rows.Select(Map).ToList();
    }

    public async Task ReplaceUpdateSchedulesAsync(IEnumerable<ScheduleConfig> schedules, CancellationToken ct = default)
    {
        var cleaned = schedules.ToList();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingIds = (await conn.QueryAsync<Guid>("SELECT Id FROM Schedules;", transaction: tx)).ToHashSet();
        var keepIds = cleaned.Select(x => x.Id).ToHashSet();

        foreach (var id in existingIds)
        {
            if (!keepIds.Contains(id))
            {
                await conn.ExecuteAsync("DELETE FROM Schedules WHERE Id = @Id;", new { Id = id }, tx);
            }
        }

        const string upsertSql = """
            INSERT INTO Schedules (Id, Source, CronOrInterval, Enabled)
            VALUES (@Id, @Source, @CronOrInterval, @Enabled)
            ON CONFLICT(Id) DO UPDATE SET
                Source = excluded.Source,
                CronOrInterval = excluded.CronOrInterval,
                Enabled = excluded.Enabled;
            """;

        foreach (var item in cleaned)
        {
            if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
            await conn.ExecuteAsync(upsertSql, new
            {
                item.Id,
                Source = (int)item.Source,
                item.CronOrInterval,
                Enabled = item.Enabled ? 1 : 0
            }, tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task ReplaceSummarySchedulesAsync(IEnumerable<SummarySchedule> schedules, CancellationToken ct = default)
    {
        var cleaned = schedules.ToList();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingIds = (await conn.QueryAsync<Guid>("SELECT Id FROM SummarySchedules;", transaction: tx)).ToHashSet();
        var keepIds = cleaned.Select(x => x.Id).ToHashSet();

        foreach (var id in existingIds)
        {
            if (!keepIds.Contains(id))
            {
                await conn.ExecuteAsync("DELETE FROM SummarySchedules WHERE Id = @Id;", new { Id = id }, tx);
            }
        }

        const string upsertSql = """
            INSERT INTO SummarySchedules (Id, IsWeekly, DayOfWeek, Time, Enabled)
            VALUES (@Id, @IsWeekly, @DayOfWeek, @Time, @Enabled)
            ON CONFLICT(Id) DO UPDATE SET
                IsWeekly = excluded.IsWeekly,
                DayOfWeek = excluded.DayOfWeek,
                Time = excluded.Time,
                Enabled = excluded.Enabled;
            """;

        foreach (var item in cleaned)
        {
            if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();

            await conn.ExecuteAsync(upsertSql, new
            {
                item.Id,
                IsWeekly = item.IsWeekly ? 1 : 0,
                DayOfWeek = item.DayOfWeek is null ? (int?)null : (int)item.DayOfWeek.Value,
                item.Time,
                Enabled = item.Enabled ? 1 : 0
            }, tx);
        }

        await tx.CommitAsync(ct);
    }

    private sealed class ScheduleConfigRow
    {
        public Guid Id { get; set; }
        public int Source { get; set; }
        public string CronOrInterval { get; set; } = string.Empty;
        public long Enabled { get; set; }
    }

    private static ScheduleConfig Map(ScheduleConfigRow row) => new()
    {
        Id = row.Id,
        Source = (ContextSource)row.Source,
        CronOrInterval = row.CronOrInterval,
        Enabled = row.Enabled != 0
    };

    private sealed class SummaryScheduleRow
    {
        public Guid Id { get; set; }
        public long IsWeekly { get; set; }
        public int? DayOfWeek { get; set; }
        public TimeOnly Time { get; set; }
        public long Enabled { get; set; }
    }

    private static SummarySchedule Map(SummaryScheduleRow row) => new()
    {
        Id = row.Id,
        IsWeekly = row.IsWeekly != 0,
        DayOfWeek = row.DayOfWeek is null ? null : (DayOfWeek)row.DayOfWeek.Value,
        Time = row.Time,
        Enabled = row.Enabled != 0
    };
}
