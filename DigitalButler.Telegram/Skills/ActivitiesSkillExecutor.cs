using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Skills;
using DigitalButler.Skills.VaultSearch;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Telegram.Skills;

public sealed class ActivitiesSkillExecutor : IActivitiesSkillExecutor
{
    private readonly ContextService _contextService;
    private readonly SkillInstructionService _skillInstructionService;
    private readonly ISummarizationService _summarizer;
    private readonly IAiContextAugmenter _aiContext;
    private readonly IVaultEnrichmentService _vaultEnrichment;
    private readonly TimeZoneService _tzService;
    private readonly ILogger<ActivitiesSkillExecutor> _logger;

    public ActivitiesSkillExecutor(
        ContextService contextService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        IVaultEnrichmentService vaultEnrichment,
        TimeZoneService tzService,
        ILogger<ActivitiesSkillExecutor> logger)
    {
        _contextService = contextService;
        _skillInstructionService = skillInstructionService;
        _summarizer = summarizer;
        _aiContext = aiContext;
        _vaultEnrichment = vaultEnrichment;
        _tzService = tzService;
        _logger = logger;
    }

    public Task<string> ExecuteAsync(CancellationToken ct)
    {
        return ExecuteAsync(userQuery: null, vaultQuery: null, startDate: null, endDate: null, ct);
    }

    public async Task<string> ExecuteAsync(
        string? userQuery,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        var tz = await _tzService.GetTimeZoneInfoAsync(ct);
        var items = await _contextService.GetRelevantAsync(daysBack: 14, take: 250, ct: ct);
        var citations = new List<ObsidianCitation>();

        var cfg = await GetSkillConfigAsync(ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Activities, cfg?.ContextSourcesMask ?? -1);
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
                    _logger.LogInformation("Added {Count} items from vault enrichment for activities", enrichment.ContextItems.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich activities with vault context");
            }
        }

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
        var prompt = BuildSkillPrompt(userQuery, cfg?.Content, citations.Count > 0);
        var result = await _summarizer.SummarizeAsync(items, instructionsBySource, "activities", prompt, ct);

        // Append citations if any
        if (citations.Count > 0)
        {
            result += _vaultEnrichment.FormatCitations(citations);
        }

        return result;
    }

    private async Task<SkillInstruction?> GetSkillConfigAsync(CancellationToken ct)
    {
        var dict = await _skillInstructionService.GetFullBySkillsAsync(new[] { ButlerSkill.Activities }, ct);
        return dict.TryGetValue(ButlerSkill.Activities, out var v) ? v : null;
    }

    private static string BuildSkillPrompt(string? userQuery, string? custom, bool hasVaultContext = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: activities");

        if (!string.IsNullOrWhiteSpace(userQuery))
        {
            sb.AppendLine();
            sb.AppendLine($"User's request: \"{userQuery}\"");
            sb.AppendLine("Tailor activity suggestions to the user's specific request.");
        }
        else
        {
            sb.AppendLine("Suggest a small list of activities based on energy/mood signals in Personal context.");
        }

        if (hasVaultContext)
        {
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: Use insights from the Obsidian notes provided as context to personalize suggestions.");
            sb.AppendLine("Reference specific activities, patterns, or preferences from the notes when relevant.");
        }

        sb.AppendLine();
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
