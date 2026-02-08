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

        var sources = items.Select(x => x.Source).Distinct().ToArray();
        var instructionsBySource = await _instructionService.GetBySourcesAsync(sources, ct);
        var skillInstructions = cfg?.Content;
        var period = weekly ? "weekly" : "daily";
        var result = await _summarizer.SummarizeUnifiedAsync(items, instructionsBySource, taskName, BuildSkillPrompt(period, skillInstructions, obsidianAnalysisText), ct);

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

    private static string BuildSkillPrompt(string period, string? custom, string? obsidianAnalysis = null)
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
            sb.AppendLine("Structure the summary as four blocks separated by blank lines:");
            sb.AppendLine();
            sb.AppendLine("1. Encouragement (1-2 sentences, NO heading):");
            sb.AppendLine("   - Acknowledge specific accomplishments from YESTERDAY'S ACCOMPLISHMENTS");
            sb.AppendLine("   - Be warm but concise");
            sb.AppendLine();
            sb.AppendLine("2. Heads up (NO heading, only if there are concerns):");
            sb.AppendLine("   - Reference metrics (energy, motivation, stress, etc.) to support your points");
            sb.AppendLine("   - If energy/motivation was low, suggest self-care");
            sb.AppendLine("   - If stress was high, acknowledge it and suggest taking it easy");
            sb.AppendLine("   - If yesterday's journal tone was negative, be supportive");
            sb.AppendLine("   - If everything looks good, skip this block entirely");
            sb.AppendLine();
            sb.AppendLine("3. Agenda (this is the ONLY block with a heading — use 'Agenda:'):");
            sb.AppendLine("   - Combine calendar events with TODAY'S PLANNED TASKS into a single list");
            sb.AppendLine("   - Show priority [*] and attention [!] tasks prominently");
            sb.AppendLine("   - Include pending tasks alongside calendar events");
            sb.AppendLine();
            sb.AppendLine("4. Other context (NO heading, brief):");
            sb.AppendLine("   - Journal mood/highlights if notable");
            sb.AppendLine("   - Metrics summary in one compact line (e.g. Energy 6, Motivation 7, Stress 3)");
            sb.AppendLine("   - Habits summary in one compact line (e.g. Soul 5, Body 3, Indulging 2)");
            sb.AppendLine("   - Any other relevant info (emails, personal notes)");
            sb.AppendLine("   - Do NOT repeat metrics already mentioned in the heads-up block");
            sb.AppendLine();
            sb.AppendLine("Focus on being helpful and supportive. Keep it concise.");
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
