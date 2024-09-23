﻿using System;
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
            .WithDescription("Abre el menu");

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
                        .AddOption("Roll de Recuerdate (20 creditos)", "sub_option_a");

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
                    // Load updated count of the CSV file.
                    LoadData();

                    var userId = component.User.Id;
                    var reactionsReceived = GetUserReactionCount(userId);

                    if (reactionsReceived >= _recuerdatePrice)
                    {
                        // Subtract the _recuerdatePrice from reactionsReceived
                        reactionsReceived -= _recuerdatePrice;

                        // Update the reaction count
                        _userReactionCounts[userId] = reactionsReceived;

                        // Write the updated count to the CSV file
                        SaveData();

                        // Write the added reward to the CSV file
                        WriteRewardToCsv("recuerdate", 1);

                        // Respond to the interaction
                        await component.RespondAsync("Añadida una nueva imagen a la cola, se enviará en los próximos 5 minutos. Créditos restantes: " + reactionsReceived, ephemeral: true);

                        // Sending a message to a specific channel
                        var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? ""); // Replace with your channel ID if not using env var
                        var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

                        if (targetChannel != null)
                        {
                            // Sending a message to the specific channel and tagging the user
                            var userMention = component.User.Mention; // This will mention the user who used the option
                            await targetChannel.SendMessageAsync($"{userMention} ha canjeado una nueva recompensa 'Recuerdate' por {_recuerdatePrice} créditos.");
                        }
                        else
                        {
                            Console.WriteLine($"Could not find the target channel with ID: {channelId}");
                        }
                    }
                    else
                    {
                        await component.RespondAsync($"No tienes suficiente crédito social. Necesitas {_recuerdatePrice} créditos.", ephemeral: true);
                    }
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
                using (var reader = new StreamReader(_csvFilePath))
                using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
                {
                    var records = csv.GetRecords<UserReactionRecord>().ToList();
                    foreach (var record in records)
                    {
                        _userReactionCounts[record.UserId] = record.ReactionCount;
                    }
                }
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
            using (var writer = new StreamWriter(_csvFilePath))
            using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
            {
                var records = _userReactionCounts.Select(kv => new UserReactionRecord { UserId = kv.Key, ReactionCount = kv.Value }).ToList();
                csv.WriteRecords(records);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving data: {ex.Message}");
        }
    }

    private int GetUserReactionCount(ulong userId)
    {
        return _userReactionCounts.ContainsKey(userId) ? _userReactionCounts[userId] : 0;
    }

    private void WriteRewardToCsv(string rewardType, int quantity)
    {
        try
        {
            using (var writer = new StreamWriter(_rewardsFilePath, append: true))
            using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
            {
                var rewardRecord = new RewardRecord
                {
                    RewardType = rewardType,
                    Quantity = quantity,
                    DateAdded = DateTime.UtcNow
                };

                csv.WriteRecord(rewardRecord);
                csv.NextRecord(); 
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error writing reward to CSV: {ex.Message}");
        }
    }

    private void LoadIgnoredUsers()
    {
        try
        {
            if (File.Exists(_ignoredUsersFilePath))
            {
                using (var reader = new StreamReader(_ignoredUsersFilePath))
                using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
                {
                    var records = csv.GetRecords<IgnoredUserRecord>().ToList();
                    foreach (var record in records)
                    {
                        _ignoredUsers.Add(record.UserId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading ignored users: {ex.Message}");
        }
    }

    private void CreateRewardsFileIfNotExists()
    {
        try
        {
            if (!File.Exists(_rewardsFilePath))
            {
                using (var writer = new StreamWriter(_rewardsFilePath))
                using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
                {
                    csv.WriteHeader<RewardRecord>();
                    csv.NextRecord(); 
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating rewards file: {ex.Message}");
        }
    }

    private class UserReactionRecord
    {
        public ulong UserId { get; set; }
        public int ReactionCount { get; set; }
    }

    private class RewardRecord
    {
        public string RewardType { get; set; }
        public int Quantity { get; set; }
        public DateTime DateAdded { get; set; }
    }

    private class IgnoredUserRecord
    {
        public ulong UserId { get; set; }
    }
}
