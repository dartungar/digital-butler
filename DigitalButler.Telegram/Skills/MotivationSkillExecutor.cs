using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Skills;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Telegram.Skills;

public sealed class MotivationSkillExecutor : IMotivationSkillExecutor
{
    private readonly ContextService _contextService;
    private readonly SkillInstructionService _skillInstructionService;
    private readonly ISummarizationService _summarizer;
    private readonly IAiContextAugmenter _aiContext;
    private readonly TimeZoneService _tzService;
    private readonly ILogger<MotivationSkillExecutor> _logger;

    public MotivationSkillExecutor(
        ContextService contextService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        TimeZoneService tzService,
        ILogger<MotivationSkillExecutor> logger)
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
        var items = await _contextService.GetRelevantAsync(daysBack: 30, take: 250, ct: ct);

        var cfg = await GetSkillConfigAsync(ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Motivation, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

        if (cfg?.EnableAiContext == true)
        {
            var snippet = await _aiContext.GenerateAsync(ButlerSkill.Motivation, items, "motivation", ct);
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

        // Motivation should be driven by Personal context and per-skill instructions only.
        var instructionsBySource = new Dictionary<ContextSource, string>();
        var prompt = BuildSkillPrompt(cfg?.Content);
        return await _summarizer.SummarizeAsync(items, instructionsBySource, "motivation", prompt, ct);
    }

    private async Task<SkillInstruction?> GetSkillConfigAsync(CancellationToken ct)
    {
        var dict = await _skillInstructionService.GetFullBySkillsAsync(new[] { ButlerSkill.Motivation }, ct);
        return dict.TryGetValue(ButlerSkill.Motivation, out var v) ? v : null;
    }

    private static string BuildSkillPrompt(string? custom)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: motivation");
        sb.AppendLine("Write a short motivational message grounded ONLY in Personal context items.");
        sb.AppendLine("Ignore calendar/events/emails unless they are part of Personal context.");
        sb.AppendLine("Do not summarize the notes; do not quote them; use them only as inspiration.");
        sb.AppendLine("Do not mention that you are an AI or that you were given 'context items'.");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine(custom.Trim());
        }
        return sb.ToString();
    }
}
