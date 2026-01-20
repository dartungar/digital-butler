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

            CREATE TABLE IF NOT EXISTS ObsidianDailyNotes (
                Date TEXT NOT NULL PRIMARY KEY,
                LifeSatisfaction INTEGER NULL,
                SelfEsteem INTEGER NULL,
                Presence INTEGER NULL,
                Energy INTEGER NULL,
                Motivation INTEGER NULL,
                Optimism INTEGER NULL,
                Stress INTEGER NULL,
                Irritability INTEGER NULL,
                Obsession INTEGER NULL,
                OfflineTime INTEGER NULL,
                MeditationMinutes INTEGER NULL,
                Weight REAL NULL,
                SoulCount INTEGER NULL,
                SoulItems TEXT NULL,
                BodyCount INTEGER NULL,
                BodyItems TEXT NULL,
                AreasCount INTEGER NULL,
                AreasItems TEXT NULL,
                LifeCount INTEGER NULL,
                LifeItems TEXT NULL,
                IndulgingCount INTEGER NULL,
                IndulgingItems TEXT NULL,
                WeatherItems TEXT NULL,
                CompletedTasks TEXT NULL,
                PendingTasks TEXT NULL,
                Notes TEXT NULL,
                Tags TEXT NULL,
                FilePath TEXT NOT NULL,
                FileModifiedAt TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ContextUpdateLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Source TEXT NOT NULL,
                Status TEXT NOT NULL,
                ItemsScanned INTEGER NOT NULL,
                ItemsAdded INTEGER NOT NULL,
                ItemsUpdated INTEGER NOT NULL,
                ItemsUnchanged INTEGER NOT NULL,
                DurationMs INTEGER NOT NULL,
                Message TEXT NULL,
                Details TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS ObsidianWeeklySummaries (
                WeekStart TEXT NOT NULL PRIMARY KEY,
                AvgLifeSatisfaction REAL NULL,
                AvgSelfEsteem REAL NULL,
                AvgPresence REAL NULL,
                AvgEnergy REAL NULL,
                AvgMotivation REAL NULL,
                AvgOptimism REAL NULL,
                AvgStress REAL NULL,
                AvgIrritability REAL NULL,
                AvgObsession REAL NULL,
                AvgOfflineTime REAL NULL,
                TotalMeditationMinutes INTEGER NULL,
                TotalSoulCount INTEGER NULL,
                TotalBodyCount INTEGER NULL,
                TotalAreasCount INTEGER NULL,
                TotalLifeCount INTEGER NULL,
                TotalIndulgingCount INTEGER NULL,
                TotalCompletedTasks INTEGER NULL,
                TotalPendingTasks INTEGER NULL,
                DaysWithData INTEGER NOT NULL,
                Summary TEXT NULL,
                TopTags TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS VaultNotes (
                Id TEXT NOT NULL PRIMARY KEY,
                FilePath TEXT NOT NULL UNIQUE,
                Title TEXT NULL,
                ContentHash TEXT NOT NULL,
                FileModifiedAt TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS NoteChunks (
                Id TEXT NOT NULL PRIMARY KEY,
                NoteId TEXT NOT NULL,
                ChunkIndex INTEGER NOT NULL,
                ChunkText TEXT NOT NULL,
                StartLine INTEGER NULL,
                EndLine INTEGER NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (NoteId) REFERENCES VaultNotes(Id) ON DELETE CASCADE,
                UNIQUE(NoteId, ChunkIndex)
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
            CREATE INDEX IF NOT EXISTS IX_ObsidianDailyNotes_Date ON ObsidianDailyNotes (Date DESC);
            CREATE INDEX IF NOT EXISTS IX_ContextUpdateLog_Timestamp ON ContextUpdateLog (Timestamp DESC);
            CREATE INDEX IF NOT EXISTS IX_ContextUpdateLog_Source ON ContextUpdateLog (Source);
            CREATE INDEX IF NOT EXISTS IX_ObsidianWeeklySummaries_WeekStart ON ObsidianWeeklySummaries (WeekStart DESC);
            CREATE INDEX IF NOT EXISTS IX_VaultNotes_FilePath ON VaultNotes (FilePath);
            CREATE INDEX IF NOT EXISTS IX_NoteChunks_NoteId ON NoteChunks (NoteId);
            """
        );

        // Create sqlite-vec virtual table for vector search (if extension is loaded)
        // text-embedding-3-small produces 1536-dimensional vectors
        try
        {
            await conn.ExecuteAsync(
                """
                CREATE VIRTUAL TABLE IF NOT EXISTS vec_note_chunks USING vec0(
                    chunk_id TEXT PRIMARY KEY,
                    embedding float[1536]
                );
                """
            );
        }
        catch
        {
            // sqlite-vec extension not available - vector search will be disabled
        }
    }
}
