using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Text;
using DigitalButler.Data;

namespace DigitalButler.Context;

public sealed class GmailContextSource : IContextSource
{
    private readonly GmailOptions _options;
    private readonly ILogger<GmailContextSource> _logger;

    public GmailContextSource(IOptions<GmailOptions> options, ILogger<GmailContextSource> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ContextSource Source => ContextSource.Gmail;

    public async Task<IReadOnlyList<ContextItem>> FetchAsync(CancellationToken ct = default)
    {
        var accounts = ParseAccounts(_options).ToList();
        if (accounts.Count == 0)
        {
            _logger.LogInformation("No Gmail accounts configured (set GMAIL_ACCOUNTS)");
            return Array.Empty<ContextItem>();
        }

        var results = new List<ContextItem>(capacity: 256);
        foreach (var account in accounts)
        {
            try
            {
                var items = await FetchAccountAsync(account, ct);
                results.AddRange(items);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Gmail for account '{AccountName}'", account.Name);
            }
        }

        return results;
    }

    private sealed record AccountCfg(
        string Name,
        string Host,
        int Port,
        bool UseSsl,
        string Username,
        string Password,
        bool UnreadOnly,
        int DaysBack,
        int MaxMessages
    );

    private static IEnumerable<AccountCfg> ParseAccounts(GmailOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Accounts))
        {
            yield break;
        }

        foreach (var rawAccount in options.Accounts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawAccount.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(parts[0]) ? "Gmail" : parts[0].Trim();
            var username = parts[1].Trim();
            var password = parts[2].Trim();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                continue;
            }

            // Optional overrides
            var unreadOnly = options.UnreadOnlyDefault;
            if (parts.Length >= 4 && TryParseBool(parts[3], out var b1)) unreadOnly = b1;

            var daysBack = options.DaysBackDefault;
            if (parts.Length >= 5 && int.TryParse(parts[4], out var i1) && i1 > 0) daysBack = i1;

            var maxMessages = options.MaxMessagesDefault;
            if (parts.Length >= 6 && int.TryParse(parts[5], out var i2) && i2 > 0) maxMessages = i2;

            yield return new AccountCfg(
                Name: name,
                Host: options.Host,
                Port: options.Port,
                UseSsl: options.UseSsl,
                Username: username,
                Password: password,
                UnreadOnly: unreadOnly,
                DaysBack: daysBack,
                MaxMessages: maxMessages
            );
        }
    }

    private async Task<IReadOnlyList<ContextItem>> FetchAccountAsync(AccountCfg account, CancellationToken ct)
    {
        using var client = new ImapClient();
        client.CheckCertificateRevocation = true;

        var socketOptions = account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(account.Host, account.Port, socketOptions, ct);
        await client.AuthenticateAsync(account.Username, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var since = DateTime.UtcNow.Date.AddDays(-Math.Abs(account.DaysBack));
        SearchQuery query = SearchQuery.DeliveredAfter(since);
        if (account.UnreadOnly)
        {
            query = query.And(SearchQuery.NotSeen);
        }

        var uids = await inbox.SearchAsync(query, ct);
        if (uids.Count == 0)
        {
            await client.DisconnectAsync(true, ct);
            return Array.Empty<ContextItem>();
        }

        var ordered = uids.OrderByDescending(u => u.Id).Take(Math.Max(1, account.MaxMessages)).ToList();
        var summaries = await inbox.FetchAsync(
            ordered,
            MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId,
            ct);

        var results = new List<ContextItem>(summaries.Count);
        foreach (var summary in summaries)
        {
            var envelope = summary.Envelope;
            if (envelope is null)
            {
                continue;
            }

            var messageId = envelope.MessageId;
            var externalId = !string.IsNullOrWhiteSpace(messageId)
                ? $"gmail:{account.Username}:{messageId.Trim()}"
                : $"gmail:{account.Username}:{inbox.UidValidity}:{summary.UniqueId.Id}";

            var dateUtc = summary.InternalDate?.ToUniversalTime() ?? envelope.Date?.ToUniversalTime();
            var subject = string.IsNullOrWhiteSpace(envelope.Subject) ? "(No subject)" : envelope.Subject;
            var from = FormatMailboxList(envelope.From);

            var body = new StringBuilder();
            body.AppendLine($"Account: {account.Name}");
            body.AppendLine($"From: {from}");
            body.AppendLine($"Subject: {subject}");
            if (dateUtc is not null)
            {
                body.AppendLine($"Date (UTC): {dateUtc:O}");
            }
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                body.AppendLine($"Message-Id: {messageId}");
            }

            results.Add(new ContextItem
            {
                Source = ContextSource.Gmail,
                ExternalId = externalId,
                Title = Truncate(subject, 512),
                Body = Truncate(body.ToString().Trim(), 4000),
                RelevantDate = dateUtc,
                IsTimeless = false,
                Category = account.UnreadOnly ? "Email (Unread)" : "Email",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await client.DisconnectAsync(true, ct);
        return results;
    }

    private static bool TryParseBool(string? value, out bool result)
    {
        if (value is null)
        {
            result = default;
            return false;
        }

        var v = value.Trim();
        if (bool.TryParse(v, out result))
        {
            return true;
        }

        if (v == "1") { result = true; return true; }
        if (v == "0") { result = false; return true; }

        return false;
    }

    private static string FormatMailboxList(InternetAddressList? list)
    {
        if (list is null || list.Count == 0)
        {
            return "";
        }

        return string.Join(", ",
            list.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));
    }

    private static string Truncate(string value, int maxLen)
    {
        if (value.Length <= maxLen)
        {
            return value;
        }
        return value[..maxLen];
    }
}
