using Discord;
using Discord.WebSocket;
using FaelingDiscordBot.Controllers;
using FaelingDiscordBot.Services;

namespace FaelingDiscordBot;

public class Program
{
    private readonly DiscordSocketClient _client;
    private readonly PanelStorageService _panelStorage;
    private bool _commandsRegistered;

    public Program()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds });
        _panelStorage = new PanelStorageService();
        _commandsRegistered = false;

        _client.Log += msg =>
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} | {msg}");
            return Task.CompletedTask;
        };

        _client.Ready += OnReadyAsync;
        _client.JoinedGuild += OnJoinedGuildAsync;
        _client.InteractionCreated += OnInteractionCreatedAsync;
    }

    public static async Task Main(string[] args)
    {
        var program = new Program();
        await program.RunAsync();
    }

    public async Task RunAsync()
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")
            ?? throw new Exception("DISCORD_BOT_TOKEN fehlt.");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
        await Task.Delay(Timeout.Infinite);
    }

    private async Task OnReadyAsync()
    {
        Console.WriteLine($"READY als {_client.CurrentUser}");

        if (_commandsRegistered)
            return;

        _commandsRegistered = true;
        foreach (var guild in _client.Guilds)
        {
            await RegisterPanelCommandForGuildAsync(guild);
        }
    }

    private async Task OnJoinedGuildAsync(SocketGuild guild)
    {
        try
        {
            await RegisterPanelCommandForGuildAsync(guild);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler bei Command-Registrierung für beigetretene Guild {guild.Name}:");
            Console.WriteLine(ex);
        }
    }

    private async Task RegisterPanelCommandForGuildAsync(SocketGuild guild)
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
                    .AddChoice("edit", "edit")
                    .AddChoice("move", "move")
                    .AddChoice("list", "list")
                    .AddChoice("hide", "hide"))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithDescription("Anzeigename des Panels")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("order")
                    .WithDescription("Reihenfolge (1 = oben)")
                    .WithType(ApplicationCommandOptionType.Integer)
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

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            switch (interaction)
            {
                case SocketSlashCommand cmd when cmd.Data.Name == "panel":
                    await PanelController.HandlePanelCommandAsync(cmd, _panelStorage, _client);
                    break;

                case SocketMessageComponent comp:
                    await PanelController.HandlePanelButtonAsync(comp, _panelStorage, _client);
                    break;

                case SocketModal modal:
                    await PanelController.HandleModalSubmitAsync(modal, _panelStorage, _client);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Fehler in InteractionCreated:");
            Console.WriteLine(ex);
        }
    }
}
