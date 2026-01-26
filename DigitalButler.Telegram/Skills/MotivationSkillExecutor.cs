using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Skills;
using DigitalButler.Skills.VaultSearch;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Telegram.Skills;

public sealed class MotivationSkillExecutor : IMotivationSkillExecutor
{
    private readonly ContextService _contextService;
    private readonly SkillInstructionService _skillInstructionService;
    private readonly ISummarizationService _summarizer;
    private readonly IAiContextAugmenter _aiContext;
    private readonly IVaultEnrichmentService _vaultEnrichment;
    private readonly TimeZoneService _tzService;
    private readonly ILogger<MotivationSkillExecutor> _logger;

    public MotivationSkillExecutor(
        ContextService contextService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        IVaultEnrichmentService vaultEnrichment,
        TimeZoneService tzService,
        ILogger<MotivationSkillExecutor> logger)
    {
        _contextService = contextService;
        _skillInstructionService = skillInstructionService;
        _summarizer = summarizer;
        _aiContext = aiContext;
        _vaultEnrichment = vaultEnrichment;
        _tzService = tzService;
        _logger = logger;
    }

    public Task<string> ExecuteAsync(string? userQuery, CancellationToken ct)
    {
        return ExecuteAsync(userQuery, vaultQuery: null, startDate: null, endDate: null, ct);
    }

    public async Task<string> ExecuteAsync(
        string? userQuery,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        var tz = await _tzService.GetTimeZoneInfoAsync(ct);
        var items = await _contextService.GetRelevantAsync(daysBack: 30, take: 250, ct: ct);
        var citations = new List<ObsidianCitation>();

        var cfg = await GetSkillConfigAsync(ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Motivation, cfg?.ContextSourcesMask ?? -1);
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
                    _logger.LogInformation("Added {Count} items from vault enrichment for motivation", enrichment.ContextItems.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich motivation with vault context");
            }
        }

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
        var prompt = BuildSkillPrompt(userQuery, cfg?.Content, citations.Count > 0);
        var result = await _summarizer.SummarizeAsync(items, instructionsBySource, "motivation", prompt, ct);

        // Append citations if any
        if (citations.Count > 0)
        {
            result += _vaultEnrichment.FormatCitations(citations);
        }

        return result;
    }

    private async Task<SkillInstruction?> GetSkillConfigAsync(CancellationToken ct)
    {
        var dict = await _skillInstructionService.GetFullBySkillsAsync(new[] { ButlerSkill.Motivation }, ct);
        return dict.TryGetValue(ButlerSkill.Motivation, out var v) ? v : null;
    }

    private static string BuildSkillPrompt(string? userQuery, string? custom, bool hasVaultContext = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: motivation");

        if (!string.IsNullOrWhiteSpace(userQuery))
        {
            sb.AppendLine();
            sb.AppendLine($"User's message: \"{userQuery}\"");
            sb.AppendLine();
            sb.AppendLine("Write a motivational message that directly addresses what the user said.");
            sb.AppendLine("If the user's message relates to something in their Personal context, incorporate that context naturally.");
            sb.AppendLine("If the user's message is generic or unrelated to their context, focus on addressing their specific feeling or situation without forcing context references.");
        }
        else
        {
            sb.AppendLine("Write a short motivational message grounded in Personal context items.");
        }

        if (hasVaultContext)
        {
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: Use insights from the Obsidian notes provided as context to personalize your message.");
            sb.AppendLine("Reference specific achievements, progress, or themes from the notes when relevant.");
        }

        sb.AppendLine();
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
