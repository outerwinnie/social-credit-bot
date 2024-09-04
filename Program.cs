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
using Microsoft.Extensions.Configuration;
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
    private readonly Dictionary<ulong, int> _userReactionCounts = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, HashSet<ulong>> _userMessageReactions = new Dictionary<ulong, HashSet<ulong>>(); // Dictionary to track reactions
    private readonly HashSet<ulong> _ignoredUsers = new HashSet<ulong>(); // Track ignored users
    private readonly int _reactionIncrement;

    public Bot()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        _csvFilePath = configuration["Discord:CsvFilePath"];
        _ignoredUsersFilePath = configuration["Discord:IgnoredUsersFilePath"];
        if (!int.TryParse(configuration["Discord:ReactionIncrement"], out _reactionIncrement))
        {
            _reactionIncrement = 1; // Default value if the environment variable is not set or invalid
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

        Console.WriteLine("Bot is running...");
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
        try
        {
            var commandService = new SlashCommandBuilder()
                .WithName("menu")
                .WithDescription("Shows a dropdown menu");

            var globalCommand = commandService.Build();
            await _client.Rest.CreateGlobalCommand(globalCommand);
            Console.WriteLine("Slash command registered.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error registering slash commands: {ex.Message}");
        }
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
                        .AddOption("Opción A", "sub_option_a")
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
                else if (component.Data.CustomId == "second_menu")
                {
                    var secondOption = component.Data.Values.FirstOrDefault();
                    if (secondOption == "sub_option_a")
                    {
                        await component.RespondAsync("Seleccionaste Opción A.", ephemeral: true);
                    }
                    else if (secondOption == "sub_option_b")
                    {
                        await component.RespondAsync("Seleccionaste Opción B.", ephemeral: true);
                    }
                }
                else
                {
                    await component.RespondAsync($"Has seleccionado: {selectedOption}", ephemeral: true);
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

        // Debug log for user ID and ignored list
        Console.WriteLine($"Received reaction from user ID: {userId}. Ignored users: {string.Join(", ", _ignoredUsers)}");

        // Skip if the user is in the ignored list
        if (_ignoredUsers.Contains(userId))
        {
            Console.WriteLine($"Ignoring reaction from user ID: {userId}");
            return; // Do nothing if the user is on the ignored list
        }

        // Skip if the message author is in the ignored list
        if (_ignoredUsers.Contains(messageAuthorId))
        {
            Console.WriteLine($"Ignoring reaction to a message by ignored user ID: {messageAuthorId}");
            return; // Do nothing if the message author is on the ignored list
        }

        // Ignore reactions from the message author themselves
        if (userId == messageAuthorId)
        {
            return; // Do nothing if the reaction is from the message author
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
                // New reaction from this user to this message
                _userMessageReactions[messageAuthorId].Add(messageId);

                // Update reaction count for the message author
                if (_userReactionCounts.ContainsKey(messageAuthorId))
                {
                    _userReactionCounts[messageAuthorId] += _reactionIncrement;
                }
                else
                {
                    _userReactionCounts[messageAuthorId] = _reactionIncrement;
                }

                // Log the reaction
                var author = _client.GetUser(messageAuthorId) as SocketUser;
                var authorName = author?.Username ?? "Unknown"; // Fallback if user data is not available
                Console.WriteLine($"Message author {authorName} received a reaction. Total reactions for this user: {_userReactionCounts[messageAuthorId]}.");

                // Save data after updating the reaction count
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
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null, // Disable header validation
                    MissingFieldFound = null // Disable missing field validation
                });
                csv.Context.RegisterClassMap<ReactionLogMap>();
                var records = csv.GetRecords<ReactionLog>();
                foreach (var record in records)
                {
                    _userReactionCounts[record.UserID] = record.ReactionsReceived;
                }
                Console.WriteLine("Data loaded from CSV.");
            }
            else
            {
                // If the CSV file does not exist, create it with headers
                using var writer = new StreamWriter(_csvFilePath);
                using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });
                csv.WriteField("User ID");
                csv.WriteField("User Name");
                csv.WriteField("Reactions Received");
                csv.NextRecord();
                Console.WriteLine("New CSV file created with headers.");
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
            // Read existing data into a dictionary
            var existingData = new Dictionary<ulong, ReactionLog>();
            if (File.Exists(_csvFilePath))
            {
                using var reader = new StreamReader(_csvFilePath);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null, // Disable header validation
                    MissingFieldFound = null // Disable missing field validation
                });
                csv.Context.RegisterClassMap<ReactionLogMap>();
                var records = csv.GetRecords<ReactionLog>();
                foreach (var record in records)
                {
                    existingData[record.UserID] = record;
                }
            }

            // Update or add reaction counts
            lock (_userReactionCounts)
            {
                foreach (var kvp in _userReactionCounts)
                {
                    if (existingData.ContainsKey(kvp.Key))
                    {
                        // Update existing record
                        existingData[kvp.Key].ReactionsReceived = kvp.Value;
                    }
                    else
                    {
                        // Add new record
                        var user = _client.GetUser(kvp.Key) as SocketUser;
                        var userName = user?.Username ?? "Unknown"; // Fallback if user data is not available
                        existingData[kvp.Key] = new ReactionLog
                        {
                            UserID = kvp.Key,
                            UserName = userName,
                            ReactionsReceived = kvp.Value
                        };
                    }
                }
            }

            // Overwrite the CSV file with updated data
            using var writer = new StreamWriter(_csvFilePath);
            using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
            csvWriter.Context.RegisterClassMap<ReactionLogMap>();
            csvWriter.WriteRecords(existingData.Values);

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
                using var reader = new StreamReader(_ignoredUsersFilePath);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null, // Disable header validation
                    MissingFieldFound = null // Disable missing field validation
                });
                csv.Context.RegisterClassMap<IgnoredUserMap>();
                var records = csv.GetRecords<IgnoredUser>();
                foreach (var record in records)
                {
                    _ignoredUsers.Add(record.UserID);
                }
                Console.WriteLine("Ignored users loaded from CSV.");
            }
            else
            {
                // If the ignored users CSV file does not exist, create it with headers
                using var writer = new StreamWriter(_ignoredUsersFilePath);
                using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });
                csv.WriteField("User ID");
                csv.NextRecord();
                Console.WriteLine("New ignored users CSV file created with headers.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading ignored users: {ex.Message}");
        }
    }

    private int GetUserReactionCount(ulong userId)
    {
        _userReactionCounts.TryGetValue(userId, out var reactionCount);
        return reactionCount;
    }
}

// Define a class to represent the CSV record for reactions
public class ReactionLog
{
    public ulong UserID { get; set; }
    public string UserName { get; set; }
    public int ReactionsReceived { get; set; }
}

// Define a mapping class to map properties to CSV headers for reactions
public sealed class ReactionLogMap : ClassMap<ReactionLog>
{
    public ReactionLogMap()
    {
        Map(m => m.UserID).Name("User ID");
        Map(m => m.UserName).Name("User Name");
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
