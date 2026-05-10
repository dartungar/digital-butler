using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalButler.Context;

public sealed class ObsidianDailyNotesContextSource : IContextSource, ICategorizedStaleContextCleanupSource
{
    public const string DailyNotesCategory = "Daily Notes";

    private readonly ObsidianOptions _options;
    private readonly ObsidianDailyNotesRepository _repo;
    private readonly ContextUpdateLogRepository _logRepo;
    private readonly TimeZoneService _timeZoneService;
    private readonly ILogger<ObsidianDailyNotesContextSource> _logger;
    private bool _canCleanStaleItems;
    private DateTimeOffset? _cleanupWindowStartUtc;
    private DateTimeOffset? _cleanupWindowEndUtc;

    public ObsidianDailyNotesContextSource(
        IOptions<ObsidianOptions> options,
        ObsidianDailyNotesRepository repo,
        ContextUpdateLogRepository logRepo,
        TimeZoneService timeZoneService,
        ILogger<ObsidianDailyNotesContextSource> logger)
    {
        _options = options.Value;
        _repo = repo;
        _logRepo = logRepo;
        _timeZoneService = timeZoneService;
        _logger = logger;
    }

    public ContextSource Source => ContextSource.Obsidian;
    public bool CanCleanStaleItems => _canCleanStaleItems;
    public DateTimeOffset? CleanupWindowStartUtc => _cleanupWindowStartUtc;
    public DateTimeOffset? CleanupWindowEndUtc => _cleanupWindowEndUtc;
    public IReadOnlyCollection<string?> CleanupCategories { get; } = new[] { DailyNotesCategory };

    public async Task<IReadOnlyList<ContextItem>> FetchAsync(CancellationToken ct = default)
    {
        _canCleanStaleItems = false;
        _cleanupWindowStartUtc = null;
        _cleanupWindowEndUtc = null;

        var sw = Stopwatch.StartNew();
        var log = new ContextUpdateLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = "Obsidian"
        };

        try
        {
            // Build the full path pattern
            var dailyNotesDir = Path.Combine(_options.VaultPath, Path.GetDirectoryName(_options.DailyNotesPattern) ?? "");
            var filePattern = Path.GetFileName(_options.DailyNotesPattern);
            var tz = await _timeZoneService.GetTimeZoneInfoAsync(ct);
            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);

            if (!Directory.Exists(dailyNotesDir))
            {
                _logger.LogWarning("Obsidian daily notes directory not found: {Path}", dailyNotesDir);
                log.Status = "Failed";
                log.Message = $"Directory not found: {dailyNotesDir}";
                return Array.Empty<ContextItem>();
            }

            // Find all daily note files within lookback window
            var today = DateOnly.FromDateTime(localNow.DateTime);
            var cutoffDate = today.AddDays(-_options.LookbackDays);
            _cleanupWindowStartUtc = LocalDateStartUtc(cutoffDate, tz);
            _cleanupWindowEndUtc = LocalDateStartUtc(today.AddDays(1), tz);
            var files = Directory.GetFiles(dailyNotesDir, filePattern);

            var relevantFiles = files
                .Where(f => ObsidianDailyNotesParser.TryParseDateFromFilename(f, out var date) && date >= cutoffDate)
                .ToList();

            log.ItemsScanned = relevantFiles.Count;
            _logger.LogInformation("Found {Count} daily notes within lookback window ({Days} days)", relevantFiles.Count, _options.LookbackDays);

            // Parse each file
            var notes = new List<ObsidianDailyNote>();
            var errors = new List<string>();

            foreach (var file in relevantFiles)
            {
                try
                {
                    var note = ObsidianDailyNotesParser.Parse(file);
                    if (note.Date != default)
                    {
                        notes.Add(note);
                    }
                    else
                    {
                        errors.Add($"{Path.GetFileName(file)}: Could not determine date");
                        _logger.LogWarning("Could not determine date for daily note: {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    _logger.LogWarning(ex, "Failed to parse daily note: {File}", file);
                }
            }

            // Upsert to database, tracking changes
            var (added, updated, unchanged) = await _repo.UpsertManyAsync(notes, ct);

            log.ItemsAdded = added;
            log.ItemsUpdated = updated;
            log.ItemsUnchanged = unchanged;
            log.Status = errors.Count == 0 ? "Success" : "PartialSuccess";
            log.Message = $"Processed {notes.Count} daily notes ({added} new, {updated} updated, {unchanged} unchanged)";

            if (errors.Count > 0)
            {
                log.Details = JsonSerializer.Serialize(new { Errors = errors });
                _logger.LogWarning("Completed with {ErrorCount} errors", errors.Count);
            }

            _logger.LogInformation("Obsidian sync complete: {Added} added, {Updated} updated, {Unchanged} unchanged",
                added, updated, unchanged);

            _canCleanStaleItems = errors.Count == 0;

            // Return as ContextItems for summarization pipeline
            return notes.Select(note => ToContextItem(note, tz)).ToList();
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

    private static ContextItem ToContextItem(ObsidianDailyNote note, TimeZoneInfo tz)
    {
        var body = new StringBuilder();

        // Add metrics summary
        var metrics = new List<string>();
        if (note.Energy.HasValue) metrics.Add($"Energy: {note.Energy}");
        if (note.Motivation.HasValue) metrics.Add($"Motivation: {note.Motivation}");
        if (note.LifeSatisfaction.HasValue) metrics.Add($"Life satisfaction: {note.LifeSatisfaction}");
        if (note.Stress.HasValue) metrics.Add($"Stress: {note.Stress}");

        if (metrics.Count > 0)
        {
            body.AppendLine(string.Join(", ", metrics));
        }

        // Add habit counts
        var habits = new List<string>();
        if (note.SoulCount > 0) habits.Add($"Soul: {note.SoulCount}");
        if (note.BodyCount > 0) habits.Add($"Body: {note.BodyCount}");
        if (note.AreasCount > 0) habits.Add($"Areas: {note.AreasCount}");
        if (note.IndulgingCount > 0) habits.Add($"Indulging: {note.IndulgingCount}");

        if (habits.Count > 0)
        {
            body.AppendLine($"Habits: {string.Join(", ", habits)}");
        }

        // Add journal notes (truncated)
        if (!string.IsNullOrEmpty(note.Notes))
        {
            var truncated = note.Notes.Length > 500
                ? note.Notes[..500] + "..."
                : note.Notes;
            body.AppendLine();
            body.AppendLine(truncated);
        }

        return new ContextItem
        {
            Source = ContextSource.Obsidian,
            Title = $"Daily Note: {note.Date:yyyy-MM-dd}",
            Body = body.ToString().Trim(),
            RelevantDate = LocalDateStartUtc(note.Date, tz),
            IsTimeless = false,
            ExternalId = $"obsidian:daily:{note.Date:yyyy-MM-dd}",
            Category = DailyNotesCategory
        };
    }

    private static DateTimeOffset LocalDateStartUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
    }
}
