using Dapper;

namespace DigitalButler.Data;

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

            CREATE TABLE IF NOT EXISTS SkillInstructions (
                Id TEXT NOT NULL PRIMARY KEY,
                Skill INTEGER NOT NULL,
                Content TEXT NOT NULL,
                ContextSourcesMask INTEGER NOT NULL DEFAULT -1,
                EnableAiContext INTEGER NOT NULL DEFAULT 0,
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

            CREATE TABLE IF NOT EXISTS GoogleOAuthTokens (
                Id TEXT NOT NULL PRIMARY KEY,
                UserId TEXT NOT NULL,
                AccessToken TEXT NOT NULL,
                RefreshToken TEXT NULL,
                ExpiresAt TEXT NOT NULL,
                Scope TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """
        );

        // Lightweight migrations (idempotent). SQLite doesn't support IF NOT EXISTS for ADD COLUMN.
        // Ignore the error if the column already exists.
        try
        {
            await conn.ExecuteAsync("ALTER TABLE SkillInstructions ADD COLUMN ContextSourcesMask INTEGER NOT NULL DEFAULT -1;");
        }
        catch
        {
            // Intentionally ignored
        }

        try
        {
            await conn.ExecuteAsync("ALTER TABLE SkillInstructions ADD COLUMN EnableAiContext INTEGER NOT NULL DEFAULT 0;");
        }
        catch
        {
            // Intentionally ignored
        }

        try
        {
            await conn.ExecuteAsync("ALTER TABLE ContextItems ADD COLUMN MediaMetadata TEXT NULL;");
        }
        catch
        {
            // Intentionally ignored
        }

        try
        {
            await conn.ExecuteAsync("ALTER TABLE ContextItems ADD COLUMN MediaType TEXT NULL;");
        }
        catch
        {
            // Intentionally ignored
        }

        // Indexes (idempotent)
        await conn.ExecuteAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AiTaskSettings_TaskName ON AiTaskSettings (TaskName);
            CREATE INDEX IF NOT EXISTS IX_ContextItems_Source_ExternalId ON ContextItems (Source, ExternalId);
            CREATE INDEX IF NOT EXISTS IX_ContextItems_RelevantDate ON ContextItems (RelevantDate);
            CREATE INDEX IF NOT EXISTS IX_ContextItems_IsTimeless ON ContextItems (IsTimeless);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Schedules_Source ON Schedules (Source);
            CREATE INDEX IF NOT EXISTS IX_SummarySchedules_IsWeekly_DayOfWeek ON SummarySchedules (IsWeekly, DayOfWeek);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_GoogleCalendarFeeds_Url ON GoogleCalendarFeeds (Url);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SkillInstructions_Skill ON SkillInstructions (Skill);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_GoogleOAuthTokens_UserId ON GoogleOAuthTokens (UserId);
            """
        );
    }
}
