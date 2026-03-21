using FaelingDiscordBot.Models;
using System.Text;
using System.Text.Json;

namespace FaelingDiscordBot.Services;

public class PanelStorageService
{
    private readonly string _contentRoot;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public PanelStorageService(string? contentRoot = null)
    {
        _contentRoot = contentRoot ?? Path.Combine(AppContext.BaseDirectory, "content");
    }

    public async Task<(bool Success, string Message)> MovePanelAsync(
        ulong guildId,
        ulong channelId,
        string panelId,
        int newOrder)
    {
        if (string.IsNullOrWhiteSpace(panelId))
            return (false, "Ungültige Panel-ID.");

        if (newOrder <= 0)
            return (false, "Ungültige Ziel-Reihenfolge.");

        var index = await ReadIndexAsync(guildId, channelId);
        var normalizedId = NormalizePanelId(panelId);
        var panel = index.Panels.FirstOrDefault(p => p.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));
        if (panel is null)
            return (false, $"Kein Panel mit der ID `{panelId}` gefunden.");

        var oldOrder = panel.Order;
        var maxOrder = index.Panels.Count == 0 ? 0 : index.Panels.Max(p => p.Order);

        // clamp newOrder
        if (newOrder > maxOrder)
            newOrder = maxOrder == 0 ? 1 : maxOrder;

        if (newOrder == oldOrder)
            return (true, "Panel bleibt an derselben Position.");

        if (newOrder < oldOrder)
        {
            // shift others down (increment) in range [newOrder, oldOrder-1]
            foreach (var p in index.Panels.Where(p => p.Id != panel.Id && p.Order >= newOrder && p.Order < oldOrder))
            {
                p.Order++;
            }
        }
        else // newOrder > oldOrder
        {
            // shift others up (decrement) in range [oldOrder+1, newOrder]
            foreach (var p in index.Panels.Where(p => p.Id != panel.Id && p.Order <= newOrder && p.Order > oldOrder))
            {
                p.Order--;
            }
        }

        panel.Order = newOrder;

        // normalize ordering to contiguous starting at 1
        var ordered = index.Panels.OrderBy(p => p.Order).ToList();
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Order = i + 1;

        await WriteIndexAsync(guildId, channelId, index);

        return (true, "Panel verschoben.");
    }

    public string GetGuildPath(ulong guildId)
        => Path.Combine(_contentRoot, guildId.ToString());

    public string GetChannelPath(ulong guildId, ulong channelId)
        => Path.Combine(GetGuildPath(guildId), channelId.ToString());

    public string GetIndexPath(ulong guildId, ulong channelId)
        => Path.Combine(GetChannelPath(guildId, channelId), "index.json");

    public string GetPanelPath(ulong guildId, ulong channelId, string panelId)
        => Path.Combine(GetChannelPath(guildId, channelId), $"{NormalizePanelId(panelId)}.json");

    public async Task EnsureChannelStructureAsync(ulong guildId, ulong channelId)
    {
        var channelPath = GetChannelPath(guildId, channelId);
        Directory.CreateDirectory(channelPath);

        var indexPath = GetIndexPath(guildId, channelId);
        if (!File.Exists(indexPath))
        {
            await WriteIndexAsync(guildId, channelId, new PanelIndexModel());
        }
    }

    public async Task<PanelIndexModel> ReadIndexAsync(ulong guildId, ulong channelId)
    {
        await EnsureChannelStructureAsync(guildId, channelId);

        var indexPath = GetIndexPath(guildId, channelId);
        var json = await File.ReadAllTextAsync(indexPath);

        return JsonSerializer.Deserialize<PanelIndexModel>(json) ?? new PanelIndexModel();
    }

    public async Task WriteIndexAsync(ulong guildId, ulong channelId, PanelIndexModel model)
    {
        var channelPath = GetChannelPath(guildId, channelId);
        Directory.CreateDirectory(channelPath);

        var indexPath = GetIndexPath(guildId, channelId);
        var json = JsonSerializer.Serialize(model, _jsonOptions);

        await File.WriteAllTextAsync(indexPath, json);
    }

    public async Task<PanelContentModel> ReadPanelAsync(ulong guildId, ulong channelId, string panelId)
    {
        var normalizedId = NormalizePanelId(panelId);
        var panelPath = GetPanelPath(guildId, channelId, normalizedId);

        if (!File.Exists(panelPath))
        {
            return new PanelContentModel();
        }

        var json = await File.ReadAllTextAsync(panelPath);
        return JsonSerializer.Deserialize<PanelContentModel>(json) ?? new PanelContentModel();
    }

    public async Task WritePanelAsync(ulong guildId, ulong channelId, string panelId, PanelContentModel model)
    {
        var normalizedId = NormalizePanelId(panelId);

        var channelPath = GetChannelPath(guildId, channelId);
        Directory.CreateDirectory(channelPath);

        var panelPath = GetPanelPath(guildId, channelId, normalizedId);
        var json = JsonSerializer.Serialize(model, _jsonOptions);

        await File.WriteAllTextAsync(panelPath, json);
    }

    public async Task<bool> PanelExistsAsync(ulong guildId, ulong channelId, string panelId)
    {
        var normalizedId = NormalizePanelId(panelId);
        var panelPath = GetPanelPath(guildId, channelId, normalizedId);

        return await Task.FromResult(File.Exists(panelPath));
    }

    public async Task<(bool Success, string Message, string? PanelId)> CreatePanelAsync(
        ulong guildId,
        ulong channelId,
        string title,
        string? requestedId = null,
        int? order = null)
    {
        // enforce reasonable title length limit
        title = (title ?? string.Empty).Trim();
        if (title.Length > 128)
            title = title.Substring(0, 128);

        var finalId = string.IsNullOrWhiteSpace(requestedId)
            ? NormalizePanelId(title)
            : NormalizePanelId(requestedId);

        if (string.IsNullOrWhiteSpace(finalId))
            return (false, "Es konnte keine gültige Panel-ID erzeugt werden.", null);

        await EnsureChannelStructureAsync(guildId, channelId);

        var index = await ReadIndexAsync(guildId, channelId);

        if (index.Panels.Any(p => p.Id.Equals(finalId, StringComparison.OrdinalIgnoreCase)))
            return (false, $"Ein Panel mit der ID `{finalId}` existiert bereits.", null);

        int finalOrder;

        if (order.HasValue && order.Value > 0)
        {
            // clamp requested order to a sensible maximum (at most count+1)
            var maxAllowed = index.Panels.Count + 1;
            finalOrder = order.Value > maxAllowed ? maxAllowed : order.Value;

            // shift existing panels with order >= finalOrder
            foreach (var p in index.Panels.Where(p => p.Order >= finalOrder))
            {
                p.Order++;
            }
        }
        else
        {
            finalOrder = index.Panels.Count == 0
                ? 1
                : index.Panels.Max(p => p.Order) + 1;
        }

        index.Panels.Add(new PanelIndexEntryModel
        {
            Id = finalId,
            Title = title,
            Order = finalOrder,
            Visible = true
        });

        await WriteIndexAsync(guildId, channelId, index);

        var content = new PanelContentModel
        {
            Body = new List<string> { "Neuer Eintrag" }
        };

        await WritePanelAsync(guildId, channelId, finalId, content);

        return (true, $"Panel `{title}` wurde mit der ID `{finalId}` erstellt.", finalId);
    }

    public async Task<(bool Success, string Message)> AddLineAsync(
        ulong guildId,
        ulong channelId,
        string panelId,
        string text)
    {
        var normalizedId = NormalizePanelId(panelId);

        if (string.IsNullOrWhiteSpace(normalizedId))
            return (false, "Ungültige Panel-ID.");

        var index = await ReadIndexAsync(guildId, channelId);
        var panelEntry = index.Panels.FirstOrDefault(p =>
            p.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));

        if (panelEntry is null)
            return (false, $"Kein Panel mit der ID `{normalizedId}` gefunden.");

        var content = await ReadPanelAsync(guildId, channelId, normalizedId);
        content.Body.Add(text);

        await WritePanelAsync(guildId, channelId, normalizedId, content);

        return (true, $"Zeile zu `{panelEntry.Title}` hinzugefügt.");
    }

    public async Task<(bool Success, string Message)> SetBodyAsync(
        ulong guildId,
        ulong channelId,
        string panelId,
        IEnumerable<string> lines)
    {
        var normalizedId = NormalizePanelId(panelId);

        if (string.IsNullOrWhiteSpace(normalizedId))
            return (false, "Ungültige Panel-ID.");

        var index = await ReadIndexAsync(guildId, channelId);
        var panelEntry = index.Panels.FirstOrDefault(p =>
            p.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));

        if (panelEntry is null)
            return (false, $"Kein Panel mit der ID `{normalizedId}` gefunden.");

        var content = new PanelContentModel
        {
            Body = lines
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList()
        };

        await WritePanelAsync(guildId, channelId, normalizedId, content);

        return (true, $"Inhalt von `{panelEntry.Title}` wurde aktualisiert.");
    }

    public async Task<(bool Success, string Message)> DeletePanelAsync(
        ulong guildId,
        ulong channelId,
        string panelId)
    {
        var normalizedId = NormalizePanelId(panelId);

        if (string.IsNullOrWhiteSpace(normalizedId))
            return (false, "Ungültige Panel-ID.");

        var index = await ReadIndexAsync(guildId, channelId);
        var removed = index.Panels.RemoveAll(p =>
            p.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
            return (false, $"Kein Panel mit der ID `{normalizedId}` gefunden.");

        await WriteIndexAsync(guildId, channelId, index);

        var panelPath = GetPanelPath(guildId, channelId, normalizedId);
        if (File.Exists(panelPath))
        {
            File.Delete(panelPath);
        }

        return (true, $"Panel `{normalizedId}` wurde gelöscht.");
    }

    public string NormalizePanelId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var parts = SplitIntoWords(value);
        if (parts.Count == 0)
            return string.Empty;

        var first = parts[0].ToLowerInvariant();
        var rest = parts.Skip(1).Select(ToPascalCasePart);

        return first + string.Concat(rest);
    }

    private static List<string> SplitIntoWords(string input)
    {
        var cleaned = new StringBuilder();

        foreach (var ch in input.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                cleaned.Append(ch);
            }
            else
            {
                cleaned.Append(' ');
            }
        }

        return cleaned.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static string ToPascalCasePart(string value)
    {
        var lower = value.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(lower))
            return string.Empty;

        if (lower.Length == 1)
            return lower.ToUpperInvariant();

        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}