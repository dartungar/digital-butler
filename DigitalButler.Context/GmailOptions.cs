namespace DigitalButler.Context;

public sealed class GmailOptions
{
    /// <summary>
    /// IMAP host used for all accounts unless overridden in the account string.
    /// </summary>
    public string Host { get; set; } = "imap.gmail.com";

    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;

    public bool UnreadOnlyDefault { get; set; } = false;
    public int DaysBackDefault { get; set; } = 7;
    public int MaxMessagesDefault { get; set; } = 50;

    /// <summary>
    /// Multiple-account env var format:
    /// GMAIL_ACCOUNTS="name|username|appPassword[|unreadOnly][|daysBack][|maxMessages];name2|user2|pass2..."
    ///
    /// Examples:
    /// - "Personal|me@gmail.com|abcd efgh ijkl mnop|true|14|100"
    /// - "Work|me@company.com|app-pass;Personal|me@gmail.com|app-pass|false|7|50"
    /// </summary>
    public string? Accounts { get; set; }
}
