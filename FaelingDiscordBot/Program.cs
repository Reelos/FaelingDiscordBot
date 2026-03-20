using Discord;
using Discord.WebSocket;

var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")
    ?? throw new Exception("DISCORD_BOT_TOKEN fehlt.");

var guildIdRaw = Environment.GetEnvironmentVariable("DISCORD_GUILD_ID")
    ?? throw new Exception("DISCORD_GUILD_ID fehlt.");

if (!ulong.TryParse(guildIdRaw, out var guildId))
    throw new Exception("DISCORD_GUILD_ID ist ungültig.");

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds
});

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

    var guild = client.GetGuild(guildId);
    if (guild == null)
    {
        Console.WriteLine("Guild nicht gefunden. Ist der Bot auf dem Server?");
        return;
    }

    try
    {
        var commands = await guild.GetApplicationCommandsAsync();
        if (!commands.Any(x => x.Name == "panel"))
        {
            var slashCommand = new SlashCommandBuilder()
                .WithName("panel")
                .WithDescription("Test Panel")
                .Build();

            await guild.CreateApplicationCommandAsync(slashCommand);
            Console.WriteLine("/panel registriert");
        }
        else
        {
            Console.WriteLine("/panel existiert bereits");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Fehler bei Command-Registrierung:");
        Console.WriteLine(ex);
    }
};

client.InteractionCreated += async interaction =>
{
    try
    {
        switch (interaction)
        {
            case SocketSlashCommand cmd when cmd.Data.Name == "panel":
                {
                    var components = new ComponentBuilder()
                        .WithButton("Mehr anzeigen", "toggle_closed", ButtonStyle.Primary)
                        .Build();

                    await cmd.RespondAsync(
                        text: "**Panel**\nKlick den Button",
                        components: components);

                    break;
                }

            case SocketMessageComponent comp when comp.Data.CustomId == "toggle_closed":
                {
                    var components = new ComponentBuilder()
                        .WithButton("Weniger anzeigen", "toggle_open", ButtonStyle.Secondary)
                        .Build();

                    await comp.UpdateAsync(msg =>
                    {
                        msg.Content = "**Panel**\nHier ist mehr Inhalt\n- Punkt 1\n- Punkt 2";
                        msg.Components = components;
                    });

                    break;
                }

            case SocketMessageComponent comp when comp.Data.CustomId == "toggle_open":
                {
                    var components = new ComponentBuilder()
                        .WithButton("Mehr anzeigen", "toggle_closed", ButtonStyle.Primary)
                        .Build();

                    await comp.UpdateAsync(msg =>
                    {
                        msg.Content = "**Panel**\nKlick den Button";
                        msg.Components = components;
                    });

                    break;
                }
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