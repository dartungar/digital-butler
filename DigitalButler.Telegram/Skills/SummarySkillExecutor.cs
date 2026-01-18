using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Skills;
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
    private readonly TimeZoneService _tzService;
    private readonly ILogger<SummarySkillExecutor> _logger;

    public SummarySkillExecutor(
        ContextService contextService,
        InstructionService instructionService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        IObsidianAnalysisService obsidianAnalysis,
        TimeZoneService tzService,
        ILogger<SummarySkillExecutor> logger)
    {
        _contextService = contextService;
        _instructionService = instructionService;
        _skillInstructionService = skillInstructionService;
        _summarizer = summarizer;
        _aiContext = aiContext;
        _obsidianAnalysis = obsidianAnalysis;
        _tzService = tzService;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(bool weekly, string taskName, CancellationToken ct)
    {
        var tz = await _tzService.GetTimeZoneInfoAsync(ct);
        var items = weekly
            ? await GetWeeklyItemsAsync(tz, ct)
            : await GetDailyItemsAsync(tz, ct);

        var skill = weekly ? ButlerSkill.WeeklySummary : ButlerSkill.DailySummary;
        var cfg = await GetSkillConfigAsync(skill, ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(skill, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

        // Add Obsidian analysis as context
        string? obsidianAnalysisText = null;
        ObsidianAnalysisResult? obsidianResult = null;
        try
        {
            obsidianResult = weekly
                ? await _obsidianAnalysis.AnalyzeWeeklyAsync(tz, ct)
                : await _obsidianAnalysis.AnalyzeDailyAsync(tz, ct);

            if (obsidianResult != null)
            {
                obsidianAnalysisText = _obsidianAnalysis.FormatAnalysisForPrompt(obsidianResult);
                items.Add(new ContextItem
                {
                    Source = ContextSource.Obsidian,
                    Title = weekly ? "Weekly Obsidian Analysis" : "Daily Obsidian Analysis",
                    Body = obsidianAnalysisText,
                    IsTimeless = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate Obsidian analysis for {TaskName}", taskName);
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
        var result = await _summarizer.SummarizeAsync(items, instructionsBySource, taskName, BuildSkillPrompt(period, skillInstructions, obsidianAnalysisText != null), ct);

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

    private static string BuildSkillPrompt(string period, string? custom, bool hasObsidianAnalysis = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: summary");
        sb.AppendLine($"Period: {period}");
        sb.AppendLine("Output a concise agenda with actionable highlights.");

        if (hasObsidianAnalysis)
        {
            sb.AppendLine();
            if (period == "daily")
            {
                sb.AppendLine("IMPORTANT: Include insights from the Obsidian daily notes analysis:");
                sb.AppendLine("- Highlight energy/motivation/stress levels and compare to previous days");
                sb.AppendLine("- Note habit activity counts (soul, body, indulging)");
                sb.AppendLine("- Summarize completed and pending tasks");
                sb.AppendLine("- Extract key themes from journal entries");
            }
            else
            {
                sb.AppendLine("IMPORTANT: Include insights from the Obsidian weekly analysis:");
                sb.AppendLine("- Summarize weekly trends in energy/motivation/stress");
                sb.AppendLine("- Compare metrics to last week (note improvements or concerns)");
                sb.AppendLine("- Highlight total habit activities and patterns");
                sb.AppendLine("- Note task completion progress");
                sb.AppendLine("- Identify recurring themes from journal entries and tags");
            }
        }

        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine(custom.Trim());
        }
        return sb.ToString();
    }
}
