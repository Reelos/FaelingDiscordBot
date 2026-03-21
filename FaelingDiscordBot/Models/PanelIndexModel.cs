namespace FaelingDiscordBot.Models;

public class PanelIndexModel
{
    public List<PanelIndexEntryModel> Panels { get; set; } = new();
}

public class PanelIndexEntryModel
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Visible { get; set; } = true;
    public ulong? MessageId { get; set; }
}