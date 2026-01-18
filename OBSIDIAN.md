# Obsidian Daily Notes Integration

## Overview

This document describes the integration of Obsidian daily notes as a context source for Digital Butler. Daily notes are synced from the Obsidian vault to the server via rsync, then parsed and stored in SQLite for AI context and trend analysis.

---

## Data Flow

```
Obsidian Vault (local)
        │
        │ rsync (cron, every hour)
        ▼
/var/notes/04 archive/journal/daily notes/
        │
        │ ObsidianDailyNotesContextSource.FetchAsync() (hourly via Scheduler)
        ▼
Parse Markdown + YAML Frontmatter
        │
        ▼
┌─────────────────────────────────────────────────────────────────┐
│                    ObsidianDailyNotes Table                     │
├─────────────────────────────────────────────────────────────────┤
│ Date (PK) │ Metrics │ Habits │ Tasks │ Notes │ UpdatedAt │ ... │
└─────────────────────────────────────────────────────────────────┘
        │
        │ ContextUpdateLog (audit trail)
        ▼
┌─────────────────────────────────────────────────────────────────┐
│                     ContextUpdateLog Table                      │
├─────────────────────────────────────────────────────────────────┤
│ Timestamp │ Source │ ItemsAdded │ ItemsUpdated │ Message │ ... │
└─────────────────────────────────────────────────────────────────┘
```

---

## Configuration

| Environment Variable | Description | Default |
|---------------------|-------------|---------|
| `OBSIDIAN_VAULT_PATH` | Path to synced vault root | `/var/notes` |
| `OBSIDIAN_DAILY_NOTES_PATTERN` | Glob pattern for daily notes | `04 archive/journal/daily notes/*.md` |
| `OBSIDIAN_DAILY_NOTES_LOOKBACK_DAYS` | How many days back to scan | `30` |

---

## Database Schema

### ObsidianDailyNotes Table

Stores parsed daily notes with structured frontmatter data.

```sql
CREATE TABLE IF NOT EXISTS ObsidianDailyNotes (
    -- Primary key: the date of the daily note
    Date TEXT NOT NULL PRIMARY KEY,  -- Format: YYYY-MM-DD

    -- Numeric metrics (nullable, user may not track all)
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

    -- Habit/activity tracking (count = total emojis in array, items = JSON array)
    -- Example: ["🍟🍟🍟chips 150g", "🍟chips 50g"] → count=4, items=JSON
    SoulCount INTEGER NULL,
    SoulItems TEXT NULL,        -- JSON: ["🌄go outside", "🌅 calm morning", ...]
    BodyCount INTEGER NULL,
    BodyItems TEXT NULL,        -- JSON: ["🚶🚶walking x2", "☀️ morning sun", ...]
    AreasCount INTEGER NULL,
    AreasItems TEXT NULL,       -- JSON: ["😊joy", "🚀life_improvement", ...]
    LifeCount INTEGER NULL,
    LifeItems TEXT NULL,        -- JSON: [...]
    IndulgingCount INTEGER NULL,
    IndulgingItems TEXT NULL,   -- JSON: ["🍟🍟🍟chips 150g", "🕹️gaming 30m", ...]
    WeatherItems TEXT NULL,     -- JSON: ["🌥️", "☁️", "🌨️"] (no count, weather is descriptive)

    -- Task tracking
    CompletedTasks TEXT NULL,  -- JSON: ["Task 1", "Task 2", ...]
    PendingTasks TEXT NULL,    -- JSON: ["Task 3", "Task 4", ...]

    -- Free-form content
    Notes TEXT NULL,           -- Journal section text (markdown)
    Tags TEXT NULL,            -- JSON: ["harmony", "motivation", ...]

    -- Metadata
    FilePath TEXT NOT NULL,    -- Original file path for debugging
    FileModifiedAt TEXT NULL,  -- File modification time (for change detection)
    CreatedAt TEXT NOT NULL,   -- First import timestamp
    UpdatedAt TEXT NOT NULL    -- Last update timestamp
);

CREATE INDEX IF NOT EXISTS IX_ObsidianDailyNotes_Date ON ObsidianDailyNotes (Date DESC);
```

### ContextUpdateLog Table

Audit trail for all context source updates (Obsidian, Calendar, Gmail, etc.).

```sql
CREATE TABLE IF NOT EXISTS ContextUpdateLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,           -- When the update ran (UTC ISO 8601)
    Source TEXT NOT NULL,              -- "Obsidian", "GoogleCalendar", "Gmail", etc.
    Status TEXT NOT NULL,              -- "Success", "PartialSuccess", "Failed"
    ItemsScanned INTEGER NOT NULL,     -- Total items examined
    ItemsAdded INTEGER NOT NULL,       -- New items inserted
    ItemsUpdated INTEGER NOT NULL,     -- Existing items modified
    ItemsUnchanged INTEGER NOT NULL,   -- Items with no changes
    DurationMs INTEGER NOT NULL,       -- How long the update took
    Message TEXT NULL,                 -- Human-readable summary or error details
    Details TEXT NULL                  -- JSON with additional info (errors, warnings, etc.)
);

CREATE INDEX IF NOT EXISTS IX_ContextUpdateLog_Timestamp ON ContextUpdateLog (Timestamp DESC);
CREATE INDEX IF NOT EXISTS IX_ContextUpdateLog_Source ON ContextUpdateLog (Source);
```

---

## Parsing Logic

### Daily Note Structure

```markdown
---
life_satisfaction: 7
self-esteem: 6
presence: 6
energy: 5
motivation: 5
optimism: 5
stress: 6
irritability: 6
obsession: 7
offline_time: 4
meditation_minutes: 0
soul:
  - 🌄go outside
  - 🌅 calm morning
body:
  - 🚶🚶walking x2
areas:
  - 😊joy
life:
indulging:
  - 🍟🍟🍟chips 150g
weather:
  - 🌥️
weight:
tags:
  - journal/daily
parent:
  - "[[daily journal hub]]"
journal: daily
journal-date: 2026-01-17
---
# Tasks
- [x] Completed task 1
- [x] Completed task 2
- [ ] Pending task 1

# Journal
- Free-form journal entry
- Another line of notes
#harmony #motivation
```

### Parsing Rules

1. **Date Extraction:**
   - Primary: `journal-date` frontmatter field
   - Fallback: Parse from filename (`YYYY-MM-DD.md`)

2. **Numeric Metrics:**
   - Map frontmatter keys to columns (snake_case → PascalCase)
   - Accept `null`/empty as NULL in database

3. **Array Fields (soul, body, areas, life, indulging, weather):**
   - Parse YAML arrays into two columns each:
     - `{Field}Count`: Total number of emojis across all items
     - `{Field}Items`: JSON array of the raw strings
   - Emoji counting logic:
     ```
     Input:  ["🍟🍟🍟chips 150g", "🍟chips 50g", "🕹️🕹️🕹️gaming 2h"]
     Count:  3 + 1 + 3 = 7
     Items:  ["🍟🍟🍟chips 150g", "🍟chips 50g", "🕹️🕹️🕹️gaming 2h"]
     ```
   - Count all leading emoji characters in each item (before non-emoji text begins)
   - Empty arrays → NULL for both columns
   - Weather: Items only (no count, weather is descriptive)

4. **Tasks:**
   - Scan for markdown checkboxes in `# Tasks` section
   - `- [x]` → CompletedTasks (extract text after checkbox)
   - `- [ ]` → PendingTasks
   - Ignore Obsidian Tasks plugin query blocks (` ```tasks ... ``` `)

5. **Notes:**
   - Extract content after `# Journal` heading (or similar)
   - Strip inline tags (`#hashtag`) into Tags field
   - Preserve markdown formatting
   - Truncate to 8000 chars if needed

6. **Tags:**
   - Combine frontmatter `tags` array with inline `#tags` from journal
   - Deduplicate and normalize (lowercase, remove `journal/` prefix)

### Ignored Frontmatter

These fields are not stored (metadata only):
- `parent` (Obsidian linking)
- `journal` (always "daily")
- Any unknown fields

---

## Implementation

### New Files

```
DigitalButler.Context/
├── ObsidianDailyNotesContextSource.cs    # IContextSource implementation
├── ObsidianDailyNotesParser.cs           # Markdown/YAML parsing logic
└── ObsidianDailyNote.cs                  # Model class

DigitalButler.Data/
├── Models/ObsidianDailyNote.cs           # Data model
├── Repositories/ObsidianDailyNotesRepository.cs
└── Repositories/ContextUpdateLogRepository.cs

DigitalButler.Common/
└── Models.cs                             # Add ContextSource.Obsidian enum value
```

### ObsidianDailyNote Model

```csharp
public class ObsidianDailyNote
{
    public DateOnly Date { get; set; }

    // Numeric metrics
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

    // Habit tracking (count = total emojis, items = JSON array)
    public int? SoulCount { get; set; }
    public List<string>? SoulItems { get; set; }
    public int? BodyCount { get; set; }
    public List<string>? BodyItems { get; set; }
    public int? AreasCount { get; set; }
    public List<string>? AreasItems { get; set; }
    public int? LifeCount { get; set; }
    public List<string>? LifeItems { get; set; }
    public int? IndulgingCount { get; set; }
    public List<string>? IndulgingItems { get; set; }
    public List<string>? WeatherItems { get; set; }  // No count for weather

    // Tasks
    public List<string>? CompletedTasks { get; set; }
    public List<string>? PendingTasks { get; set; }

    // Content
    public string? Notes { get; set; }
    public List<string>? Tags { get; set; }

    // Metadata
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset? FileModifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

### ContextUpdateLog Model

```csharp
public class ContextUpdateLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;  // "Obsidian", "GoogleCalendar", "Gmail"
    public string Status { get; set; } = string.Empty;  // "Success", "PartialSuccess", "Failed"
    public int ItemsScanned { get; set; }
    public int ItemsAdded { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsUnchanged { get; set; }
    public int DurationMs { get; set; }
    public string? Message { get; set; }
    public string? Details { get; set; }  // JSON for warnings, errors, etc.
}
```

### ObsidianDailyNotesContextSource

```csharp
public sealed class ObsidianDailyNotesContextSource : IContextSource
{
    public ContextSource Source => ContextSource.Obsidian;

    private readonly string _vaultPath;
    private readonly string _dailyNotesPattern;
    private readonly int _lookbackDays;
    private readonly ObsidianDailyNotesRepository _repo;
    private readonly ContextUpdateLogRepository _logRepo;
    private readonly ILogger<ObsidianDailyNotesContextSource> _logger;

    public async Task<IReadOnlyList<ContextItem>> FetchAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var log = new ContextUpdateLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = "Obsidian"
        };

        try
        {
            // 1. Find all daily note files within lookback window
            var cutoffDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-_lookbackDays));
            var files = Directory.GetFiles(
                Path.Combine(_vaultPath, Path.GetDirectoryName(_dailyNotesPattern)!),
                Path.GetFileName(_dailyNotesPattern));

            var relevantFiles = files
                .Where(f => TryParseDateFromFilename(f, out var date) && date >= cutoffDate)
                .ToList();

            log.ItemsScanned = relevantFiles.Count;

            // 2. Parse each file
            var notes = new List<ObsidianDailyNote>();
            var errors = new List<string>();

            foreach (var file in relevantFiles)
            {
                try
                {
                    var note = ObsidianDailyNotesParser.Parse(file);
                    notes.Add(note);
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    _logger.LogWarning(ex, "Failed to parse daily note: {File}", file);
                }
            }

            // 3. Upsert to database, tracking changes
            var (added, updated, unchanged) = await _repo.UpsertManyAsync(notes, ct);

            log.ItemsAdded = added;
            log.ItemsUpdated = updated;
            log.ItemsUnchanged = unchanged;
            log.Status = errors.Count == 0 ? "Success" : "PartialSuccess";
            log.Message = $"Processed {notes.Count} daily notes ({added} new, {updated} updated)";

            if (errors.Count > 0)
            {
                log.Details = JsonSerializer.Serialize(new { Errors = errors });
            }

            // 4. Return as ContextItems for summarization pipeline
            return notes.Select(n => ToContextItem(n)).ToList();
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.Message = ex.Message;
            _logger.LogError(ex, "Failed to fetch Obsidian daily notes");
            return Array.Empty<ContextItem>();
        }
        finally
        {
            sw.Stop();
            log.DurationMs = (int)sw.ElapsedMilliseconds;
            await _logRepo.AddAsync(log, ct);
        }
    }

    private static ContextItem ToContextItem(ObsidianDailyNote note)
    {
        // Convert to ContextItem for use in summarization pipeline
        var body = new StringBuilder();

        // Add metrics summary
        if (note.Energy.HasValue || note.Motivation.HasValue)
        {
            body.AppendLine($"Energy: {note.Energy}, Motivation: {note.Motivation}");
        }

        // Add completed tasks
        if (note.CompletedTasks?.Count > 0)
        {
            body.AppendLine($"Completed: {string.Join(", ", note.CompletedTasks)}");
        }

        // Add journal notes (truncated)
        if (!string.IsNullOrEmpty(note.Notes))
        {
            var truncated = note.Notes.Length > 500
                ? note.Notes[..500] + "..."
                : note.Notes;
            body.AppendLine(truncated);
        }

        return new ContextItem
        {
            Source = ContextSource.Obsidian,
            Title = $"Daily Note: {note.Date:yyyy-MM-dd}",
            Body = body.ToString(),
            RelevantDate = note.Date.ToDateTime(TimeOnly.MinValue),
            IsTimeless = false,
            ExternalId = $"obsidian:daily:{note.Date:yyyy-MM-dd}",
            Category = "Daily Notes"
        };
    }
}
```

### Update Logic (Upsert)

```csharp
// In ObsidianDailyNotesRepository
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
        // Check if exists and compare FileModifiedAt
        var existing = await conn.QuerySingleOrDefaultAsync<ObsidianDailyNote>(
            "SELECT * FROM ObsidianDailyNotes WHERE Date = @Date",
            new { Date = note.Date.ToString("yyyy-MM-dd") },
            tx);

        if (existing == null)
        {
            // Insert new
            note.CreatedAt = now;
            note.UpdatedAt = now;
            await InsertAsync(conn, tx, note);
            added++;
        }
        else if (note.FileModifiedAt > existing.FileModifiedAt)
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

    await tx.CommitAsync(ct);
    return (added, updated, unchanged);
}
```

---

## Scheduler Integration

Add to `SchedulerService` hourly task:

```csharp
// In SchedulerService.cs
private async Task UpdateObsidianContextAsync(CancellationToken ct)
{
    var updater = _updaterRegistry.GetUpdater(ContextSource.Obsidian);
    if (updater != null)
    {
        await updater.UpdateAsync(ct);
    }
}
```

Schedule configuration:
- Run every hour at minute :30 (offset from calendar sync at :00)
- Or integrate with existing context update schedule

---

## Web Admin UI

### Context Update Logs Page

Display recent updates from all sources:

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Context Update History                                       [Refresh] │
├─────────────────────────────────────────────────────────────────────────┤
│ Time                │ Source    │ Status  │ Added │ Updated │ Duration │
├─────────────────────┼───────────┼─────────┼───────┼─────────┼──────────┤
│ 2026-01-18 10:30:00 │ Obsidian  │ ✓       │ 2     │ 1       │ 450ms    │
│ 2026-01-18 10:00:00 │ Calendar  │ ✓       │ 0     │ 5       │ 1.2s     │
│ 2026-01-18 10:00:00 │ Gmail     │ ⚠       │ 12    │ 0       │ 3.4s     │
│ 2026-01-18 09:30:00 │ Obsidian  │ ✓       │ 0     │ 0       │ 230ms    │
└─────────────────────────────────────────────────────────────────────────┘
```

Click on row to expand details (errors, warnings, etc.).

---

## Analysis Integration (Implemented)

### Daily Summary Analysis

When generating daily summaries (`/daily` command or scheduled), the system:

1. **Analyzes yesterday + today** data from `ObsidianDailyNotes`
2. **Computes averages** for metrics: Energy, Motivation, Stress, LifeSatisfaction, Optimism
3. **Sums habit counts**: Soul, Body, Areas, Indulging, MeditationMinutes
4. **Collects tasks**: Completed and pending tasks lists
5. **Extracts journal highlights**: First meaningful line from each day's notes
6. **Compares to**:
   - Day before yesterday (primary)
   - This week average (fallback)
   - Last week average (fallback)
7. **Injects analysis** as a `ContextItem` with source `Obsidian` into the AI prompt

### Weekly Summary Analysis

When generating weekly summaries (`/weekly` command or scheduled), the system:

1. **Analyzes current week** (Monday to today) from `ObsidianDailyNotes`
2. **Computes all metrics and totals** similar to daily
3. **Compares to**:
   - Last week (primary)
   - Last 4 weeks average (fallback)
4. **Stores the summary** in `ObsidianWeeklySummaries` table for historical comparison
5. **Injects analysis** into the AI prompt with comparison deltas

### Analysis Output Format

The analysis is formatted as structured text for the AI:

```
=== Obsidian Daily Notes Analysis (Jan 17 - Jan 18) ===
Days with data: 2

METRICS:
  Energy: 5.0 (+0.5 [better])
  Motivation: 5.5 (-1.0 [worse])
  Life Satisfaction: 7.0
  Stress: 6.0 (+1.0 [worse])
  (compared to day before yesterday)

HABITS:
  Soul activities: 5
  Body activities: 3
  Indulging: 8
  Meditation: 0 min

TASKS:
  Completed: 4
    - Task 1
    - Task 2
  Pending: 2
    - Pending task 1

TOP TAGS: harmony, motivation

JOURNAL HIGHLIGHTS:
  - First meaningful line from today's journal
  - First meaningful line from yesterday's journal
```

### Weekly Summary Storage

Weekly summaries are stored in `ObsidianWeeklySummaries` for:
- Historical trend tracking
- Week-over-week comparisons
- Future `/trends` command support

---

## Future Enhancements

### Dedicated `/trends` Command

Add a `/trends` command for on-demand trend analysis without full summary generation.

### Additional Note Types

The same pattern can extend to:
- Weekly reviews
- Project notes
- Reading notes

Each would get its own parser and potentially its own table.

---

## Migration Notes

### ContextSource Enum Update

```csharp
public enum ContextSource
{
    GoogleCalendar,  // 0
    Gmail,           // 1
    Personal,        // 2
    Obsidian,        // 3 (was Other)
    Other            // 4 (new)
}
```

Note: This changes the numeric value of `Other` from 3 to 4. If any existing data uses `Other`, it needs migration.

### Schema Migration

Add to `ButlerSchemaInitializer`:

```csharp
// Create ObsidianDailyNotes table
await conn.ExecuteAsync(@"
    CREATE TABLE IF NOT EXISTS ObsidianDailyNotes (
        Date TEXT NOT NULL PRIMARY KEY,
        LifeSatisfaction INTEGER NULL,
        -- ... all columns from schema above
    );
");

// Create ContextUpdateLog table
await conn.ExecuteAsync(@"
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
");

// Create indexes
await conn.ExecuteAsync(@"
    CREATE INDEX IF NOT EXISTS IX_ObsidianDailyNotes_Date
    ON ObsidianDailyNotes (Date DESC);

    CREATE INDEX IF NOT EXISTS IX_ContextUpdateLog_Timestamp
    ON ContextUpdateLog (Timestamp DESC);

    CREATE INDEX IF NOT EXISTS IX_ContextUpdateLog_Source
    ON ContextUpdateLog (Source);
");
```

---

## Dependencies

Add to `DigitalButler.Context.csproj`:

```xml
<PackageReference Include="YamlDotNet" Version="16.*" />
```

YamlDotNet is used to parse YAML frontmatter from markdown files.
