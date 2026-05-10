using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalButler.Context;

public sealed class ObsidianDailyNotesContextSource : IContextSource
{
    private readonly ObsidianOptions _options;
    private readonly ObsidianDailyNotesRepository _repo;
    private readonly ContextUpdateLogRepository _logRepo;
    private readonly ILogger<ObsidianDailyNotesContextSource> _logger;

    public ObsidianDailyNotesContextSource(
        IOptions<ObsidianOptions> options,
        ObsidianDailyNotesRepository repo,
        ContextUpdateLogRepository logRepo,
        ILogger<ObsidianDailyNotesContextSource> logger)
    {
        _options = options.Value;
        _repo = repo;
        _logRepo = logRepo;
        _logger = logger;
    }

    public ContextSource Source => ContextSource.Obsidian;

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
            // Build the full path pattern
            var dailyNotesDir = Path.Combine(_options.VaultPath, Path.GetDirectoryName(_options.DailyNotesPattern) ?? "");
            var filePattern = Path.GetFileName(_options.DailyNotesPattern);

            if (!Directory.Exists(dailyNotesDir))
            {
                _logger.LogWarning("Obsidian daily notes directory not found: {Path}", dailyNotesDir);
                log.Status = "Failed";
                log.Message = $"Directory not found: {dailyNotesDir}";
                return Array.Empty<ContextItem>();
            }

            // Find all daily note files within lookback window
            var cutoffDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-_options.LookbackDays));
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

            // Return as ContextItems for summarization pipeline
            return notes.Select(ToContextItem).ToList();
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

        // Add completed tasks
        if (note.CompletedTasks?.Count > 0)
        {
            body.AppendLine($"Completed tasks: {string.Join("; ", note.CompletedTasks)}");
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
            RelevantDate = note.Date.ToDateTime(TimeOnly.MinValue),
            IsTimeless = false,
            ExternalId = $"obsidian:daily:{note.Date:yyyy-MM-dd}",
            Category = "Daily Notes"
        };
    }
}
