using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Skills;
using DigitalButler.Skills.VaultSearch;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Telegram.Skills;

public sealed class SummarySkillExecutor : ISummarySkillExecutor
{
    private readonly ContextService _contextService;
    private readonly InstructionService _instructionService;
    private readonly SkillInstructionService _skillInstructionService;
    private readonly ISummarizationService _summarizer;
    private readonly IAiContextAugmenter _aiContext;
    private readonly IObsidianAnalysisService _obsidianAnalysis;
    private readonly IVaultEnrichmentService _vaultEnrichment;
    private readonly TimeZoneService _tzService;
    private readonly ILogger<SummarySkillExecutor> _logger;

    public SummarySkillExecutor(
        ContextService contextService,
        InstructionService instructionService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        IObsidianAnalysisService obsidianAnalysis,
        IVaultEnrichmentService vaultEnrichment,
        TimeZoneService tzService,
        ILogger<SummarySkillExecutor> logger)
    {
        _contextService = contextService;
        _instructionService = instructionService;
        _skillInstructionService = skillInstructionService;
        _summarizer = summarizer;
        _aiContext = aiContext;
        _obsidianAnalysis = obsidianAnalysis;
        _vaultEnrichment = vaultEnrichment;
        _tzService = tzService;
        _logger = logger;
    }

    public Task<string> ExecuteAsync(bool weekly, string taskName, CancellationToken ct)
    {
        return ExecuteCoreAsync(weekly, taskName, vaultQuery: null, startDate: null, endDate: null, includeDailyAnalysisForDateWindow: false, ct);
    }

    public Task<string> ExecuteDailyForDateAsync(DateOnly date, string taskName, CancellationToken ct)
    {
        return ExecuteCoreAsync(weekly: false, taskName, vaultQuery: null, startDate: date, endDate: date, includeDailyAnalysisForDateWindow: true, ct);
    }

    public async Task<string> ExecuteAsync(
        bool weekly,
        string taskName,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        return await ExecuteCoreAsync(weekly, taskName, vaultQuery, startDate, endDate, includeDailyAnalysisForDateWindow: false, ct);
    }

    private async Task<string> ExecuteCoreAsync(
        bool weekly,
        string taskName,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        bool includeDailyAnalysisForDateWindow,
        CancellationToken ct)
    {
        var tz = await _tzService.GetTimeZoneInfoAsync(ct);
        List<ContextItem> items;
        List<ObsidianCitation> citations = new();

        // If we have a date range from temporal detection, use it instead of default window
        if (startDate.HasValue && endDate.HasValue)
        {
            var start = LocalDateStartUtc(startDate.Value, tz);
            var end = LocalDateStartUtc(endDate.Value.AddDays(1), tz);
            items = await _contextService.GetForWindowAsync(start, end, take: 500, ct: ct);
        }
        else
        {
            items = weekly
                ? await GetWeeklyItemsAsync(tz, ct)
                : await GetDailyItemsAsync(tz, ct);
        }

        var skill = weekly ? ButlerSkill.WeeklySummary : ButlerSkill.DailySummary;
        var cfg = await GetSkillConfigAsync(skill, ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(skill, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();
        if (!weekly)
        {
            items = items
                .Where(x => !string.Equals(x.Category, ObsidianDailyNotesContextSource.DailyNotesCategory, StringComparison.Ordinal))
                .ToList();
        }

        // Add vault enrichment if requested
        if (!string.IsNullOrWhiteSpace(vaultQuery) ||
            (startDate.HasValue && endDate.HasValue && !includeDailyAnalysisForDateWindow))
        {
            try
            {
                var enrichment = await _vaultEnrichment.EnrichAsync(vaultQuery, startDate, endDate, ct);
                if (enrichment.HasResults)
                {
                    items.AddRange(enrichment.ContextItems);
                    citations.AddRange(enrichment.Citations);
                    _logger.LogInformation("Added {Count} items from vault enrichment", enrichment.ContextItems.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich with vault context");
            }
        }

        // Get Obsidian analysis (only if no specific date range was provided)
        // Note: Analysis is included directly in the prompt, not as a context item, to avoid separate "Obsidian" section
        string? obsidianAnalysisText = null;
        ObsidianAnalysisResult? obsidianResult = null;
        if (!startDate.HasValue || !endDate.HasValue || includeDailyAnalysisForDateWindow)
        {
            try
            {
                obsidianResult = weekly
                    ? await _obsidianAnalysis.AnalyzeWeeklyAsync(tz, ct)
                    : await _obsidianAnalysis.AnalyzeDailyAsync(tz, ct);

                if (obsidianResult != null)
                {
                    obsidianAnalysisText = _obsidianAnalysis.FormatAnalysisForPrompt(obsidianResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate Obsidian analysis for {TaskName}", taskName);
            }
        }

        if (cfg?.EnableAiContext == true)
        {
            var snippet = await _aiContext.GenerateAsync(skill, items, taskName, ct);
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                items.Add(new ContextItem
                {
                    Source = ContextSource.Other,
                    Title = "AI self-thought",
                    Body = snippet.Trim(),
                    IsTimeless = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        // If the current window has no context items, but Obsidian analysis exists,
        // add it as a synthetic context item so summary generation still produces output.
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(obsidianAnalysisText))
        {
            items.Add(new ContextItem
            {
                Source = ContextSource.Obsidian,
                Title = weekly ? "Weekly notes analysis" : "Daily notes analysis",
                Body = obsidianAnalysisText.Trim(),
                RelevantDate = DateTimeOffset.UtcNow,
                IsTimeless = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ExternalId = $"obsidian:analysis:{(weekly ? "weekly" : "daily")}:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                Category = "Obsidian Analysis"
            });
        }

        if (items.Count == 0)
        {
            return "No context items found for this period. Try running /sync and verify enabled sources in settings.";
        }

        items = DeduplicateItems(items, tz);
        items = PrioritizeItems(items, weekly, tz);

        var sources = items.Select(x => x.Source).Distinct().ToArray();
        var instructionsBySource = await _instructionService.GetBySourcesAsync(sources, ct);
        var skillInstructions = SkillInstructionDefaults.ResolveContent(skill, cfg?.Content);
        var period = weekly ? "weekly" : "daily";
        var dataFreshnessNote = BuildDataFreshnessNote(items, weekly, tz);
        var agendaDate = !weekly && startDate.HasValue && endDate.HasValue && startDate.Value == endDate.Value
            ? startDate.Value
            : (DateOnly?)null;
        agendaDate ??= !weekly ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime) : null;
        var statsDate = !weekly ? obsidianResult?.PeriodStart : null;
        var result = await _summarizer.SummarizeUnifiedAsync(items, instructionsBySource, taskName, BuildSkillPrompt(period, skillInstructions, obsidianAnalysisText, dataFreshnessNote, agendaDate, statsDate), ct);

        // Append citations if any
        if (citations.Count > 0)
        {
            result += _vaultEnrichment.FormatCitations(citations);
        }

        // Store weekly summary for future comparisons
        if (weekly && obsidianResult != null)
        {
            try
            {
                await _obsidianAnalysis.StoreWeeklySummaryAsync(obsidianResult, result, ct);
                _logger.LogInformation("Stored weekly Obsidian summary for week starting {WeekStart}", obsidianResult.PeriodStart);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store weekly Obsidian summary");
            }
        }

        return result;
    }

    private async Task<SkillInstruction?> GetSkillConfigAsync(ButlerSkill skill, CancellationToken ct)
    {
        var dict = await _skillInstructionService.GetFullBySkillsAsync(new[] { skill }, ct);
        return dict.TryGetValue(skill, out var v) ? v : null;
    }

    private async Task<List<ContextItem>> GetDailyItemsAsync(TimeZoneInfo tz, CancellationToken ct)
    {
        var (start, end) = TimeWindowHelper.GetDailyWindow(tz);
        return await _contextService.GetForWindowAsync(start, end, take: 300, ct: ct);
    }

    private async Task<List<ContextItem>> GetWeeklyItemsAsync(TimeZoneInfo tz, CancellationToken ct)
    {
        var (start, end) = TimeWindowHelper.GetWeeklyWindow(tz);
        return await _contextService.GetForWindowAsync(start, end, take: 500, ct: ct);
    }

    private static DateTimeOffset LocalDateStartUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
    }

    private static List<ContextItem> DeduplicateItems(List<ContextItem> items, TimeZoneInfo tz)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ContextItem>(items.Count);

        foreach (var item in items)
        {
            var key = BuildDedupKey(item, tz);
            if (seen.Add(key))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string BuildDedupKey(ContextItem item, TimeZoneInfo tz)
    {
        if (item.Source == ContextSource.GoogleCalendar)
        {
            var localStart = item.RelevantDate is null
                ? ""
                : TimeZoneInfo.ConvertTime(item.RelevantDate.Value, tz).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

            return $"calendar|{localStart}|{NormalizeForDedup(item.Title)}";
        }

        if (string.Equals(item.Category, ObsidianTaskContextIndexer.Category, StringComparison.Ordinal))
        {
            var localDate = item.RelevantDate is null
                ? ""
                : TimeZoneInfo.ConvertTime(item.RelevantDate.Value, tz).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            return $"vault-task|{localDate}|{NormalizeForDedup(item.Title)}";
        }

        if (!string.IsNullOrWhiteSpace(item.ExternalId))
        {
            return $"{item.Source}|external|{item.ExternalId}";
        }

        return $"{item.Source}|{item.Category}|{item.RelevantDate:O}|{NormalizeForDedup(item.Title)}";
    }

    private static string NormalizeForDedup(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Where(ch => !char.IsPunctuation(ch) || ch is '#' or '/' or '-')
            .ToArray();

        return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static List<ContextItem> PrioritizeItems(List<ContextItem> items, bool weekly, TimeZoneInfo tz)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var maxItems = weekly ? 220 : 140;

        return items
            .Select(item => new { Item = item, Score = ScoreItem(item, now, weekly) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.RelevantDate ?? x.Item.UpdatedAt)
            .Take(maxItems)
            .Select(x => x.Item)
            .ToList();
    }

    private static double ScoreItem(ContextItem item, DateTimeOffset now, bool weekly)
    {
        var score = item.Source switch
        {
            ContextSource.GoogleCalendar => 30,
            ContextSource.Obsidian => 24,
            ContextSource.Gmail => 16,
            _ => 12
        };

        if (item.RelevantDate is DateTimeOffset relevant)
        {
            var delta = relevant - now;
            if (delta.TotalHours is >= -12 and <= 36)
            {
                score += 35;
            }
            else if (delta.TotalDays is >= -2 and <= 3)
            {
                score += 18;
            }
            else if (weekly && delta.TotalDays is >= -7 and <= 7)
            {
                score += 10;
            }
        }

        var text = $"{item.Title} {item.Body}".ToLowerInvariant();
        if (ContainsAny(text, "urgent", "asap", "deadline", "today", "tomorrow", "important", "[!]", "[*]", "pending", "overdue"))
        {
            score += 18;
        }

        if (item.Source == ContextSource.Gmail && ContainsAny(text, "newsletter", "promotion", "promo", "sale", "discount", "unsubscribe"))
        {
            score -= 18;
        }

        if (item.IsTimeless)
        {
            score -= 6;
        }

        var updateAgeHours = (now - item.UpdatedAt).TotalHours;
        if (updateAgeHours <= 12)
        {
            score += 6;
        }
        else if (updateAgeHours >= 72)
        {
            score -= 4;
        }

        return score;
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? BuildDataFreshnessNote(List<ContextItem> items, bool weekly, TimeZoneInfo tz)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var thresholdHours = weekly ? 36 : 12;
        var staleSources = items
            .GroupBy(x => x.Source)
            .Select(group =>
            {
                var latest = group.Max(x => x.UpdatedAt);
                var ageHours = (now - latest).TotalHours;
                return new { Source = group.Key, AgeHours = ageHours };
            })
            .Where(x => x.AgeHours > thresholdHours)
            .OrderByDescending(x => x.AgeHours)
            .ToList();

        if (staleSources.Count == 0)
        {
            return null;
        }

        var pieces = staleSources.Select(x => $"{x.Source}: latest update {Math.Round(x.AgeHours)}h ago");
        return string.Join(", ", pieces);
    }

    private static string BuildSkillPrompt(
        string period,
        string? custom,
        string? obsidianAnalysis = null,
        string? dataFreshness = null,
        DateOnly? agendaDate = null,
        DateOnly? statsDate = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: summary");
        sb.AppendLine($"Period: {period}");
        if (agendaDate.HasValue)
        {
            sb.AppendLine($"Agenda local date: {agendaDate.Value:yyyy-MM-dd}");
        }
        if (statsDate.HasValue)
        {
            sb.AppendLine($"Stats/reflection date: {statsDate.Value:yyyy-MM-dd}");
        }

        if (string.Equals(period, "daily", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("Agenda requirements: list Google Calendar events before all other agenda items.");
            if (agendaDate.HasValue)
            {
                sb.AppendLine("Build the Agenda for the agenda local date only.");
            }
            sb.AppendLine("For each calendar event, include the local start time; if an end time exists, include a time range in HH:mm-HH:mm format.");
            sb.AppendLine("Deduplicate agenda items: if the same calendar title has the same start time, or the same task text appears more than once, include it only once.");
            sb.AppendLine("Include active Obsidian Tasks plugin items from the vault when their due, scheduled, or start date lands in the daily window.");
            sb.AppendLine("Never treat note headings or section labels such as Tasks, Journal, Recurring, or Planned as agenda items.");
        }

        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine("=== SKILL INSTRUCTIONS ===");
            sb.AppendLine(custom.Trim());
        }

        if (string.Equals(period, "daily", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine("=== DAILY SUMMARY FORMAT OVERRIDES ===");
            if (agendaDate.HasValue && statsDate.HasValue)
            {
                sb.AppendLine($"Return exactly two sections with these exact headings: Agenda ({agendaDate.Value:yyyy-MM-dd}) and Yesterday ({statsDate.Value:yyyy-MM-dd}).");
            }
            else if (agendaDate.HasValue)
            {
                sb.AppendLine($"Return exactly two sections with these exact headings: Agenda ({agendaDate.Value:yyyy-MM-dd}) and Yesterday.");
            }
            else if (statsDate.HasValue)
            {
                sb.AppendLine($"Return exactly two sections with these exact headings: Agenda and Yesterday ({statsDate.Value:yyyy-MM-dd}).");
            }
            else
            {
                sb.AppendLine("Return exactly two sections: Agenda and Yesterday.");
            }
            sb.AppendLine("Agenda: compact bullets only. Calendar events first, then active tasks. Do not include duplicate events or duplicate tasks.");
            sb.AppendLine("Agenda must be built only from actual events/tasks; markdown section headings are not agenda items.");
            sb.AppendLine("Yesterday: write one short, natural note based on yesterday's Obsidian stats. Do not list raw metrics mechanically.");
            sb.AppendLine("Do not output task status counts such as completed, pending, rescheduled, or cancelled.");
            sb.AppendLine("Metric direction: higher is better for energy, motivation, life satisfaction, optimism, self esteem, and presence; 7+ is solid/positive and 4 or below is low.");
            sb.AppendLine("Metric direction: lower is better for stress, irritability, and obsession; 0-3 is OK/low and 7+ is elevated.");
        }

        if (!string.IsNullOrWhiteSpace(dataFreshness))
        {
            sb.AppendLine();
            sb.AppendLine("=== DATA FRESHNESS ===");
            sb.AppendLine(dataFreshness.Trim());
            sb.AppendLine("If any source appears stale, mention uncertainty briefly in the summary instead of speaking with full confidence.");
        }

        // Include Obsidian analysis directly in the prompt (not as a separate context source)
        if (!string.IsNullOrWhiteSpace(obsidianAnalysis))
        {
            sb.AppendLine();
            sb.AppendLine("=== OBSIDIAN DAILY NOTES DATA ===");
            sb.AppendLine(obsidianAnalysis);
        }

        return sb.ToString();
    }
}
