using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

class Program
{
    private static async Task Main(string[] args)
    {
        var bot = new Bot();
        await bot.StartAsync();
        await Task.Delay(-1); // Prevents the application from exiting
    }
}

class Bot
{
    private readonly DiscordSocketClient _client = new DiscordSocketClient();
    private readonly InteractionService _interactionService;
    private readonly IServiceProvider _services;
    private readonly string _csvFilePath;
    private readonly string _ignoredUsersFilePath;
    private readonly string _rewardsFilePath;
    private readonly Dictionary<ulong, int> _userReactionCounts = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, HashSet<ulong>> _userMessageReactions = new Dictionary<ulong, HashSet<ulong>>(); // Dictionary to track reactions
    private readonly HashSet<ulong> _ignoredUsers = new HashSet<ulong>(); // Track ignored users
    private readonly int _reactionIncrement;
    private readonly int _recuerdatePrice; // Reaction threshold from environment variable

    public Bot()
    {
        _csvFilePath = Environment.GetEnvironmentVariable("CSV_FILE_PATH") ?? "user_reactions.csv";
        _ignoredUsersFilePath = Environment.GetEnvironmentVariable("IGNORED_USERS_FILE_PATH") ?? "ignored_users.csv";
        _rewardsFilePath = Environment.GetEnvironmentVariable("REWARDS_FILE_PATH") ?? "rewards.csv";

        if (!int.TryParse(Environment.GetEnvironmentVariable("REACTION_INCREMENT"), out _reactionIncrement))
        {
            _reactionIncrement = 1; // Default value if the environment variable is not set or invalid
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable("RECUERDATE_PRICE"), out _recuerdatePrice))
        {
            _recuerdatePrice = 5; // Default value if the environment variable is not set or invalid
        }

        _interactionService = new InteractionService(_client.Rest);
        _services = new ServiceCollection()
            .AddSingleton(_client)
            .AddSingleton(_interactionService)
            .BuildServiceProvider();
    }

    public async Task StartAsync()
    {
        _client.Log += LogAsync;
        _client.ReactionAdded += ReactionAddedAsync;
        _client.Ready += ReadyAsync;
        _client.InteractionCreated += InteractionCreated;

        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("DISCORD_BOT_TOKEN environment variable is not set.");
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Load existing data and ignored users from CSV files
        LoadData();
        LoadIgnoredUsers();
        CreateRewardsFileIfNotExists();

        Console.WriteLine("Bot is running...");
        Console.WriteLine($"RECUERDATE_PRICE: {_recuerdatePrice}");
        Console.WriteLine($"REACTION_INCREMENT: {_reactionIncrement}");
    }

    private Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log);
        return Task.CompletedTask;
    }

    private async Task ReadyAsync()
    {
        // Register slash commands
        await RegisterSlashCommands();
    }

    private async Task RegisterSlashCommands()
    {
        var commandService = new SlashCommandBuilder()
            .WithName("menu")
            .WithDescription("Shows a dropdown menu");

        var globalCommand = commandService.Build();
        await _client.Rest.CreateGlobalCommand(globalCommand);
        Console.WriteLine("Slash command registered.");
    }

    private async Task InteractionCreated(SocketInteraction interaction)
    {
        if (interaction is SocketSlashCommand command)
        {
            if (command.Data.Name == "menu")
            {
                var menu = new SelectMenuBuilder()
                    .WithCustomId("select_menu")
                    .WithPlaceholder("Elige una opción...")
                    .AddOption("Canjear una recompensa", "option1")
                    .AddOption("Credito actual", "option2");

                var message = new ComponentBuilder()
                    .WithSelectMenu(menu)
                    .Build();

                // Respond with an ephemeral message
                await command.RespondAsync("Elija una opción:", components: message, ephemeral: true);
            }
        }
        else if (interaction is SocketMessageComponent component)
        {
            if (component.Data.CustomId == "select_menu")
            {
                var selectedOption = component.Data.Values.FirstOrDefault();

                if (selectedOption == "option1")
                {
                    // Create and send the second menu when option1 is selected
                    var secondMenu = new SelectMenuBuilder()
                        .WithCustomId("second_menu")
                        .WithPlaceholder("Elige una sub-opción...")
                        .AddOption("Roll de Recuerdate", "sub_option_a")
                        .AddOption("Opción B", "sub_option_b");

                    var secondMessage = new ComponentBuilder()
                        .WithSelectMenu(secondMenu)
                        .Build();

                    // Respond to the interaction with the second menu
                    await component.RespondAsync("Selecciona una sub-opción:", components: secondMessage, ephemeral: true);
                }
                else if (selectedOption == "option2")
                {
                    var userId = component.User.Id;
                    var reactionsReceived = GetUserReactionCount(userId);
                    await component.RespondAsync($"Posees {reactionsReceived} créditos.", ephemeral: true);
                }
            }
            else if (component.Data.CustomId == "second_menu")
            {
                var secondOption = component.Data.Values.FirstOrDefault();
                if (secondOption == "sub_option_a")
                {
                    var userId = component.User.Id;
                    var reactionsReceived = GetUserReactionCount(userId);
                    if (reactionsReceived >= _recuerdatePrice)
                    {
                        // Subtract the recuerdate price from the user's reactions
                        _userReactionCounts[userId] -= _recuerdatePrice;

                        // Write the reward to the rewards CSV file
                        WriteRewardToCsv("recuerdate", 1);

                        // Save the updated reaction data to the CSV
                        SaveData();

                        await component.RespondAsync($"Recompensa 'recuerdate' añadida con cantidad 1. Se te descontaron {_recuerdatePrice} reacciones. Te quedan {_userReactionCounts[userId]} reacciones.", ephemeral: true);
                    }
                    else
                    {
                        await component.RespondAsync($"No tienes suficientes reacciones. Necesitas {_recuerdatePrice} reacciones.", ephemeral: true);
                    }
                }
                else if (secondOption == "sub_option_b")
                {
                    await component.RespondAsync("Seleccionaste Opción B.", ephemeral: true);
                }
            }
        }
    }

    private async Task ReactionAddedAsync(Cacheable<IUserMessage, ulong> cacheable, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        var message = await cacheable.GetOrDownloadAsync();
        var messageId = message.Id;
        var messageAuthorId = message.Author.Id;
        var userId = reaction.UserId;

        Console.WriteLine($"Received reaction from user ID: {userId}. Ignored users: {string.Join(", ", _ignoredUsers)}");

        // Skip if the user is in the ignored list
        if (_ignoredUsers.Contains(userId))
        {
            Console.WriteLine($"Ignoring reaction from user ID: {userId}");
            return;
        }

        // Skip if the message author is in the ignored list
        if (_ignoredUsers.Contains(messageAuthorId))
        {
            Console.WriteLine($"Ignoring reaction to a message by ignored user ID: {messageAuthorId}");
            return;
        }

        // Ignore reactions from the message author themselves
        if (userId == messageAuthorId)
        {
            return;
        }

        // Ensure the reaction tracking dictionary is initialized
        if (!_userMessageReactions.ContainsKey(messageAuthorId))
        {
            _userMessageReactions[messageAuthorId] = new HashSet<ulong>();
        }

        lock (_userMessageReactions)
        {
            if (!_userMessageReactions[messageAuthorId].Contains(messageId))
            {
                _userMessageReactions[messageAuthorId].Add(messageId);

                if (_userReactionCounts.ContainsKey(messageAuthorId))
                {
                    _userReactionCounts[messageAuthorId] += _reactionIncrement;
                }
                else
                {
                    _userReactionCounts[messageAuthorId] = _reactionIncrement;
                }

                var author = _client.GetUser(messageAuthorId) as SocketUser;
                var authorName = author?.Username ?? "Unknown";
                Console.WriteLine($"Message author {authorName} received a reaction. Total reactions for this user: {_userReactionCounts[messageAuthorId]}.");

                SaveData();
            }
        }
    }

    private void LoadData()
    {
        try
        {
            if (File.Exists(_csvFilePath))
            {
                using var reader = new StreamReader(_csvFilePath);
                using var csvReader = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null,
                    MissingFieldFound = null
                });
                csvReader.Context.RegisterClassMap<ReactionLogMap>();
                var records = csvReader.GetRecords<ReactionLog>();
                foreach (var record in records)
                {
                    _userReactionCounts[record.UserID] = record.ReactionsReceived;
                }
                Console.WriteLine("Data loaded from CSV.");
            }
            else
            {
                File.Create(_csvFilePath).Dispose(); // Create an empty CSV file if it doesn't exist
                Console.WriteLine("Created new CSV file for user reactions.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading data: {ex.Message}");
        }
    }

    private void SaveData()
    {
        try
        {
            using var writer = new StreamWriter(_csvFilePath);
            using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
            csvWriter.Context.RegisterClassMap<ReactionLogMap>();
            csvWriter.WriteRecords(_userReactionCounts.Select(kvp => new ReactionLog
            {
                UserID = kvp.Key,
                ReactionsReceived = kvp.Value
            }));
            Console.WriteLine("Data saved to CSV.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving data: {ex.Message}");
        }
    }

    private void LoadIgnoredUsers()
    {
        try
        {
            if (File.Exists(_ignoredUsersFilePath))
            {
                _ignoredUsers.UnionWith(File.ReadAllLines(_ignoredUsersFilePath).Select(ulong.Parse));
                Console.WriteLine("Ignored users loaded from CSV.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading ignored users: {ex.Message}");
        }
    }

    private void CreateRewardsFileIfNotExists()
    {
        if (!File.Exists(_rewardsFilePath))
        {
            File.Create(_rewardsFilePath).Dispose();
            Console.WriteLine("Created new rewards CSV file.");
        }
    }

    private void WriteRewardToCsv(string rewardName, int quantity)
    {
        try
        {
            using var writer = new StreamWriter(_rewardsFilePath, append: true);
            using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
            csvWriter.WriteField(rewardName);
            csvWriter.WriteField(quantity);
            csvWriter.NextRecord();
            Console.WriteLine($"Reward '{rewardName}' with quantity {quantity} written to rewards CSV.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error writing reward to CSV: {ex.Message}");
        }
    }

    private int GetUserReactionCount(ulong userId)
    {
        return _userReactionCounts.ContainsKey(userId) ? _userReactionCounts[userId] : 0;
    }

    private class ReactionLog
    {
        public ulong UserID { get; set; }
        public string UserName { get; set; }
        public int ReactionsReceived { get; set; }
    }

    private class ReactionLogMap : ClassMap<ReactionLog>
    {
        public ReactionLogMap()
        {
            Map(m => m.UserID).Name("User ID");
            Map(m => m.UserID).Name("User ID");
            Map(m => m.ReactionsReceived).Name("Reactions Received");
        }
    }

    // Define a class to represent the CSV record for ignored users
    public class IgnoredUser
    {
        public ulong UserID { get; set; }
    }

    // Define a mapping class to map properties to CSV headers for ignored users
    public sealed class IgnoredUserMap : ClassMap<IgnoredUser>
    {
    public IgnoredUserMap()
    {
        Map(m => m.UserID).Name("User ID");
    }
    }
}
