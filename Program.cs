using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using Discord;
using Discord.WebSocket;
using Discord.Interactions;

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
    private readonly string _ignoredUsersCsvFilePath;
    private readonly HashSet<ulong> _ignoredUsers = new HashSet<ulong>(); // Collection for ignored users
    private readonly Dictionary<ulong, int> _userReactionCounts = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, HashSet<ulong>> _userMessageReactions = new Dictionary<ulong, HashSet<ulong>>(); // Dictionary to track reactions
    private readonly int _reactionIncrement;
    private readonly ulong _guildId; // Single guild ID for command registration

    public Bot()
    {
        _csvFilePath = Environment.GetEnvironmentVariable("CSV_FILE_PATH") ?? "user_reactions.csv";
        _ignoredUsersCsvFilePath = Environment.GetEnvironmentVariable("IGNORED_USERS_CSV_PATH") ?? "ignored_users.csv";
        _interactionService = new InteractionService(_client.Rest);
        _guildId = GetGuildId(); // Get guild ID from environment variable

        if (!int.TryParse(Environment.GetEnvironmentVariable("REACTION_INCREMENT"), out _reactionIncrement))
        {
            _reactionIncrement = 1; // Default value if the environment variable is not set or invalid
        }
    }

    public async Task StartAsync()
    {
        _client.Log += LogAsync;
        _client.ReactionAdded += ReactionAddedAsync;
        _client.Ready += ReadyAsync;

        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("DISCORD_BOT_TOKEN environment variable is not set.");
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Register slash commands
        _client.SlashCommandExecuted += HandleSlashCommandAsync;

        // Load existing data from the CSV files
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
        // Register the commands when the bot is ready
        await RegisterCommandsAsync();
    }

    private async Task RegisterCommandsAsync()
    {
        var commandBuilder = new SlashCommandBuilder()
            .WithName("ignorar")
            .WithDescription("Add a user to the ignore list by username.")
            .AddOption("username", ApplicationCommandOptionType.String, "The username of the user to ignore.", isRequired: true);

        if (_guildId != 0)
        {
            await _client.Rest.CreateGuildCommand(commandBuilder.Build(), _guildId);
            Console.WriteLine($"Slash command registered for guild {_guildId}.");
        }
        else
        {
            Console.WriteLine("Guild ID is not set. Slash command not registered.");
        }
    }

    private async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.CommandName == "ignorar")
        {
            var username = command.Data.Options.First().Value.ToString();

            // Find the user by username
            var user = _client.Guilds
                .SelectMany(guild => guild.Users)
                .FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user != null)
            {
                var userId = user.Id;
                AddIgnoredUser(userId);
                await command.RespondAsync($"{username} has been added to the ignored users list.");
            }
            else
            {
                await command.RespondAsync($"User with username {username} not found.");
            }
        }
    }

    private async Task ReactionAddedAsync(Cacheable<IUserMessage, ulong> cacheable, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        var message = await cacheable.GetOrDownloadAsync();
        var messageId = message.Id;
        var messageAuthorId = message.Author.Id;
        var userId = reaction.UserId;

        // Ignore reactions from the ignored users
        if (_ignoredUsers.Contains(userId))
        {
            return;
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
            if (File.Exists(_ignoredUsersCsvFilePath))
            {
                using var reader = new StreamReader(_ignoredUsersCsvFilePath);
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
                // Create the ignored users file if it does not exist
                using var writer = new StreamWriter(_ignoredUsersCsvFilePath);
                using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });
                csv.Context.RegisterClassMap<IgnoredUserMap>();
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

    private void SaveIgnoredUsers()
    {
        try
        {
            using var writer = new StreamWriter(_ignoredUsersCsvFilePath);
            using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
            csvWriter.Context.RegisterClassMap<IgnoredUserMap>();
            csvWriter.WriteRecords(_ignoredUsers.Select(id => new IgnoredUser { UserID = id }));
            Console.WriteLine("Ignored users saved to CSV.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving ignored users: {ex.Message}");
        }
    }

    private void AddIgnoredUser(ulong userId)
    {
        if (_ignoredUsers.Add(userId))
        {
            SaveIgnoredUsers(); // Save the updated ignored users list
        }
    }

    private ulong GetGuildId()
    {
        var guildIdString = Environment.GetEnvironmentVariable("GUILD_ID");
        return ulong.TryParse(guildIdString, out var guildId) ? guildId : 0;
    }
}

// Define a class to represent the CSV record for ignored users
public class IgnoredUser
{
    public ulong UserID { get; set; }
}

// Define a class to represent the CSV record for reactions
public class ReactionLog
{
    public ulong UserID { get; set; }
    public string UserName { get; set; }
    public int ReactionsReceived { get; set; }
}

// Define a mapping class to map properties to CSV headers for ignored users
public sealed class IgnoredUserMap : ClassMap<IgnoredUser>
{
    public IgnoredUserMap()
    {
        Map(m => m.UserID).Name("User ID");
    }
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
