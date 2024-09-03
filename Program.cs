using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using Discord;
using Discord.WebSocket;

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
    private readonly string _csvFilePath = "user_reactions.csv";
    private readonly Dictionary<ulong, int> _userReactionCounts = new Dictionary<ulong, int>();
    private const int ReactionIncrement = 1; // Configurable increment value

    public async Task StartAsync()
    {
        _client.Log += LogAsync;
        _client.ReactionAdded += ReactionAddedAsync;

        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("DISCORD_BOT_TOKEN environment variable is not set.");
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Load existing data from the CSV file
        LoadData();

        Console.WriteLine("Bot is running...");
    }

    private Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log);
        return Task.CompletedTask;
    }

    private async Task ReactionAddedAsync(Cacheable<IUserMessage, ulong> cacheable, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        // Retrieve the message and its author from the cache
        var message = await cacheable.GetOrDownloadAsync();
        var messageAuthorId = message.Author.Id; // Get the message author's ID

        // Update reaction count for the message author
        lock (_userReactionCounts)
        {
            if (_userReactionCounts.ContainsKey(messageAuthorId))
            {
                _userReactionCounts[messageAuthorId] += ReactionIncrement; // Use the increment value
            }
            else
            {
                _userReactionCounts[messageAuthorId] = ReactionIncrement; // Initialize with increment value
            }
        }

        // Log the reaction
        var author = _client.GetUser(messageAuthorId) as SocketUser;
        var authorName = author?.Username ?? "Unknown";
        Console.WriteLine($"Message author {authorName} received a reaction. Total reactions for this user: {_userReactionCounts[messageAuthorId]}.");

        // Save data after updating the reaction count
        SaveData();
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
                        var userName = user?.Username ?? "Unknown";
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
}

// Define a class to represent the CSV record
public class ReactionLog
{
    public ulong UserID { get; set; }
    public string UserName { get; set; }
    public int ReactionsReceived { get; set; }
}

// Define a mapping class to map properties to CSV headers
public sealed class ReactionLogMap : ClassMap<ReactionLog>
{
    public ReactionLogMap()
    {
        Map(m => m.UserID).Name("User ID");
        Map(m => m.UserName).Name("User Name");
        Map(m => m.ReactionsReceived).Name("Reactions Received");
    }
}
