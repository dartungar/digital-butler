using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Skills;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Telegram.Skills;

public sealed class ActivitiesSkillExecutor : IActivitiesSkillExecutor
{
    private readonly ContextService _contextService;
    private readonly SkillInstructionService _skillInstructionService;
    private readonly ISummarizationService _summarizer;
    private readonly IAiContextAugmenter _aiContext;
    private readonly TimeZoneService _tzService;
    private readonly ILogger<ActivitiesSkillExecutor> _logger;

    public ActivitiesSkillExecutor(
        ContextService contextService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        TimeZoneService tzService,
        ILogger<ActivitiesSkillExecutor> logger)
    {
        _contextService = contextService;
        _skillInstructionService = skillInstructionService;
        _summarizer = summarizer;
        _aiContext = aiContext;
        _tzService = tzService;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(CancellationToken ct)
    {
        var tz = await _tzService.GetTimeZoneInfoAsync(ct);
        var items = await _contextService.GetRelevantAsync(daysBack: 14, take: 250, ct: ct);

        var cfg = await GetSkillConfigAsync(ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Activities, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

        if (cfg?.EnableAiContext == true)
        {
            var snippet = await _aiContext.GenerateAsync(ButlerSkill.Activities, items, "activities", ct);
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

        // Activities should be driven by Personal context and per-skill instructions only.
        var instructionsBySource = new Dictionary<ContextSource, string>();
        var prompt = BuildSkillPrompt(cfg?.Content);
        return await _summarizer.SummarizeAsync(items, instructionsBySource, "activities", prompt, ct);
    }

    private async Task<SkillInstruction?> GetSkillConfigAsync(CancellationToken ct)
    {
        var dict = await _skillInstructionService.GetFullBySkillsAsync(new[] { ButlerSkill.Activities }, ct);
        return dict.TryGetValue(ButlerSkill.Activities, out var v) ? v : null;
    }

    private static string BuildSkillPrompt(string? custom)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: activities");
        sb.AppendLine("Suggest a small list of activities based on energy/mood signals in Personal context.");
        sb.AppendLine("Ignore calendar/events/emails unless they are part of Personal context.");
        sb.AppendLine("Prefer 3-7 bullet points with brief rationale.");
        sb.AppendLine("Do not quote the notes; use them only as signals.");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine(custom.Trim());
        }
        return sb.ToString();
    }
}
