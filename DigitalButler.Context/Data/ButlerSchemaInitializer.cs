using Dapper;

namespace DigitalButler.Context.Data;

public sealed class ButlerSchemaInitializer
{
    private readonly IButlerDb _db;

    public ButlerSchemaInitializer(IButlerDb db)
    {
        _db = db;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        // Tables
        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS AiTaskSettings (
                Id TEXT NOT NULL PRIMARY KEY,
                TaskName TEXT NOT NULL,
                ProviderUrl TEXT NULL,
                Model TEXT NULL,
                ApiKey TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ContextItems (
                Id TEXT NOT NULL PRIMARY KEY,
                Source INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL,
                RelevantDate TEXT NULL,
                IsTimeless INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                ExternalId TEXT NULL,
                Category TEXT NULL,
                Summary TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Instructions (
                Id TEXT NOT NULL PRIMARY KEY,
                Source INTEGER NOT NULL,
                Content TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Schedules (
                Id TEXT NOT NULL PRIMARY KEY,
                Source INTEGER NOT NULL,
                CronOrInterval TEXT NOT NULL,
                Enabled INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SummarySchedules (
                Id TEXT NOT NULL PRIMARY KEY,
                IsWeekly INTEGER NOT NULL,
                DayOfWeek INTEGER NULL,
                Time TEXT NOT NULL,
                Enabled INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS GoogleCalendarFeeds (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                Url TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """
        );

        // Indexes (idempotent)
        await conn.ExecuteAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AiTaskSettings_TaskName ON AiTaskSettings (TaskName);
            CREATE INDEX IF NOT EXISTS IX_ContextItems_Source_ExternalId ON ContextItems (Source, ExternalId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Schedules_Source ON Schedules (Source);
            CREATE INDEX IF NOT EXISTS IX_SummarySchedules_IsWeekly_DayOfWeek ON SummarySchedules (IsWeekly, DayOfWeek);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_GoogleCalendarFeeds_Url ON GoogleCalendarFeeds (Url);
            """
        );
    }
}
