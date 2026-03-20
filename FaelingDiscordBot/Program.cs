using Discord;
using Discord.WebSocket;
using FaelingDiscordBot.Models;
using FaelingDiscordBot.Services;

var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")
    ?? throw new Exception("DISCORD_BOT_TOKEN fehlt.");

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds
});

var panelStorage = new PanelStorageService();
var commandsRegistered = false;

client.Log += msg =>
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} | {msg}");
    return Task.CompletedTask;
};

client.Ready += async () =>
{
    Console.WriteLine($"READY als {client.CurrentUser}");

    if (commandsRegistered)
        return;

    commandsRegistered = true;

    foreach (var guild in client.Guilds)
    {
        try
        {
            var slashCommand = new SlashCommandBuilder()
                .WithName("panel")
                .WithDescription("Verwaltet Panels")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("action")
                    .WithDescription("Aktion")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)
                    .AddChoice("create", "create")
                    .AddChoice("show", "show")
                    .AddChoice("list", "list"))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithDescription("Anzeigename des Panels")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("id")
                    .WithDescription("Optionale technische ID des Panels")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false))
                .Build();

            var existingCommands = await guild.GetApplicationCommandsAsync();
            var existingPanel = existingCommands.FirstOrDefault(x => x.Name == "panel");

            if (existingPanel != null)
            {
                await existingPanel.DeleteAsync();
                Console.WriteLine($"/panel gelöscht für Guild {guild.Name}");
            }



            await guild.CreateApplicationCommandAsync(slashCommand);
            Console.WriteLine($"/panel neu registriert für Guild {guild.Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler bei Command-Registrierung für Guild {guild.Name}:");
            Console.WriteLine(ex);
        }
    }
};

client.InteractionCreated += async interaction =>
{
    try
    {
        switch (interaction)
        {
            case SocketSlashCommand cmd when cmd.Data.Name == "panel":
                await HandlePanelCommandAsync(cmd, panelStorage);
                break;

            case SocketMessageComponent comp:
                await HandlePanelButtonAsync(comp, panelStorage);
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Fehler in InteractionCreated:");
        Console.WriteLine(ex);
    }
};

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();
await Task.Delay(Timeout.Infinite);

static async Task HandlePanelCommandAsync(SocketSlashCommand cmd, PanelStorageService storage)
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

    if (string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            await cmd.RespondAsync("Bitte gib einen Namen an.", ephemeral: true);
            return;
        }

        var result = await storage.CreatePanelAsync(guildId, channelId, name, requestedId);
        await cmd.RespondAsync(result.Message, ephemeral: true);
        return;
    }

    if (string.Equals(action, "show", StringComparison.OrdinalIgnoreCase))
    {
        var (content, components) = await BuildPanelMessageAsync(storage, guildId, channelId);
        await cmd.RespondAsync(content, components: components);
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
            .Select(p => $"- `{p.Id} - {p.Title}`"));

        await cmd.RespondAsync(string.Join("\n", lines), ephemeral: true);
        return;
    }
}

static async Task HandlePanelButtonAsync(SocketMessageComponent comp, PanelStorageService storage)
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

    var panelId = parts[3];
    var action = parts[4];

    string? openPanelId = action == "open" ? panelId : null;

    var (content, components) = await BuildPanelMessageAsync(storage, guildId, channelId, openPanelId);

    await comp.UpdateAsync(msg =>
    {
        msg.Content = content;
        msg.Components = components;
    });
}

static async Task<(string Content, MessageComponent Components)> BuildPanelMessageAsync(
    PanelStorageService storage,
    ulong guildId,
    ulong channelId,
    string? openPanelId = null,
    bool includeHidden = false)
{
    var index = await storage.ReadIndexAsync(guildId, channelId);

    var visiblePanels = index.Panels
        .Where(p => includeHidden || p.Visible)
        .OrderBy(p => p.Order)
        .ToList();

    var lines = new List<string>
    {
        "**Panel-Übersicht**",
        ""
    };

    var components = new ComponentBuilder();

    foreach (var panel in visiblePanels)
    {
        bool isOpen = string.Equals(panel.Id, openPanelId, StringComparison.OrdinalIgnoreCase);

        lines.Add(isOpen
            ? $"**▼ {panel.Title}**"
            : $"**▶ {panel.Title}**");

        if (isOpen)
        {
            var content = await storage.ReadPanelAsync(guildId, channelId, panel.Id);

            if (content.Body.Count == 0)
            {
                lines.Add("- _Kein Inhalt vorhanden_");
            }
            else
            {
                lines.AddRange(content.Body.Select(x => $"- {x}"));
            }

            lines.Add("");
        }

        components.WithButton(
            label: $"{panel.Title} - {(isOpen ? "close" : "open")}",
            customId: $"panel:{guildId}:{channelId}:{panel.Id}:{(isOpen ? "close" : "open")}",
            style: isOpen ? ButtonStyle.Secondary : ButtonStyle.Primary);
    }

    return (string.Join("\n", lines), components.Build());
}