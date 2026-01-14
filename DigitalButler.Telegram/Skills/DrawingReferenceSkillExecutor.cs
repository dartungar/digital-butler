using DigitalButler.Skills;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Telegram.Skills;

public sealed class DrawingReferenceSkillExecutor : IDrawingReferenceSkillExecutor
{
    private readonly IDrawingReferenceService _drawingRefService;
    private readonly ISubjectTranslator _subjectTranslator;
    private readonly IRandomDrawingTopicService _topicService;
    private readonly ILogger<DrawingReferenceSkillExecutor> _logger;

    public DrawingReferenceSkillExecutor(
        IDrawingReferenceService drawingRefService,
        ISubjectTranslator subjectTranslator,
        IRandomDrawingTopicService topicService,
        ILogger<DrawingReferenceSkillExecutor> logger)
    {
        _drawingRefService = drawingRefService;
        _subjectTranslator = subjectTranslator;
        _topicService = topicService;
        _logger = logger;
    }

    public string GetRandomTopic() => _topicService.GetRandomTopic();

    public async Task<string> ExecuteAsync(string subject, CancellationToken ct)
    {
        var original = subject.Trim();
        var translated = await _subjectTranslator.TranslateToEnglishAsync(original, ct);
        if (string.IsNullOrWhiteSpace(translated))
        {
            translated = original;
        }

        var result = await _drawingRefService.GetReferenceAsync(translated, ct);
        if (result is null)
        {
            return $"I couldn't find a drawing reference for \"{original}\". Try a different subject?";
        }

        var header = string.Equals(original, translated, StringComparison.OrdinalIgnoreCase)
            ? $"Drawing reference for \"{original}\":"
            : $"Drawing reference for \"{original}\" (searching: \"{translated}\"):";

        return header + "\n" +
               $"{result.Value.ImageUrl}\n" +
               $"Photo by {result.Value.PhotographerName} on Unsplash: {result.Value.PhotoPageUrl}";
    }

    public static bool TryExtractSubject(string text, out string? subject)
    {
        subject = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lowered = text.Trim();

        static string? After(string input, string needle)
        {
            var idx = input.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return input[(idx + needle.Length)..].Trim();
        }

        var tail = After(lowered, "drawing reference for ")
                   ?? After(lowered, "reference for ")
                   ?? After(lowered, "draw ")
                   ?? After(lowered, "drawing ")
                   ?? After(lowered, "sketch ");

        if (string.IsNullOrWhiteSpace(tail))
        {
            return false;
        }

        // Strip leading filler words.
        var cleaned = tail.Trim().Trim('.', '!', '?', ':', ';', ',', '"', '\'', ')', '(', '[', ']', '{', '}');
        foreach (var stop in new[] { "some ", "a ", "an ", "the ", "my ", "any " })
        {
            if (cleaned.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[stop.Length..].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.StartsWith("practice", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reject vague/non-subject words
        var vague = new[]
        {
            "something", "anything", "stuff", "things", "thing",
            "time", "now", "today", "session", "practice",
            "please", "thanks", "help", "me", "it", "this", "that",
            "idk", "dunno", "whatever", "random", "surprise", "shit", "что-нибудь", "что-то"
        };
        if (vague.Any(v => cleaned.Equals(v, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        subject = cleaned;
        return true;
    }
}
