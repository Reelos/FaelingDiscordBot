using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using FaelingDiscordBot.Models;
using FaelingDiscordBot.Services;

namespace FaelingDiscordBot.Controllers;

public static class PanelController
{
    private static string GetPanelToken(string panelId)
    {
        using var sha = SHA1.Create();
        var bytes = Encoding.UTF8.GetBytes(panelId);
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
    }

    private static PanelIndexEntryModel? ResolvePanelByToken(PanelIndexModel index, string token)
    {
        return index.Panels.FirstOrDefault(p => GetPanelToken(p.Id) == token);
    }
    public static async Task HandlePanelCommandAsync(SocketSlashCommand cmd, PanelStorageService storage, DiscordSocketClient client)
    {
        if (cmd.GuildId is not ulong guildId || cmd.ChannelId is not ulong channelId)
        {
            await cmd.RespondAsync(
                "Dieser Command kann nur in einem Server-Channel verwendet werden.",
                ephemeral: true);
            return;
        }

        var action = cmd.Data.Options.FirstOrDefault(x => x.Name == "action")?.Value?.ToString();
        var name = cmd.Data.Options.FirstOrDefault(x => x.Name == "name")?.Value?.ToString();
        var requestedId = cmd.Data.Options.FirstOrDefault(x => x.Name == "id")?.Value?.ToString();
        var orderOpt = cmd.Data.Options.FirstOrDefault(x => x.Name == "order")?.Value;
        int? order = null;
        if (orderOpt != null && int.TryParse(orderOpt.ToString(), out var o))
            order = o;

        if (string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
        {

            // Open modal to collect title, optional id and order, and body
            var modalId = $"panel-modal:create:{guildId}:{channelId}";
            var initial = ""; // leave empty for new panel

            var modal = new ModalBuilder()
                .WithCustomId(modalId)
                .WithTitle("Create Panel")
                .AddTextInput("title", "Titel", TextInputStyle.Short, required: true, value: name)
                .AddTextInput("order", "Order (1 = oben)", TextInputStyle.Short, required: false, value: order?.ToString())
                .AddTextInput("id", "Optionale ID", TextInputStyle.Short, required: false, value: requestedId)
                .AddTextInput("body", "Inhalt (Zeilen)", TextInputStyle.Paragraph, required: false, value: initial, maxLength: 4000)
                .Build();

            try
            {
                await cmd.RespondWithModalAsync(modal);
            }
            catch
            {
                var fallbackTitle = "Create Panel";
                var fallback = new ModalBuilder()
                    .WithCustomId(modalId)
                    .WithTitle(fallbackTitle)
                    .AddTextInput("title", "Titel", TextInputStyle.Short, required: true, value: name)
                    .AddTextInput("order", "Order (1 = oben)", TextInputStyle.Short, required: false, value: order?.ToString())
                    .AddTextInput("body", "Inhalt (Zeilen)", TextInputStyle.Paragraph, required: false, value: initial)
                    .Build();

                try
                {
                    await cmd.RespondWithModalAsync(fallback);
                }
                catch
                {
                    await cmd.RespondAsync("Fehler beim Öffnen des Edit-Modals.", ephemeral: true);
                }
            }
            return;
        }

        if (string.Equals(action, "edit", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(requestedId))
            {
                await cmd.RespondAsync("Bitte gib die Panel-ID an (Option `id`).", ephemeral: true);
                return;
            }

            var index = await storage.ReadIndexAsync(guildId, channelId);
            var normalized = storage.NormalizePanelId(requestedId);
            var panel = index.Panels.FirstOrDefault(p => p.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (panel is null)
            {
                await cmd.RespondAsync($"Kein Panel mit der ID `{requestedId}` gefunden.", ephemeral: true);
                return;
            }

            // Allow updating title and order via slash edit
            var newTitle = name;
            var orderOpt2 = cmd.Data.Options.FirstOrDefault(x => x.Name == "order")?.Value;
            int? newOrder = null;
            if (orderOpt2 != null && int.TryParse(orderOpt2.ToString(), out var no))
                newOrder = no;

            if (!string.IsNullOrWhiteSpace(newTitle) || newOrder.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(newTitle))
                {
                    var nt = newTitle.Trim();
                    if (nt.Length > 128) nt = nt.Substring(0, 128);
                    panel.Title = nt;
                }

                var moved = false;
                if (newOrder.HasValue && newOrder.Value > 0 && newOrder.Value != panel.Order)
                {
                    var moveResult = await storage.MovePanelAsync(guildId, channelId, panel.Id, newOrder.Value);
                    if (!moveResult.Success)
                    {
                        await cmd.RespondAsync(moveResult.Message, ephemeral: true);
                        return;
                    }

                    moved = true;
                }

                await storage.WriteIndexAsync(guildId, channelId, index);

                // If the order changed, re-render all panel messages so their ordering in-channel is correct
                if (moved)
                {
                    await RenderAllPanelMessagesAsync(storage, client, guildId, channelId);
                }
            }

            var contentModel = await storage.ReadPanelAsync(guildId, channelId, panel.Id);
            var initial = string.Join("\n", contentModel.Body);
            var modalPanelToken = GetPanelToken(panel.Id);
            var modalId = $"panel-modal:edit:{guildId}:{channelId}:{modalPanelToken}";

            var modalTitleBtn = $"Edit Panel {panel.Title}";
            if (modalTitleBtn.Length > 45)
                modalTitleBtn = modalTitleBtn.Substring(0, 42) + "...";

            var modal = new ModalBuilder()
                .WithCustomId(modalId)
                .WithTitle(modalTitleBtn)
                .AddTextInput("title", "Titel", TextInputStyle.Short, required: true, value: panel.Title)
                .AddTextInput("order", "Order (1 = oben)", TextInputStyle.Short, required: false, value: panel.Order.ToString())
                .AddTextInput("body", "Inhalt (Zeilen)", TextInputStyle.Paragraph, required: false, value: initial, maxLength: 4000)
                .Build();

            await cmd.RespondWithModalAsync(modal);
            return;
        }

        if (string.Equals(action, "show", StringComparison.OrdinalIgnoreCase))
        {
            var index = await storage.ReadIndexAsync(guildId, channelId);

            var channel = cmd.Channel as IMessageChannel;
            if (channel is null)
            {
                await cmd.RespondAsync("Channel nicht verfügbar.", ephemeral: true);
                return;
            }

            // For each visible panel create or update its own message and persist MessageId
            foreach (var panel in index.Panels.Where(p => p.Visible).OrderBy(p => p.Order))
            {
                var (content, components) = await BuildSinglePanelMessageAsync(storage, guildId, channelId, panel, isOpen: false);

                if (panel.MessageId.HasValue)
                {
                    var existing = await channel.GetMessageAsync(panel.MessageId.Value) as IUserMessage;
                    if (existing != null)
                    {
                        await existing.ModifyAsync(props =>
                        {
                            props.Content = content;
                            props.Components = components;
                        });
                        continue;
                    }
                }

                var sent = await channel.SendMessageAsync(content, components: components);
                panel.MessageId = sent.Id;
            }

            await storage.WriteIndexAsync(guildId, channelId, index);
            await cmd.RespondAsync("Panel-Nachrichten erstellt/aktualisiert.", ephemeral: true);
            return;
        }

        if (string.Equals(action, "hide", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(requestedId))
            {
                await cmd.RespondAsync("Bitte gib die Panel-ID an (Option `id`).", ephemeral: true);
                return;
            }

            var index = await storage.ReadIndexAsync(guildId, channelId);
            var normalized = storage.NormalizePanelId(requestedId);
            var panel = index.Panels.FirstOrDefault(p => p.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (panel is null)
            {
                await cmd.RespondAsync($"Kein Panel mit der ID `{requestedId}` gefunden.", ephemeral: true);
                return;
            }

            var channel = cmd.Channel as IMessageChannel;
            if (channel is null)
            {
                await cmd.RespondAsync("Channel nicht verfügbar.", ephemeral: true);
                return;
            }

            if (panel.MessageId.HasValue)
            {
                var existing = await channel.GetMessageAsync(panel.MessageId.Value) as IUserMessage;
                if (existing != null)
                    await existing.DeleteAsync();

                panel.MessageId = null;
                await storage.WriteIndexAsync(guildId, channelId, index);
            }

            await cmd.RespondAsync($"Panel `{panel.Id}`: Nachricht gelöscht und MessageId entfernt.", ephemeral: true);
            return;
        }

        if (string.Equals(action, "move", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(requestedId) || !order.HasValue)
            {
                await cmd.RespondAsync("Bitte gib Panel-ID (Option `id`) und Ziel-Order (Option `order`) an.", ephemeral: true);
                return;
            }

            var moveResult = await storage.MovePanelAsync(guildId, channelId, requestedId, order.Value);
            if (!moveResult.Success)
            {
                await cmd.RespondAsync(moveResult.Message, ephemeral: true);
                return;
            }

            // re-render all panel messages so title/buttons reflect new order
            await RenderAllPanelMessagesAsync(storage, client, guildId, channelId);

            await cmd.RespondAsync(moveResult.Message, ephemeral: true);
            return;
        }

        if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
        {
            var index = await storage.ReadIndexAsync(guildId, channelId);

            if (index.Panels.Count == 0)
            {
                await cmd.RespondAsync("Keine Panels vorhanden.", ephemeral: true);
                return;
            }

            var lines = new List<string>
            {
                "**Panel-Übersicht**",
                ""
            };

            lines.AddRange(index.Panels
                .OrderBy(p => p.Order)
                .Select(p => $"- `{p.Id}`"));

            await cmd.RespondAsync(string.Join("\n", lines), ephemeral: true);
            return;
        }
    }

    public static async Task HandlePanelButtonAsync(SocketMessageComponent comp, PanelStorageService storage, DiscordSocketClient client)
    {
        if (!comp.Data.CustomId.StartsWith("panel:"))
            return;

        var parts = comp.Data.CustomId.Split(':');
        if (parts.Length != 5)
        {
            await comp.RespondAsync("Ungültige Panel-ID.", ephemeral: true);
            return;
        }

        if (!ulong.TryParse(parts[1], out var guildId) ||
            !ulong.TryParse(parts[2], out var channelId))
        {
            await comp.RespondAsync("Ungültiger Panel-Kontext.", ephemeral: true);
            return;
        }

            var panelTokenStr = parts[3];
        var action = parts[4];

        var index = await storage.ReadIndexAsync(guildId, channelId);
        var panel = index.Panels.FirstOrDefault(p => GetPanelToken(p.Id) == panelTokenStr);
        if (panel is null)
        {
            await comp.RespondAsync("Panel nicht gefunden.", ephemeral: true);
            return;
        }

        var channel = comp.Channel as IMessageChannel;
        if (channel is null)
        {
            await comp.RespondAsync("Channel nicht verfügbar.", ephemeral: true);
            return;
        }

        var isOpen = action == "open";

        // Support edit action via button: panel-edit
        if (action == "edit")
        {
            // open modal with current content
            var contentModel = await storage.ReadPanelAsync(guildId, channelId, panel.Id);
            var initial = string.Join("\n", contentModel.Body);
            var modalPanelToken = GetPanelToken(panel.Id);
            var modalId = $"panel-modal:edit:{guildId}:{channelId}:{modalPanelToken}";

            var modalTitleBtn = $"Edit Panel {panel.Title}";
            if (modalTitleBtn.Length > 45)
                modalTitleBtn = modalTitleBtn.Substring(0, 42) + "...";

            var modal = new ModalBuilder()
                .WithCustomId(modalId)
                .WithTitle(modalTitleBtn)
                .AddTextInput("title", "Titel", TextInputStyle.Short, required: true, value: panel.Title)
                .AddTextInput("order", "Order (1 = oben)", TextInputStyle.Short, required: false, value: panel.Order.ToString())
                .AddTextInput("body", "Inhalt (Zeilen)", TextInputStyle.Paragraph, required: false, value: initial, maxLength: 4000)
                .Build();

            try
            {
                await comp.RespondWithModalAsync(modal);
            }
            catch
            {
                // Fallback: open a minimal modal with a short title to avoid Discord length errors
                try
                {
                    var fallback = new ModalBuilder()
                        .WithCustomId(modalId)
                        .WithTitle("Edit Panel")
                        .AddTextInput("title", "Titel", TextInputStyle.Short, required: true, value: panel.Title)
                        .AddTextInput("order", "Order (1 = oben)", TextInputStyle.Short, required: false, value: panel.Order.ToString())
                        .AddTextInput("body", "Inhalt (Zeilen)", TextInputStyle.Paragraph, required: false, value: initial)
                        .Build();

                    await comp.RespondWithModalAsync(fallback);
                }
                catch
                {
                    await comp.RespondAsync("Fehler beim Öffnen des Edit-Modals.", ephemeral: true);
                }
            }
            return;
        }

        var (content, components) = await BuildSinglePanelMessageAsync(storage, guildId, channelId, panel, isOpen: isOpen);

        if (panel.MessageId.HasValue)
        {
            var existing = await channel.GetMessageAsync(panel.MessageId.Value) as IUserMessage;
            if (existing != null)
            {
                await existing.ModifyAsync(props =>
                {
                    props.Content = content;
                    props.Components = components;
                });

                // Ack the interaction without changing the invoking message
                await comp.DeferAsync();
                return;
            }
        }

        // If no message exists, create one and persist
        var sent = await channel.SendMessageAsync(content, components: components);
        panel.MessageId = sent.Id;
        await storage.WriteIndexAsync(guildId, channelId, index);

        await comp.DeferAsync();
    }

    public static async Task HandleModalSubmitAsync(SocketModal modal, PanelStorageService storage, DiscordSocketClient client)
    {
        var parts = modal.Data.CustomId.Split(':');
        if (parts.Length < 4)
        {
            await modal.RespondAsync("Ungültige Modal-ID.", ephemeral: true);
            return;
        }

        // no debug output

        var action = parts[1];
        if (!ulong.TryParse(parts[2], out var guildId) || !ulong.TryParse(parts[3], out var channelId))
        {
            await modal.RespondAsync("Ungültiger Kontext.", ephemeral: true);
            return;
        }

        // collect inputs: modal.Data.Components -> rows -> components
        // Extract modal inputs using dynamic access for runtime component shapes
        var inputs = new Dictionary<string, string?>();
        foreach (var row in modal.Data.Components)
        {
            try
            {
                dynamic dynRow = row;
                foreach (var comp in dynRow.Components)
                {
                    try
                    {
                        string key = comp.CustomId;
                        string val = comp.Value;
                        if (!string.IsNullOrEmpty(key))
                            inputs[key] = val;
                    }
                    catch
                    {
                        // ignore component
                    }
                }
            }
            catch
            {
                try
                {
                    dynamic dynRow2 = row;
                    string key = dynRow2.CustomId;
                    string val = dynRow2.Value;
                    if (!string.IsNullOrEmpty(key))
                        inputs[key] = val;
                }
                catch
                {
                    // ignore
                }
            }
        }

        // Helper to find input by several possible keys (custom id or label)
        static string? FindInput(Dictionary<string, string?> inputs, params string[] keys)
        {
            string Normalize(string s) => new string(s?.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

            var normalizedMap = inputs.ToDictionary(kv => Normalize(kv.Key ?? string.Empty), kv => kv.Value);

            foreach (var k in keys)
            {
                var nk = Normalize(k);
                if (normalizedMap.TryGetValue(nk, out var v))
                    return v;
            }

            // fallback: try substring match
            foreach (var k in keys)
            {
                var nk = Normalize(k);
                var match = normalizedMap.FirstOrDefault(kv => kv.Key.Contains(nk));
                if (!string.IsNullOrEmpty(match.Key))
                    return match.Value;
            }

            return null;
        }

        var titleVal = FindInput(inputs, "title", "titel");
        var orderVal = FindInput(inputs, "order", "order (1 = oben)", "order1");
        var idVal = FindInput(inputs, "id", "optionale id");
        var bodyVal = FindInput(inputs, "body", "inhalt (zeilen)", "inhalt", "content");

        int? newOrder = null;
        if (!string.IsNullOrWhiteSpace(orderVal) && int.TryParse(orderVal, out var no))
            newOrder = no;

        // inputs extracted
        var lines = (bodyVal ?? string.Empty).Split('\n').Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        if (action == "edit")
        {
            if (parts.Length != 5)
            {
                await modal.RespondAsync("Ungültige Modal-ID (edit).", ephemeral: true);
                return;
            }

            var panelToken = parts[4];

            var index = await storage.ReadIndexAsync(guildId, channelId);
            var panel = ResolvePanelByToken(index, panelToken);
            if (panel is null)
            {
                await modal.RespondAsync("Panel nicht gefunden.", ephemeral: true);
                return;
            }

            // remember original id to re-resolve after moves
            var originalId = panel.Id;

            var changed = false;
            if (!string.IsNullOrWhiteSpace(titleVal) && panel.Title != titleVal)
            {
                var newTitleTrim = titleVal.Trim();
                if (newTitleTrim.Length > 128)
                    newTitleTrim = newTitleTrim.Substring(0, 128);

                panel.Title = newTitleTrim;
                changed = true;
            }

            if (newOrder.HasValue && newOrder.Value > 0 && newOrder.Value != panel.Order)
            {
                var moveResult = await storage.MovePanelAsync(guildId, channelId, panel.Id, newOrder.Value);
                if (!moveResult.Success)
                {
                    await modal.RespondAsync(moveResult.Message, ephemeral: true);
                    return;
                }

                // reload index/panel after move
                index = await storage.ReadIndexAsync(guildId, channelId);
                panel = index.Panels.FirstOrDefault(p => p.Id.Equals(originalId, StringComparison.OrdinalIgnoreCase));
                changed = true;
            }

            if (changed)
            {
                await storage.WriteIndexAsync(guildId, channelId, index);
            }

            // Ensure panelEntry is the latest from index (in case Move changed ordering)
            index = await storage.ReadIndexAsync(guildId, channelId);
            panel = index.Panels.FirstOrDefault(p => p.Id.Equals(originalId, StringComparison.OrdinalIgnoreCase))!;

            var setResult = await storage.SetBodyAsync(guildId, channelId, panel.Id, lines);
            if (!setResult.Success)
            {
                Console.WriteLine($"[PanelController] Failed to set body for {panel.Id}: {setResult.Message}");
                await modal.RespondAsync($"Fehler beim Speichern des Inhalts: {setResult.Message}", ephemeral: true);
                return;
            }

            // Update message if exists — preserve open/closed state
            if (panel.MessageId.HasValue)
            {
                var channel = client.GetChannel(channelId) as IMessageChannel;
                if (channel != null)
                {
                    var existing = await channel.GetMessageAsync(panel.MessageId.Value) as IUserMessage;
                    if (existing != null)
                    {
                        var currentlyOpen = existing.Content != null && existing.Content.Contains("▼");
                        var (newContent, newComponents) = await BuildSinglePanelMessageAsync(storage, guildId, channelId, panel, isOpen: currentlyOpen);
                        await existing.ModifyAsync(props =>
                        {
                            props.Content = newContent;
                            props.Components = newComponents;
                        });
                    }
                }
            }

            await modal.RespondAsync("Panel aktualisiert.", ephemeral: true);
            return;
        }

        if (action == "create")
        {
            // title required
            var title = titleVal ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                await modal.RespondAsync("Titel darf nicht leer sein.", ephemeral: true);
                return;
            }

            var requested = string.IsNullOrWhiteSpace(idVal) ? null : idVal;
            var createResult = await storage.CreatePanelAsync(guildId, channelId, title, requested, newOrder);
            if (!createResult.Success || createResult.PanelId is null)
            {
                await modal.RespondAsync(createResult.Message, ephemeral: true);
                return;
            }

            var panelId = createResult.PanelId;
            var setResult2 = await storage.SetBodyAsync(guildId, channelId, panelId, lines);
            if (!setResult2.Success)
            {
                Console.WriteLine($"[PanelController] Failed to set body for {panelId}: {setResult2.Message}");
                await modal.RespondAsync($"Fehler beim Speichern des Inhalts: {setResult2.Message}", ephemeral: true);
                return;
            }

            // create message immediately
            var index = await storage.ReadIndexAsync(guildId, channelId);
            var panel = index.Panels.FirstOrDefault(p => p.Id == panelId);
            if (panel != null)
            {
                var channel = client.GetChannel(channelId) as IMessageChannel;
                if (channel != null)
                {
                    var (content, components) = await BuildSinglePanelMessageAsync(storage, guildId, channelId, panel, isOpen: false);
                    var sent = await channel.SendMessageAsync(content, components: components);
                    panel.MessageId = sent.Id;
                    await storage.WriteIndexAsync(guildId, channelId, index);
                }
            }

            await modal.RespondAsync("Panel erstellt.", ephemeral: true);
            return;
        }

        await modal.RespondAsync("Unbekannte Modal-Aktion.", ephemeral: true);
    }

    private static async Task<(string Content, MessageComponent Components)> BuildSinglePanelMessageAsync(
        PanelStorageService storage,
        ulong guildId,
        ulong channelId,
        PanelIndexEntryModel panel,
        bool isOpen)
    {
        var lines = new List<string>
        {
            isOpen ? $"**▼ {panel.Title}**" : $"**▶ {panel.Title}**",
            ""
        };

        if (isOpen)
        {
            var content = await storage.ReadPanelAsync(guildId, channelId, panel.Id);

            if (content.Body.Count == 0)
            {
                lines.Add("_Kein Inhalt vorhanden_");
            }
            else
            {
                lines.AddRange(content.Body.Select(x => $"{x}"));
            }

            lines.Add("");
        }

        var token = GetPanelToken(panel.Id);
        var components = new ComponentBuilder()
            .WithButton(
                label: $"{panel.Title} - {(isOpen ? "close" : "open")}",
                customId: $"panel:{guildId}:{channelId}:{token}:{(isOpen ? "close" : "open")}",
                style: isOpen ? ButtonStyle.Secondary : ButtonStyle.Primary)
            .WithButton(
                label: "Edit",
                customId: $"panel:{guildId}:{channelId}:{token}:edit",
                style: ButtonStyle.Secondary)
            .Build();

        return (string.Join("\n", lines), components);
    }

    // Re-render all visible panels in the channel so their ordering and messages match the index.
    private static async Task RenderAllPanelMessagesAsync(PanelStorageService storage, DiscordSocketClient client, ulong guildId, ulong channelId)
    {
        var index = await storage.ReadIndexAsync(guildId, channelId);
        var channel = client.GetChannel(channelId) as IMessageChannel;
        if (channel is null)
            return;

        // Determine open/closed state for existing messages, and delete existing messages so we can recreate them in correct order.
        var openState = new Dictionary<string, bool>();

        foreach (var panel in index.Panels)
        {
            if (!panel.Visible)
            {
                // delete any lingering message for hidden panels
                if (panel.MessageId.HasValue)
                {
                    try
                    {
                        var existing = await channel.GetMessageAsync(panel.MessageId.Value) as IUserMessage;
                        if (existing != null)
                            await existing.DeleteAsync();
                    }
                    catch
                    {
                        // ignore
                    }

                    panel.MessageId = null;
                }

                continue;
            }

            if (panel.MessageId.HasValue)
            {
                try
                {
                    var existing = await channel.GetMessageAsync(panel.MessageId.Value) as IUserMessage;
                    if (existing != null)
                    {
                        var isOpen = existing.Content != null && existing.Content.Contains("▼");
                        openState[panel.Id] = isOpen;
                        // delete message so we can recreate in order
                        await existing.DeleteAsync();
                    }
                }
                catch
                {
                    // ignore fetch/delete errors
                }

                panel.MessageId = null;
            }
        }

        // Create messages in the desired order and persist their ids
        foreach (var panel in index.Panels.Where(p => p.Visible).OrderBy(p => p.Order))
        {
            var isOpen = openState.TryGetValue(panel.Id, out var v) && v;
            var (content, components) = await BuildSinglePanelMessageAsync(storage, guildId, channelId, panel, isOpen: isOpen);
            try
            {
                var sent = await channel.SendMessageAsync(content, components: components);
                panel.MessageId = sent.Id;
            }
            catch
            {
                // ignore send errors; leave MessageId null
                panel.MessageId = null;
            }
        }

        await storage.WriteIndexAsync(guildId, channelId, index);
    }
}
