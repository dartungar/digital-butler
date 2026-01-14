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
    private readonly TimeZoneService _tzService;
    private readonly ILogger<SummarySkillExecutor> _logger;

    public SummarySkillExecutor(
        ContextService contextService,
        InstructionService instructionService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        TimeZoneService tzService,
        ILogger<SummarySkillExecutor> logger)
    {
        _contextService = contextService;
        _instructionService = instructionService;
        _skillInstructionService = skillInstructionService;
        _summarizer = summarizer;
        _aiContext = aiContext;
        _tzService = tzService;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(bool weekly, string taskName, CancellationToken ct)
    {
        var tz = await _tzService.GetTimeZoneInfoAsync(ct);
        var items = weekly
            ? await GetWeeklyItemsAsync(tz, ct)
            : await GetDailyItemsAsync(tz, ct);

        var cfg = await GetSkillConfigAsync(ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Summary, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

        if (cfg?.EnableAiContext == true)
        {
            var snippet = await _aiContext.GenerateAsync(ButlerSkill.Summary, items, taskName, ct);
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
        return await _summarizer.SummarizeAsync(items, instructionsBySource, taskName, BuildSkillPrompt(period, skillInstructions), ct);
    }

    private async Task<SkillInstruction?> GetSkillConfigAsync(CancellationToken ct)
    {
        var dict = await _skillInstructionService.GetFullBySkillsAsync(new[] { ButlerSkill.Summary }, ct);
        return dict.TryGetValue(ButlerSkill.Summary, out var v) ? v : null;
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

    private static string BuildSkillPrompt(string period, string? custom)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: summary");
        sb.AppendLine($"Period: {period}");
        sb.AppendLine("Output a concise agenda with actionable highlights.");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine(custom.Trim());
        }
        return sb.ToString();
    }
}
