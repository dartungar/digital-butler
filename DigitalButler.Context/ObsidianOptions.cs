namespace DigitalButler.Context;

public class ObsidianOptions
{
    public string VaultPath { get; set; } = "/var/notes";
    public string DailyNotesPattern { get; set; } = "04 archive/journal/daily notes/*.md";
    public int LookbackDays { get; set; } = 30;

    /// <summary>
    /// Name of the Obsidian vault (used for obsidian:// protocol links).
    /// </summary>
    public string VaultName { get; set; } = "Notes";
}
