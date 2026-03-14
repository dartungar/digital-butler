using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ObsidianBotService>();

await builder.Build().RunAsync();

public sealed class ObsidianBotService : BackgroundService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.Ordinal);

    private readonly ILogger<ObsidianBotService> _logger;
    private readonly string _vaultPath;
    private readonly string _dailyNotesPattern;
    private readonly string _inboxNotePath;
    private readonly string _mediaFolderPath;
    private readonly TimeZoneInfo _timeZone;
    private readonly long _allowedUserId;
    private readonly ITelegramBotClient _bot;
    private readonly ConcurrentDictionary<long, PendingCapture> _pendingByChat = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _awaitingDateByChat = new();

    private int _offset;

    public ObsidianBotService(ILogger<ObsidianBotService> logger)
    {
        _logger = logger;

        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is required.");
        }

        var userIdRaw = Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_ID");
        if (string.IsNullOrWhiteSpace(userIdRaw) || !long.TryParse(userIdRaw, out _allowedUserId))
        {
            throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID is required and must be numeric.");
        }

        var tzId = Environment.GetEnvironmentVariable("BUTLER_TIMEZONE") ?? "UTC";
        _timeZone = ResolveTimeZone(tzId);

        _vaultPath = Path.GetFullPath(Environment.GetEnvironmentVariable("OBSIDIAN_VAULT_PATH") ?? "/var/notes");
        _dailyNotesPattern = Environment.GetEnvironmentVariable("OBSIDIAN_DAILY_NOTES_PATTERN") ?? "04 archive/journal/daily journal/*.md";
        _inboxNotePath = Environment.GetEnvironmentVariable("OBSIDIAN_INBOX_NOTE_PATH") ?? "_inbox/_inbox.md";
        _mediaFolderPath = Environment.GetEnvironmentVariable("OBSIDIAN_MEDIA_FOLDER_PATH") ?? "_inbox";

        Directory.CreateDirectory(_vaultPath);
        _bot = new TelegramBotClient(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMeAsync(stoppingToken);
        _logger.LogInformation("Obsidian bot started as @{Username}", me.Username);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdatesAsync(
                    offset: _offset,
                    timeout: 30,
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    _offset = update.Id + 1;
                    await HandleUpdateAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallbackAsync(callbackQuery, ct);
            return;
        }

        if (update.Message is { } message)
        {
            await HandleMessageAsync(message, ct);
        }
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var userId = message.From?.Id;

        if (userId != _allowedUserId)
        {
            await _bot.SendTextMessageAsync(chatId, "Unauthorized.", cancellationToken: ct);
            return;
        }

        if (_awaitingDateByChat.TryGetValue(chatId, out var setAt) && DateTimeOffset.UtcNow - setAt < TimeSpan.FromMinutes(5) && !string.IsNullOrWhiteSpace(message.Text) && !message.Text.StartsWith('/'))
        {
            await HandleDateInputAsync(chatId, message.Text.Trim(), ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            var text = message.Text.Trim();

            if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
            {
                await _bot.SendTextMessageAsync(chatId,
                    "Send text, voice, or photo and I will save it to Obsidian.\nUse /add <text> for quick add.",
                    replyMarkup: BuildMainReplyKeyboard(),
                    cancellationToken: ct);
                return;
            }

            if (text.StartsWith("/add", StringComparison.OrdinalIgnoreCase))
            {
                var content = text[4..].Trim();
                if (string.IsNullOrWhiteSpace(content))
                {
                    await _bot.SendTextMessageAsync(chatId, "Usage: /add your note text", replyMarkup: BuildMainReplyKeyboard(), cancellationToken: ct);
                    return;
                }

                await PromptDestinationAsync(chatId, new PendingCapture { TextContent = content }, ct);
                return;
            }

            if (text.StartsWith('/'))
            {
                await _bot.SendTextMessageAsync(chatId, "Unknown command. Use /help.", replyMarkup: BuildMainReplyKeyboard(), cancellationToken: ct);
                return;
            }

            await PromptDestinationAsync(chatId, new PendingCapture { TextContent = text }, ct);
            return;
        }

        if (message.Voice is not null)
        {
            var bytes = await DownloadFileAsync(message.Voice.FileId, ct);
            await PromptDestinationAsync(chatId, new PendingCapture
            {
                MediaBytes = bytes,
                MediaFileExtension = ".ogg",
                TextContent = null
            }, ct);
            return;
        }

        if (message.Photo is { Length: > 0 })
        {
            var largest = message.Photo.OrderByDescending(p => p.FileSize).First();
            var bytes = await DownloadFileAsync(largest.FileId, ct);
            await PromptDestinationAsync(chatId, new PendingCapture
            {
                MediaBytes = bytes,
                MediaFileExtension = ".jpg",
                TextContent = string.IsNullOrWhiteSpace(message.Caption) ? null : message.Caption.Trim()
            }, ct);
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = callbackQuery.Message?.Chat.Id;
        if (chatId is null || string.IsNullOrWhiteSpace(callbackQuery.Data))
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        if (callbackQuery.From.Id != _allowedUserId)
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Unauthorized.", cancellationToken: ct);
            return;
        }

        var data = callbackQuery.Data;
        if (!data.StartsWith("obs:", StringComparison.Ordinal))
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var action = data[4..];
        await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        if (action == "cancel")
        {
            _pendingByChat.TryRemove(chatId.Value, out _);
            _awaitingDateByChat.TryRemove(chatId.Value, out _);
            await EditOrSendAsync(callbackQuery.Message, "Cancelled.", ct);
            return;
        }

        if (!_pendingByChat.TryGetValue(chatId.Value, out var pending))
        {
            await EditOrSendAsync(callbackQuery.Message, "Session expired. Send content again.", ct);
            return;
        }

        if (action == "date")
        {
            _awaitingDateByChat[chatId.Value] = DateTimeOffset.UtcNow;
            await EditOrSendAsync(callbackQuery.Message, "Send date as YYYY-MM-DD", ct);
            return;
        }

        try
        {
            SaveResult result = action switch
            {
                "today" => await SaveToDailyNoteAsync(GetLocalDateNow(), pending, ct),
                "inbox" => await SaveToInboxAsync(pending, ct),
                _ => throw new InvalidOperationException("Unknown action.")
            };

            _pendingByChat.TryRemove(chatId.Value, out _);
            _awaitingDateByChat.TryRemove(chatId.Value, out _);

            await EditOrSendAsync(callbackQuery.Message, BuildSavedMessage(result), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save pending content");
            await EditOrSendAsync(callbackQuery.Message, $"Save failed: {ex.Message}", ct);
        }
    }

    private async Task HandleDateInputAsync(long chatId, string text, CancellationToken ct)
    {
        if (!_pendingByChat.TryGetValue(chatId, out var pending))
        {
            _awaitingDateByChat.TryRemove(chatId, out _);
            await _bot.SendTextMessageAsync(chatId, "Session expired. Send content again.", replyMarkup: BuildMainReplyKeyboard(), cancellationToken: ct);
            return;
        }

        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && !DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            await _bot.SendTextMessageAsync(chatId, "Invalid date. Use YYYY-MM-DD.", replyMarkup: BuildMainReplyKeyboard(), cancellationToken: ct);
            return;
        }

        try
        {
            var result = await SaveToDailyNoteAsync(date, pending, ct);
            _pendingByChat.TryRemove(chatId, out _);
            _awaitingDateByChat.TryRemove(chatId, out _);

            await _bot.SendTextMessageAsync(chatId, BuildSavedMessage(result), replyMarkup: BuildMainReplyKeyboard(), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save content to {Date}", date);
            await _bot.SendTextMessageAsync(chatId, $"Save failed: {ex.Message}", replyMarkup: BuildMainReplyKeyboard(), cancellationToken: ct);
        }
    }

    private async Task PromptDestinationAsync(long chatId, PendingCapture pending, CancellationToken ct)
    {
        if (!pending.HasContent)
        {
            await _bot.SendTextMessageAsync(chatId, "Nothing to save.", replyMarkup: BuildMainReplyKeyboard(), cancellationToken: ct);
            return;
        }

        _pendingByChat[chatId] = pending;
        _awaitingDateByChat.TryRemove(chatId, out _);

        await _bot.SendTextMessageAsync(chatId,
            "Where should I save it?",
            replyMarkup: BuildDestinationKeyboard(),
            cancellationToken: ct);
    }

    private async Task<SaveResult> SaveToDailyNoteAsync(DateOnly date, PendingCapture pending, CancellationToken ct)
    {
        var dailyDirectory = Path.GetDirectoryName(_dailyNotesPattern) ?? string.Empty;
        var relativePath = Path.Combine(dailyDirectory, $"{date:yyyy-MM-dd}.md");
        var notePath = ResolveVaultPath(relativePath);
        return await SaveToNoteAsync(notePath, $"daily note for {date:yyyy-MM-dd}", pending, ct);
    }

    private Task<SaveResult> SaveToInboxAsync(PendingCapture pending, CancellationToken ct)
    {
        var notePath = ResolveVaultPath(_inboxNotePath);
        return SaveToNoteAsync(notePath, "inbox note", pending, ct);
    }

    private async Task<SaveResult> SaveToNoteAsync(string notePath, string target, PendingCapture pending, CancellationToken ct)
    {
        EnsureMarkdownPath(notePath);
        Directory.CreateDirectory(Path.GetDirectoryName(notePath) ?? _vaultPath);

        string? mediaRelativePath = null;
        if (pending.MediaBytes is { Length: > 0 })
        {
            mediaRelativePath = await SaveMediaAsync(pending.MediaBytes, pending.MediaFileExtension, ct);
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(pending.TextContent))
        {
            lines.Add(pending.TextContent.Trim());
        }
        if (!string.IsNullOrWhiteSpace(mediaRelativePath))
        {
            lines.Add($"![[{mediaRelativePath}]]");
        }

        var entry = string.Join("\n", lines).Trim();
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new InvalidOperationException("Empty capture payload.");
        }

        var fileLock = FileLocks.GetOrAdd(notePath, static _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct);
        try
        {
            await AppendToFileAsync(notePath, entry, ct);
        }
        finally
        {
            fileLock.Release();
        }

        return new SaveResult(target, ToVaultRelativePath(notePath), mediaRelativePath);
    }

    private async Task<string> SaveMediaAsync(byte[] mediaBytes, string extension, CancellationToken ct)
    {
        var mediaDirectory = ResolveVaultPath(_mediaFolderPath);
        Directory.CreateDirectory(mediaDirectory);

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone);
        var cleanExt = NormalizeExtension(extension);
        var baseName = now.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);
        var suffix = 0;

        while (true)
        {
            var fileName = suffix == 0 ? $"{baseName}{cleanExt}" : $"{baseName}_{suffix}{cleanExt}";
            var absolute = Path.Combine(mediaDirectory, fileName);
            try
            {
                await using var stream = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await stream.WriteAsync(mediaBytes, ct);
                await stream.FlushAsync(ct);
                return ToVaultRelativePath(absolute);
            }
            catch (IOException) when (System.IO.File.Exists(absolute))
            {
                suffix++;
            }
        }
    }

    private async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken ct)
    {
        var file = await _bot.GetFileAsync(fileId, ct);
        if (file.FilePath is null)
        {
            throw new InvalidOperationException("Telegram file path is empty.");
        }

        await using var stream = new MemoryStream();
        await _bot.DownloadFileAsync(file.FilePath, stream, ct);
        return stream.ToArray();
    }

    private async Task AppendToFileAsync(string path, string text, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var separator = await GetSeparatorAsync(stream, ct);

        stream.Seek(0, SeekOrigin.End);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        if (!string.IsNullOrEmpty(separator))
        {
            await writer.WriteAsync(separator.AsMemory(), ct);
        }
        await writer.WriteAsync(text.AsMemory(), ct);
        if (!text.EndsWith('\n'))
        {
            await writer.WriteAsync("\n".AsMemory(), ct);
        }
        await writer.FlushAsync(ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> GetSeparatorAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length == 0)
        {
            return string.Empty;
        }

        var bytesToRead = (int)Math.Min(4, stream.Length);
        var buffer = new byte[bytesToRead];
        stream.Seek(-bytesToRead, SeekOrigin.End);
        var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);
        var tail = Encoding.UTF8.GetString(buffer, 0, read);
        return tail.EndsWith('\n') ? string.Empty : "\n";
    }

    private string ResolveVaultPath(string configuredPath)
    {
        var combined = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_vaultPath, configuredPath.Replace('/', Path.DirectorySeparatorChar));

        var full = Path.GetFullPath(combined);
        if (!full.Equals(_vaultPath, StringComparison.Ordinal) &&
            !full.StartsWith(_vaultPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path must stay inside vault.");
        }

        return full;
    }

    private string ToVaultRelativePath(string fullPath)
    {
        return Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
    }

    private DateOnly GetLocalDateNow()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone);
        return DateOnly.FromDateTime(now.DateTime);
    }

    private static string NormalizeExtension(string ext)
    {
        var value = string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext.Trim();
        if (!value.StartsWith('.'))
        {
            value = "." + value;
        }

        return value.ToLowerInvariant();
    }

    private static void EnsureMarkdownPath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Target note must be a .md file.");
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    private async Task EditOrSendAsync(Message? source, string text, CancellationToken ct)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            await _bot.EditMessageTextAsync(source.Chat.Id, source.MessageId, text, replyMarkup: null, cancellationToken: ct);
        }
        catch
        {
            await _bot.SendTextMessageAsync(source.Chat.Id, text, replyMarkup: BuildMainReplyKeyboard(), cancellationToken: ct);
        }
    }

    private static string BuildSavedMessage(SaveResult result)
    {
        var msg = $"Saved to {result.Target}: {result.NotePath}";
        if (!string.IsNullOrWhiteSpace(result.MediaPath))
        {
            msg += $"\nMedia: {result.MediaPath}";
        }

        return msg;
    }

    private static ReplyKeyboardMarkup BuildMainReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("/add") },
            new[] { new KeyboardButton("/help") }
        })
        {
            ResizeKeyboard = true
        };
    }

    private static InlineKeyboardMarkup BuildDestinationKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Today's daily note", "obs:today") },
            new[] { InlineKeyboardButton.WithCallbackData("Other date", "obs:date") },
            new[] { InlineKeyboardButton.WithCallbackData("Inbox note", "obs:inbox") },
            new[] { InlineKeyboardButton.WithCallbackData("Cancel", "obs:cancel") }
        });
    }
}

public sealed class PendingCapture
{
    public string? TextContent { get; init; }
    public byte[]? MediaBytes { get; init; }
    public string MediaFileExtension { get; init; } = ".jpg";

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(TextContent) ||
        (MediaBytes is { Length: > 0 });
}

public readonly record struct SaveResult(string Target, string NotePath, string? MediaPath);
