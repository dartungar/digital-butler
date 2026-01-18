namespace DigitalButler.Context;

public class ObsidianOptions
{
    public string VaultPath { get; set; } = "/var/notes";
    public string DailyNotesPattern { get; set; } = "04 archive/journal/daily notes/*.md";
    public int LookbackDays { get; set; } = 30;
}
