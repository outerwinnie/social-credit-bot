using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using CsvHelper;
using CsvHelper.Configuration;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

class Program
{
    private static DiscordSocketClient _client;
    private static InteractionService _commands;
    private static string _csvFilePath;
    private static int _reactionIncrement;
    private static readonly ConcurrentDictionary<ulong, int> _userReactionCounts = new();

    static async Task Main(string[] args)
    {
        // Read environment variables
        string token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        _csvFilePath = Environment.GetEnvironmentVariable("CSV_FILE_PATH") ?? "reaction_data.csv";
        _reactionIncrement = int.TryParse(Environment.GetEnvironmentVariable("REACTION_INCREMENT"), out var increment) ? increment : 1;

        _client = new DiscordSocketClient();
        _commands = new InteractionService(_client.Rest);

        _client.Log += Log;
        _client.Ready += OnReady;
        _client.ReactionAdded += OnReactionAdded;
        _client.InteractionCreated += HandleInteractionAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
        await Task.Delay(-1); // Keep the bot running
    }

    private static Task Log(LogMessage log)
    {
        Console.WriteLine(log);
        return Task.CompletedTask;
    }

    private static async Task OnReady()
    {
        Console.WriteLine("Bot is connected!");

        await _commands.AddModulesAsync(typeof(Program).Assembly, null);
        await _commands.RegisterCommandsGloballyAsync();
    }

    private static async Task OnReactionAdded(Cacheable<IUserMessage, ulong> cacheableMessage, Cacheable<IMessageChannel, ulong> cacheableChannel, SocketReaction reaction)
    {
        if (reaction.User.IsSpecified && !reaction.User.Value.IsBot)
        {
            var message = await cacheableMessage.GetOrDownloadAsync();
            if (message.Author.IsBot)
                return;

            if (reaction.UserId != message.Author.Id)
            {
                _userReactionCounts.AddOrUpdate(message.Author.Id, _reactionIncrement, (_, oldValue) => oldValue + _reactionIncrement);
                await SaveDataAsync();
            }
        }
    }

    private static async Task SaveDataAsync()
    {
        try
        {
            var existingData = new Dictionary<ulong, ReactionLog>();

            if (File.Exists(_csvFilePath))
            {
                using var reader = new StreamReader(_csvFilePath);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null,
                    MissingFieldFound = null
                });
                csv.Context.RegisterClassMap<ReactionLogMap>();
                var records = csv.GetRecords<ReactionLog>();
                foreach (var record in records)
                {
                    existingData[record.UserID] = record;
                }
            }

            var updatedRecords = new List<ReactionLog>();
            var updatedData = new Dictionary<ulong, ReactionLog>();

            foreach (var kvp in _userReactionCounts)
            {
                if (existingData.ContainsKey(kvp.Key))
                {
                    existingData[kvp.Key].ReactionsReceived = kvp.Value;
                    updatedData[kvp.Key] = existingData[kvp.Key];
                }
                else
                {
                    var user = await _client.GetUserAsync(kvp.Key);
                    var userName = user?.Username ?? "Unknown User";
                    updatedData[kvp.Key] = new ReactionLog
                    {
                        UserID = kvp.Key,
                        UserName = userName,
                        ReactionsReceived = kvp.Value
                    };
                }
            }

            updatedRecords = updatedData.Values.ToList();

            using var writer = new StreamWriter(_csvFilePath);
            using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
            csvWriter.Context.RegisterClassMap<ReactionLogMap>();
            csvWriter.WriteRecords(updatedRecords);

            Console.WriteLine("Data saved to CSV.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving data: {ex.Message}");
        }
    }

    private static async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        if (interaction is SocketMessageComponent messageComponent)
        {
            if (messageComponent.Data.CustomId == "menu-1")
            {
                var selectedValue = messageComponent.Data.Values.FirstOrDefault();
                await messageComponent.UpdateAsync(properties =>
                {
                    properties.Content = $"You selected {selectedValue}.";
                });
            }
        }
        else if (interaction is SocketSlashCommand slashCommand)
        {
            if (slashCommand.Data.Name == "menu")
            {
                var menuBuilder = new SelectMenuBuilder()
                    .WithPlaceholder("Select an option")
                    .WithCustomId("menu-1")
                    .WithMinValues(1)
                    .WithMaxValues(1)
                    .AddOption("Option A", "opt-a", "Option A is the best!")
                    .AddOption("Option B", "opt-b", "Option B is great too!");

                var componentBuilder = new ComponentBuilder()
                    .WithSelectMenu(menuBuilder);

                var embed = new EmbedBuilder()
                    .WithTitle("Menu")
                    .WithDescription("Please select an option from the menu below:")
                    .WithColor(Color.Blue)
                    .Build();

                await slashCommand.RespondAsync(embed: embed, components: componentBuilder.Build());
            }
        }
    }
}

// Ensure you have these classes defined
public class ReactionLog
{
    public ulong UserID { get; set; }
    public string UserName { get; set; }
    public int ReactionsReceived { get; set; }
}

public class ReactionLogMap : ClassMap<ReactionLog>
{
    public ReactionLogMap()
    {
        Map(m => m.UserID).Name("User ID");
        Map(m => m.UserName).Name("User Name");
        Map(m => m.ReactionsReceived).Name("Reactions Received");
    }
}
