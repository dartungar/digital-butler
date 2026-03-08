using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalButler.Context;

public sealed class ObsidianCaptureSettingsService
{
    public const string InboxNotePathKey = "obsidian.capture.inboxNotePath";
    public const string MediaFolderPathKey = "obsidian.capture.mediaFolderPath";
    public const string DailyNoteTextTemplateKey = "obsidian.capture.dailyNoteTextTemplate";
    public const string DailyNoteMediaTemplateKey = "obsidian.capture.dailyNoteMediaTemplate";
    public const string InboxNoteTextTemplateKey = "obsidian.capture.inboxNoteTextTemplate";
    public const string InboxNoteMediaTemplateKey = "obsidian.capture.inboxNoteMediaTemplate";

    private const string LegacyDailyNoteTemplateKey = "obsidian.capture.dailyNoteTemplate";
    private const string LegacyInboxNoteTemplateKey = "obsidian.capture.inboxNoteTemplate";

    public const string DefaultTextTemplate = "{{text}}";
    public const string DefaultMediaTemplate = "{{media}}";

    private readonly AppSettingsRepository _repo;

    public ObsidianCaptureSettingsService(AppSettingsRepository repo)
    {
        _repo = repo;
    }

    public async Task<ObsidianCaptureSettings> GetAsync(CancellationToken ct = default)
    {
        var inboxNotePath = await _repo.GetAsync(InboxNotePathKey, ct);
        var mediaFolderPath = await _repo.GetAsync(MediaFolderPathKey, ct);
        var dailyNoteTextTemplate = await _repo.GetAsync(DailyNoteTextTemplateKey, ct);
        var dailyNoteMediaTemplate = await _repo.GetAsync(DailyNoteMediaTemplateKey, ct);
        var inboxNoteTextTemplate = await _repo.GetAsync(InboxNoteTextTemplateKey, ct);
        var inboxNoteMediaTemplate = await _repo.GetAsync(InboxNoteMediaTemplateKey, ct);
        var legacyDailyNoteTemplate = await _repo.GetAsync(LegacyDailyNoteTemplateKey, ct);
        var legacyInboxNoteTemplate = await _repo.GetAsync(LegacyInboxNoteTemplateKey, ct);

        var useLegacyDailyTemplate = string.IsNullOrWhiteSpace(dailyNoteTextTemplate) && !string.IsNullOrWhiteSpace(legacyDailyNoteTemplate);
        var useLegacyInboxTemplate = string.IsNullOrWhiteSpace(inboxNoteTextTemplate) && !string.IsNullOrWhiteSpace(legacyInboxNoteTemplate);

        return new ObsidianCaptureSettings
        {
            InboxNotePath = NormalizeOrDefault(inboxNotePath, "_inbox/_inbox.md"),
            MediaFolderPath = NormalizeOrDefault(mediaFolderPath, "_inbox"),
            DailyNoteTextTemplate = ResolveTemplate(useLegacyDailyTemplate ? legacyDailyNoteTemplate : dailyNoteTextTemplate, DefaultTextTemplate),
            DailyNoteMediaTemplate = ResolveTemplate(useLegacyDailyTemplate ? string.Empty : dailyNoteMediaTemplate, DefaultMediaTemplate),
            InboxNoteTextTemplate = ResolveTemplate(useLegacyInboxTemplate ? legacyInboxNoteTemplate : inboxNoteTextTemplate, DefaultTextTemplate),
            InboxNoteMediaTemplate = ResolveTemplate(useLegacyInboxTemplate ? string.Empty : inboxNoteMediaTemplate, DefaultMediaTemplate)
        };
    }

    public async Task SaveAsync(ObsidianCaptureSettings settings, CancellationToken ct = default)
    {
        var normalized = new ObsidianCaptureSettings
        {
            InboxNotePath = NormalizeOrDefault(settings.InboxNotePath, "_inbox/_inbox.md"),
            MediaFolderPath = NormalizeOrDefault(settings.MediaFolderPath, "_inbox"),
            DailyNoteTextTemplate = NormalizeTemplate(settings.DailyNoteTextTemplate),
            DailyNoteMediaTemplate = NormalizeTemplate(settings.DailyNoteMediaTemplate),
            InboxNoteTextTemplate = NormalizeTemplate(settings.InboxNoteTextTemplate),
            InboxNoteMediaTemplate = NormalizeTemplate(settings.InboxNoteMediaTemplate)
        };

        await _repo.UpsertAsync(InboxNotePathKey, normalized.InboxNotePath, ct);
        await _repo.UpsertAsync(MediaFolderPathKey, normalized.MediaFolderPath, ct);
        await _repo.UpsertAsync(DailyNoteTextTemplateKey, normalized.DailyNoteTextTemplate, ct);
        await _repo.UpsertAsync(DailyNoteMediaTemplateKey, normalized.DailyNoteMediaTemplate, ct);
        await _repo.UpsertAsync(InboxNoteTextTemplateKey, normalized.InboxNoteTextTemplate, ct);
        await _repo.UpsertAsync(InboxNoteMediaTemplateKey, normalized.InboxNoteMediaTemplate, ct);
    }

    private static string NormalizeOrDefault(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string ResolveTemplate(string? value, string fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        return NormalizeTemplate(value);
    }

    private static string NormalizeTemplate(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n").Trim();
        return normalized;
    }
}

public interface IObsidianCaptureService
{
    Task<ObsidianCaptureResult> AppendToTodayDailyNoteAsync(ObsidianCaptureRequest request, CancellationToken ct = default);
    Task<ObsidianCaptureResult> AppendToDailyNoteAsync(DateOnly date, ObsidianCaptureRequest request, CancellationToken ct = default);
    Task<ObsidianCaptureResult> AppendToInboxNoteAsync(ObsidianCaptureRequest request, CancellationToken ct = default);
}

public sealed class ObsidianCaptureService : IObsidianCaptureService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.Ordinal);

    private readonly ObsidianOptions _options;
    private readonly ObsidianCaptureSettingsService _settingsService;
    private readonly TimeZoneService _timeZoneService;
    private readonly ILogger<ObsidianCaptureService> _logger;

    public ObsidianCaptureService(
        IOptions<ObsidianOptions> options,
        ObsidianCaptureSettingsService settingsService,
        TimeZoneService timeZoneService,
        ILogger<ObsidianCaptureService> logger)
    {
        _options = options.Value;
        _settingsService = settingsService;
        _timeZoneService = timeZoneService;
        _logger = logger;
    }

    public async Task<ObsidianCaptureResult> AppendToTodayDailyNoteAsync(ObsidianCaptureRequest request, CancellationToken ct = default)
    {
        var now = await GetLocalNowAsync(ct);
        var today = DateOnly.FromDateTime(now.DateTime);
        return await AppendToDailyNoteAsync(today, request, ct);
    }

    public async Task<ObsidianCaptureResult> AppendToDailyNoteAsync(DateOnly date, ObsidianCaptureRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);

        var settings = await _settingsService.GetAsync(ct);
        var noteRelativePath = BuildDailyNoteRelativePath(date);
        var notePath = ResolveVaultPath(noteRelativePath);
        var now = await GetLocalNowAsync(ct);

        var result = await AppendAsync(
            notePath,
            $"daily note for {date:yyyy-MM-dd}",
            settings.DailyNoteTextTemplate,
            settings.DailyNoteMediaTemplate,
            request,
            now,
            ct);

        _logger.LogInformation("Appended content to Obsidian daily note {NotePath}", result.NotePath);
        return result;
    }

    public async Task<ObsidianCaptureResult> AppendToInboxNoteAsync(ObsidianCaptureRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);

        var settings = await _settingsService.GetAsync(ct);
        var notePath = ResolveVaultPath(settings.InboxNotePath);
        var now = await GetLocalNowAsync(ct);

        var result = await AppendAsync(
            notePath,
            "inbox note",
            settings.InboxNoteTextTemplate,
            settings.InboxNoteMediaTemplate,
            request,
            now,
            ct);
        _logger.LogInformation("Appended content to Obsidian inbox note {NotePath}", result.NotePath);
        return result;
    }

    private async Task<ObsidianCaptureResult> AppendAsync(
        string notePath,
        string targetDescription,
        string textTemplate,
        string mediaTemplate,
        ObsidianCaptureRequest request,
        DateTimeOffset localNow,
        CancellationToken ct)
    {
        var settings = await _settingsService.GetAsync(ct);
        EnsureMarkdownNotePath(notePath);
        Directory.CreateDirectory(Path.GetDirectoryName(notePath) ?? _options.VaultPath);

        string? mediaPath = null;
        if (request.MediaBytes is { Length: > 0 })
        {
            mediaPath = await SaveMediaAsync(settings.MediaFolderPath, request, localNow, ct);
        }

        var entry = BuildEntry(textTemplate, mediaTemplate, request.TextContent, mediaPath);
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new InvalidOperationException("Obsidian capture template produced an empty entry.");
        }

        var noteLock = FileLocks.GetOrAdd(notePath, static _ => new SemaphoreSlim(1, 1));
        await noteLock.WaitAsync(ct);
        try
        {
            await AppendEntrySafelyAsync(notePath, entry, ct);
        }
        finally
        {
            noteLock.Release();
        }

        return new ObsidianCaptureResult
        {
            TargetDescription = targetDescription,
            NotePath = GetVaultRelativePath(notePath),
            MediaFileName = mediaPath is null ? null : Path.GetFileName(mediaPath),
            MediaPath = mediaPath
        };
    }

    private async Task<string> SaveMediaAsync(string mediaFolderPath, ObsidianCaptureRequest request, DateTimeOffset localNow, CancellationToken ct)
    {
        var mediaDirectory = ResolveVaultPath(mediaFolderPath);
        Directory.CreateDirectory(mediaDirectory);

        var extension = NormalizeExtension(request.MediaFileExtension);
        var baseName = localNow.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);
        var suffix = 1;

        while (true)
        {
            var fileName = suffix == 1 ? baseName + extension : $"{baseName}_{suffix - 1}{extension}";
            var mediaPath = Path.Combine(mediaDirectory, fileName);

            try
            {
                await using var stream = new FileStream(mediaPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await stream.WriteAsync(request.MediaBytes!, ct);
                await stream.FlushAsync(ct);
                return GetVaultRelativePath(mediaPath);
            }
            catch (IOException) when (File.Exists(mediaPath))
            {
                suffix++;
            }
        }
    }

    private string BuildDailyNoteRelativePath(DateOnly date)
    {
        var dailyNotesDirectory = Path.GetDirectoryName(_options.DailyNotesPattern) ?? string.Empty;
        return Path.Combine(dailyNotesDirectory, $"{date:yyyy-MM-dd}.md");
    }

    private string ResolveVaultPath(string configuredPath)
    {
        var vaultRoot = Path.GetFullPath(_options.VaultPath);
        var combined = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(vaultRoot, configuredPath.Replace('/', Path.DirectorySeparatorChar));

        var fullPath = Path.GetFullPath(combined);
        if (!fullPath.Equals(vaultRoot, StringComparison.Ordinal) &&
            !fullPath.StartsWith(vaultRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Configured Obsidian path must stay inside the vault.");
        }

        return fullPath;
    }

    private string GetVaultRelativePath(string fullPath)
    {
        var vaultRoot = Path.GetFullPath(_options.VaultPath);
        return Path.GetRelativePath(vaultRoot, fullPath).Replace('\\', '/');
    }

    private static async Task AppendEntrySafelyAsync(string notePath, string entry, CancellationToken ct)
    {
        await using var stream = new FileStream(notePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var separator = await GetAppendSeparatorAsync(stream, ct);
        stream.Seek(0, SeekOrigin.End);

        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        if (separator.Length > 0)
        {
            await writer.WriteAsync(separator.AsMemory(), ct);
        }

        await writer.WriteAsync(entry.AsMemory(), ct);
        if (!entry.EndsWith('\n'))
        {
            await writer.WriteAsync("\n".AsMemory(), ct);
        }

        await writer.FlushAsync(ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> GetAppendSeparatorAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length == 0)
        {
            return string.Empty;
        }

        var bytesToRead = (int)Math.Min(stream.Length, 4);
        var buffer = new byte[bytesToRead];
        stream.Seek(-bytesToRead, SeekOrigin.End);
        var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);
        var tail = Encoding.UTF8.GetString(buffer, 0, read);

        if (tail.EndsWith("\n", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return "\n";
    }

    private async Task<DateTimeOffset> GetLocalNowAsync(CancellationToken ct)
    {
        var timeZone = await _timeZoneService.GetTimeZoneInfoAsync(ct);
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
    }

    private static string BuildEntry(string textTemplate, string mediaTemplate, string? textContent, string? mediaPath)
    {
        var text = textContent?.Trim() ?? string.Empty;
        var media = string.IsNullOrWhiteSpace(mediaPath) ? string.Empty : $"![[{mediaPath}]]";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var renderedText = RenderTemplate(textTemplate, text, media);
            if (!string.IsNullOrWhiteSpace(renderedText))
            {
                parts.Add(renderedText);
            }
        }

        if (!string.IsNullOrWhiteSpace(media))
        {
            var renderedMedia = RenderTemplate(mediaTemplate, text, media);
            if (!string.IsNullOrWhiteSpace(renderedMedia))
            {
                parts.Add(renderedMedia);
            }
        }

        return string.Join("\n", parts).Trim();
    }

    private static string RenderTemplate(string template, string text, string media)
    {
        return template
            .Replace("\r\n", "\n")
            .Replace("{{text}}", text, StringComparison.Ordinal)
            .Replace("{{value}}", text, StringComparison.Ordinal)
            .Replace("{{media}}", media, StringComparison.Ordinal)
            .Trim();
    }

    private static string NormalizeExtension(string? extension)
    {
        var normalized = string.IsNullOrWhiteSpace(extension) ? ".jpg" : extension.Trim();
        if (!normalized.StartsWith(".", StringComparison.Ordinal))
        {
            normalized = "." + normalized;
        }

        return normalized.ToLowerInvariant();
    }

    private static void EnsureMarkdownNotePath(string notePath)
    {
        if (!string.Equals(Path.GetExtension(notePath), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Obsidian note target must be a Markdown file.");
        }
    }

    private static void ValidateRequest(ObsidianCaptureRequest request)
    {
        if (!request.HasContent)
        {
            throw new InvalidOperationException("Nothing to add to Obsidian.");
        }
    }
}
