using System.Globalization;
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
        
        ScheduleMonthlyRedistribution(int.Parse(Environment.GetEnvironmentVariable("CREDIT_PERCENTAGE") ?? throw new InvalidOperationException()));
        Console.WriteLine($"CREDIT_PERCENTAGE:" + int.Parse(Environment.GetEnvironmentVariable("CREDIT_PERCENTAGE")));

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
        
        var addCreditsCommand = new SlashCommandBuilder()
            .WithName("añadir")
            .WithDescription("Añade créditos a un usuario")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("ID del usuario al que añadir créditos")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.User))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cantidad")
                .WithDescription("Cantidad de créditos a añadir")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer));
        
        var addCreditsGlobalCommand = addCreditsCommand.Build();
        await _client.Rest.CreateGlobalCommand(addCreditsGlobalCommand);
        Console.WriteLine("Slash command 'añadir' registered.");
        
        var removeCreditsCommand = new SlashCommandBuilder()
            .WithName("descontar")
            .WithDescription("Descuenta créditos a un usuario")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("ID del usuario al que descontar créditos")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.User))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cantidad")
                .WithDescription("Cantidad de créditos a descontar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer));
        
        var removeCreditsGlobalCommand = removeCreditsCommand.Build();
        await _client.Rest.CreateGlobalCommand(removeCreditsGlobalCommand);
        Console.WriteLine("Slash command 'descontar' registered.");
    }
    
    private void ScheduleMonthlyRedistribution(decimal percentage)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                DateTime now = DateTime.Now;
                DateTime nextRun = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 59, 59);

                TimeSpan waitTime = nextRun - now;
                Console.WriteLine($"{waitTime}%");
                await Task.Delay(waitTime);
                
                RedistributeWealth(percentage);
            }
        });
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
        
        else if (command.Data.Name == "añadir")
        {
            var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "usuario");
            var amountOption = command.Data.Options.FirstOrDefault(o => o.Name == "cantidad");

            // Define the authorized user ID (replace with the actual user ID)
            ulong authorizedUserId = ulong.Parse(Environment.GetEnvironmentVariable("ADMIN_USER_ID") ?? throw new InvalidOperationException()); // Replace with the actual Discord user ID of the authorized user

            // Check if the user invoking the command is authorized
            if (command.User.Id != authorizedUserId)
            {
                // Respond with an error message if the user is not authorized
                await command.RespondAsync("No tienes permiso para usar este comando.", ephemeral: true);
                return;
            }
            
            if (userOption != null && amountOption != null)
            {
                ulong userId = (userOption.Value as SocketUser)?.Id ?? 0;
                int amount = Convert.ToInt32(amountOption.Value);
                
                if (userId != 0)
                {
                    LoadData();
                    
                    // Add credits to the user
                    if (!_userReactionCounts.ContainsKey(userId))
                    {
                        _userReactionCounts[userId] = 0;
                    }

                    _userReactionCounts[userId] += amount;
                    SaveData(); // Save updated data to CSV

                    // Send a confirmation message
                    await command.RespondAsync($"Se han añadido {amount} créditos al usuario <@{userId}>. Créditos actuales: {_userReactionCounts[userId]}", ephemeral: true);
                }
                else
                {
                    await command.RespondAsync("Usuario no válido.", ephemeral: true);
                }
            }
            else
            {
                await command.RespondAsync("Faltan argumentos. Asegúrese de proporcionar un usuario y una cantidad de créditos.", ephemeral: true);
            }
        }
        
        else if (command.Data.Name == "descontar")
        {
            var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "usuario");
            var amountOption = command.Data.Options.FirstOrDefault(o => o.Name == "cantidad");

            // Define the authorized user ID (replace with the actual user ID)
            ulong authorizedUserId = ulong.Parse(Environment.GetEnvironmentVariable("ADMIN_USER_ID") ?? throw new InvalidOperationException()); // Replace with the actual Discord user ID of the authorized user

            // Check if the user invoking the command is authorized
            if (command.User.Id != authorizedUserId)
            {
                // Respond with an error message if the user is not authorized
                await command.RespondAsync("No tienes permiso para usar este comando.", ephemeral: true);
                return;
            }
            
            if (userOption != null && amountOption != null)
            {
                ulong userId = (userOption.Value as SocketUser)?.Id ?? 0;
                int amount = Convert.ToInt32(amountOption.Value);
                
                if (userId != 0)
                {
                    LoadData();
                    
                    // Discount credits to the user
                    if (!_userReactionCounts.ContainsKey(userId))
                    {
                        _userReactionCounts[userId] = 0;
                    }

                    _userReactionCounts[userId] -= amount;
                    SaveData(); // Save updated data to CSV

                    // Send a confirmation message
                    await command.RespondAsync($"Se han descontado {amount} créditos al usuario <@{userId}>. Créditos actuales: {_userReactionCounts[userId]}", ephemeral: true);
                }
                else
                {
                    await command.RespondAsync("Usuario no válido.", ephemeral: true);
                }
            }
            else
            {
                await command.RespondAsync("Faltan argumentos. Asegúrese de proporcionar un usuario y una cantidad de créditos.", ephemeral: true);
            }
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

                //Load updated count of the CSV file.
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

                    //Write the added reward to the CSV file
                    WriteRewardToCsv("recuerdate", 1);
                    
                    // Respond to the interaction
                    await component.RespondAsync("Añadida una nueva imagena a la cola, se enviara en los proximos 5 minutos. Créditos restantes: " + reactionsReceived, ephemeral: true);
                    
                    // Sending a message to a specific channel
                    var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? ""); // Replace with your channel ID if not using env var
                    var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

                    if (targetChannel != null)
                    {
                    // Sending a message to the specific channel and tagging the user
                    var userMention = component.User.Mention; // This will mention the user who used the option
                    await targetChannel.SendMessageAsync($"{userMention} ha canjeado una nueva recompensa 'Recuerdate' por { _recuerdatePrice} créditos.");
                    }
                    else
                    {
                        Console.WriteLine($"Could not find the target channel with ID: {channelId}");
                    }
                }
                else
                {
                    await component.RespondAsync($"No tienes suficiente credito social. Necesitas {_recuerdatePrice} creditos.", ephemeral: true);
                }
            }
        }
    }
}
    
    private void RedistributeWealth(decimal percentage)
    {
        if (percentage <= 0 || percentage > 100)
        {
            Console.WriteLine("Invalid percentage. Please provide a value between 0 and 100.");
            return;
        }

        if (_userReactionCounts.Count < 2)
        {
            Console.WriteLine("Not enough users to redistribute wealth.");
            return;
        }

        LoadData();
        
        // Find the wealthiest user
        var wealthiestUser = _userReactionCounts.OrderByDescending(kvp => kvp.Value).First();
        ulong wealthiestUserId = wealthiestUser.Key;
        int wealthiestUserCredits = wealthiestUser.Value;

        // Calculate the amount to redistribute
        int amountToRedistribute = (int)(wealthiestUserCredits * (percentage / 100m));
        if (amountToRedistribute <= 0)
        {
            Console.WriteLine("Redistribution amount is too small to be meaningful.");
            return;
        }

        // Deduct the amount from the wealthiest user
        _userReactionCounts[wealthiestUserId] -= amountToRedistribute;

        // Calculate the amount each other user receives
        int numberOfRecipients = _userReactionCounts.Count - 1;
        int amountPerUser = amountToRedistribute / numberOfRecipients;

        foreach (var userId in _userReactionCounts.Keys.ToList())
        {
            if (userId != wealthiestUserId)
            {
                _userReactionCounts[userId] += amountPerUser;
            }
        }

        // Save the updated data to the CSV
        SaveData();
        
        // Sending a message to a specific channel
        var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? ""); // Replace with your channel ID if not using env var
        var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

        if (targetChannel != null)
        {
            // Sending a message to the specific channel and tagging the user
            targetChannel.SendMessageAsync("A");
        }
        else
        {
            Console.WriteLine($"Could not find the target channel with ID: {channelId}");
        }

        Console.WriteLine($"Redistributed {amountToRedistribute} credits from user {wealthiestUserId}. Each of the other {numberOfRecipients} users received {amountPerUser} credits.");
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

                var author = _client.GetUser(messageAuthorId);
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
                using var writer = new StreamWriter(_csvFilePath);
                using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });
                csvWriter.WriteField("User ID");
                csvWriter.WriteField("User Name");
                csvWriter.WriteField("Reactions Received");
                csvWriter.NextRecord();
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
            var existingData = new Dictionary<ulong, ReactionLog>();
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
                existingData = records.ToDictionary(r => r.UserID);
            }

            using var writer = new StreamWriter(_csvFilePath);
            using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });
            csvWriter.Context.RegisterClassMap<ReactionLogMap>();
            csvWriter.WriteHeader<ReactionLog>();
            csvWriter.NextRecord();

            foreach (var userReaction in _userReactionCounts)
            {
                var record = new ReactionLog
                {
                    UserID = userReaction.Key,
                    ReactionsReceived = userReaction.Value,
                };

                if (existingData.ContainsKey(userReaction.Key))
                {
                    existingData[userReaction.Key] = record;
                }
                else
                {
                    existingData.Add(userReaction.Key, record);
                }

                csvWriter.WriteRecord(record);
                csvWriter.NextRecord();
            }
            Console.WriteLine("Data saved to CSV.");
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

    private void LoadIgnoredUsers()
{
    try
    {
        if (!File.Exists(_ignoredUsersFilePath))
        {
            // Create the file if it doesn't exist
            using var writer = new StreamWriter(_ignoredUsersFilePath);
            writer.WriteLine("User ID"); // Write a header
            Console.WriteLine("Ignored users CSV file created.");
        }

        // Load ignored users from the file
        using var reader = new StreamReader(_ignoredUsersFilePath);
        using var csvReader = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        _ignoredUsers.UnionWith(csvReader.GetRecords<ulong>());
        Console.WriteLine($"Loaded {_ignoredUsers.Count} ignored users.");
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
            using var writer = new StreamWriter(_rewardsFilePath);
            writer.WriteLine("RewardName,Quantity");
            Console.WriteLine("Rewards CSV file created.");
        }
    }

    private void WriteRewardToCsv(string rewardName, int quantity)
    {
        try
        {
            var rewards = new List<Reward>();
            if (File.Exists(_rewardsFilePath))
            {
                using var reader = new StreamReader(_rewardsFilePath);
                using var csvReader = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
                csvReader.Context.RegisterClassMap<RewardMap>();
                rewards = csvReader.GetRecords<Reward>().ToList();
            }

            var existingReward = rewards.FirstOrDefault(r => r.RewardName == rewardName);
            if (existingReward != null)
            {
                existingReward.Quantity += quantity;
            }
            else
            {
                rewards.Add(new Reward
                {
                    RewardName = rewardName,
                    Quantity = quantity
                });
            }

            using var writer = new StreamWriter(_rewardsFilePath);
            using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
            csvWriter.Context.RegisterClassMap<RewardMap>();
            csvWriter.WriteRecords(rewards);

            Console.WriteLine($"Reward '{rewardName}' updated in CSV. New quantity: {existingReward?.Quantity ?? quantity}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error writing reward to CSV: {ex.Message}");
        }
    }
}

// Helper classes and mappings
class Reward
{
    public string RewardName { get; set; }
    public int Quantity { get; set; }
}

class RewardMap : ClassMap<Reward>
{
    public RewardMap()
    {
        Map(m => m.RewardName).Name("RewardName");
        Map(m => m.Quantity).Name("Quantity");
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