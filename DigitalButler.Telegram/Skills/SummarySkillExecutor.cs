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
        return ExecuteAsync(weekly, taskName, vaultQuery: null, startDate: null, endDate: null, ct);
    }

    public async Task<string> ExecuteAsync(
        bool weekly,
        string taskName,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        var tz = await _tzService.GetTimeZoneInfoAsync(ct);
        List<ContextItem> items;
        List<ObsidianCitation> citations = new();

        // If we have a date range from temporal detection, use it instead of default window
        if (startDate.HasValue && endDate.HasValue)
        {
            var start = new DateTimeOffset(startDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var end = new DateTimeOffset(endDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
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

        items = PrioritizeItems(items, weekly, tz);

        // Add vault enrichment if requested
        if (!string.IsNullOrWhiteSpace(vaultQuery) || (startDate.HasValue && endDate.HasValue))
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
        if (!startDate.HasValue || !endDate.HasValue)
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
                    Source = ContextSource.Personal,
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

        var sources = items.Select(x => x.Source).Distinct().ToArray();
        var instructionsBySource = await _instructionService.GetBySourcesAsync(sources, ct);
        var skillInstructions = cfg?.Content;
        var period = weekly ? "weekly" : "daily";
        var dataFreshnessNote = BuildDataFreshnessNote(items, weekly, tz);
        var result = await _summarizer.SummarizeUnifiedAsync(items, instructionsBySource, taskName, BuildSkillPrompt(period, skillInstructions, obsidianAnalysisText, dataFreshnessNote), ct);

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
            ContextSource.Personal => 28,
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

    private static string BuildSkillPrompt(string period, string? custom, string? obsidianAnalysis = null, string? dataFreshness = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: summary");
        sb.AppendLine($"Period: {period}");

        var hasObsidianAnalysis = !string.IsNullOrWhiteSpace(obsidianAnalysis);

        if (hasObsidianAnalysis && period == "daily")
        {
            sb.AppendLine();
            sb.AppendLine("LANGUAGE: Write the ENTIRE summary in a single language. Match the language of the custom skill instructions. If no custom instructions are provided, default to English. Do NOT mix languages.");
            sb.AppendLine("Do NOT separate content by source. Merge all sources into a single cohesive summary.");
            sb.AppendLine();
            sb.AppendLine("Write in a warm, direct, and practical tone. Avoid generic motivational fluff.");
            sb.AppendLine("Every claim should be grounded in provided data; do not invent facts.");
            sb.AppendLine();
            sb.AppendLine("Structure the summary with these blocks separated by blank lines:");
            sb.AppendLine();
            sb.AppendLine("1. Top priorities today:");
            sb.AppendLine("   - Exactly 3 bullets, ordered by impact/urgency");
            sb.AppendLine("   - For each: what it is, why it matters, and first concrete step");
            sb.AppendLine("   - Prioritize deadline/attention tasks and time-bound events");
            sb.AppendLine();
            sb.AppendLine("2. Human check-in:");
            sb.AppendLine("   - 1-2 sentences of grounded encouragement tied to specific progress");
            sb.AppendLine("   - If there are warning signals (high stress, low energy/motivation), acknowledge them briefly and suggest gentler pacing");
            sb.AppendLine();
            sb.AppendLine("3. Agenda:");
            sb.AppendLine("   - Compact timeline/list of today's events and planned tasks");
            sb.AppendLine("   - Surface [*] and [!] tasks prominently");
            sb.AppendLine();
            sb.AppendLine("4. Keep in mind:");
            sb.AppendLine("   - 1 short line on what to deprioritize/ignore today to reduce overload");
            sb.AppendLine("   - 1 compact metrics line only if meaningful");
            sb.AppendLine();
            sb.AppendLine("Keep total length concise and useful, not verbose.");
        }
        else if (hasObsidianAnalysis && period == "weekly")
        {
            sb.AppendLine();
            sb.AppendLine("LANGUAGE: Write the ENTIRE summary in a single language. Match the language of the custom skill instructions. If no custom instructions are provided, default to English. Do NOT mix languages.");
            sb.AppendLine("Output a concise summary of what happened during this period.");
            sb.AppendLine("Write it as natural flowing text without section headings.");
            sb.AppendLine();
            sb.AppendLine("Include relevant insights from the analysis below:");
            sb.AppendLine("- Weekly trends in energy/motivation/stress");
            sb.AppendLine("- Habit activity patterns");
            sb.AppendLine("- Task completion progress");
            sb.AppendLine("- Recurring themes from journal entries");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("LANGUAGE: Write the ENTIRE summary in a single language. Match the language of the custom skill instructions. If no custom instructions are provided, default to English. Do NOT mix languages.");
            sb.AppendLine("Output a concise summary of what happened during this period.");
            sb.AppendLine("Write it as natural flowing text without section headings.");
            sb.AppendLine("Focus on facts and events. Do NOT include action items, advice, recommendations, or suggestions.");
        }

        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine(custom.Trim());
        }

        if (!string.IsNullOrWhiteSpace(dataFreshness))
        {
            sb.AppendLine();
            sb.AppendLine("=== DATA FRESHNESS ===");
            sb.AppendLine(dataFreshness.Trim());
            sb.AppendLine("If any source appears stale, mention uncertainty briefly in the summary instead of speaking with full confidence.");
        }

        // Include Obsidian analysis directly in the prompt (not as a separate context source)
        if (hasObsidianAnalysis)
        {
            sb.AppendLine();
            sb.AppendLine("=== PERSONAL DAILY NOTES DATA ===");
            sb.AppendLine(obsidianAnalysis);
        }

        return sb.ToString();
    }
}
