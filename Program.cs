using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace Social_Credit_Bot;

class Program
{
    private static async Task Main()
    {
        var bot = new Bot();
        await bot.StartAsync();
        await Task.Delay(-1); // Prevents the application from exiting
    }
}

class Bot
{
    private readonly DiscordSocketClient _client;
    private readonly string _csvFilePath;
    private readonly string _ignoredUsersFilePath;
    private readonly string _rewardsFilePath;
    private readonly Dictionary<ulong, int> _userReactionCounts = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, HashSet<ulong>> _userMessageReactions = new Dictionary<ulong, HashSet<ulong>>(); // Dictionary to track reactions
    private readonly HashSet<ulong> _ignoredUsers = new HashSet<ulong>(); // Track ignored users
    private readonly int _reactionIncrement;
    private readonly int _recuerdatePrice;
    private readonly int _preguntarPrice;
    private readonly int _memePrice;
    private readonly ulong _guildId;
    private readonly ulong _adminId;
    private static string _apiUrl = null!;
    private static string _safeKey = null!;
    private static string _apiChatUrl = null!;
    private readonly string _dailyTaskTime;
    private readonly string _dailyTaskReward;
    private readonly int _dailyQuizReward_1;
    private readonly int _dailyQuizReward_2;
    private readonly int _dailyQuizReward_3;
    private string? _uploader = string.Empty;
    private HashSet<ulong> _revelarTriedUsers = new HashSet<ulong>();
    private List<ulong> _revelarCorrectUsers = new List<ulong>();
    private readonly string _quizStatePath; // now instance field
    
    // Retar challenge system
    private readonly Dictionary<string, RetarChallenge> _activeRetarChallenges = new Dictionary<string, RetarChallenge>();
    private readonly string _retarChallengesPath;

    private class QuizState
    {
        public string? Uploader { get; set; }
        public List<ulong> CorrectUsers { get; set; } = new List<ulong>();
        public List<ulong> TriedUsers { get; set; } = new List<ulong>();
    }
    
    private class RetarChallenge
    {
        public string ChallengeId { get; set; } = string.Empty;
        public ulong ChallengerId { get; set; }
        public ulong ChallengedId { get; set; }
        public int BetAmount { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsAccepted { get; set; }
        public string? ImageUrl { get; set; }
        public Dictionary<ulong, int> GuessAttempts { get; set; } = new Dictionary<ulong, int>();
        public int ChallengerAttempts { get; set; } = 0;
        public int ChallengedAttempts { get; set; } = 0;
        public ulong? WinnerId { get; set; }
        public bool IsCompleted { get; set; }
    }

    private void SaveQuizState()
    {
        try
        {
            var state = new QuizState
            {
                Uploader = _uploader,
                CorrectUsers = new List<ulong>(_revelarCorrectUsers),
                TriedUsers = new List<ulong>(_revelarTriedUsers)
            };
            var json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            // Ensure directory exists before writing (handles custom paths)
            var dir = Path.GetDirectoryName(_quizStatePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            Console.WriteLine($"[DEBUG] Attempting to write quiz state to: {_quizStatePath}");
            Console.WriteLine($"[DEBUG] Current Directory: {Directory.GetCurrentDirectory()}");
            File.WriteAllText(_quizStatePath, json, Encoding.UTF8); // This will create or overwrite the file
            if (!File.Exists(_quizStatePath))
                Console.WriteLine($"Quiz state file was not created: {_quizStatePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving quiz state: {ex.Message}");
        }
    }

    private void LoadQuizState()
    {
        try
        {
            if (!File.Exists(_quizStatePath))
            {
                _uploader = string.Empty;
                _revelarCorrectUsers.Clear();
                _revelarTriedUsers.Clear();
                return;
            }
            var json = File.ReadAllText(_quizStatePath, Encoding.UTF8);
            var state = System.Text.Json.JsonSerializer.Deserialize<QuizState>(json);
            if (state != null)
            {
                _uploader = state.Uploader ?? string.Empty;
                _revelarCorrectUsers = state.CorrectUsers ?? new List<ulong>();
                _revelarTriedUsers = state.TriedUsers != null ? new HashSet<ulong>(state.TriedUsers) : new HashSet<ulong>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading quiz state: {ex.Message}");
            _uploader = string.Empty;
            _revelarCorrectUsers.Clear();
            _revelarTriedUsers.Clear();
        }
    }
    private static string? _revelarLeaderboardPath;
    private static Dictionary<ulong, int> _revelarLeaderboard = new Dictionary<ulong, int>();

    private readonly string _votesFilePath;
    private List<VoteRecord> _votes = new List<VoteRecord>();

    public Bot()
    {
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers
        };
        
        _quizStatePath = Path.Combine(Environment.CurrentDirectory, "quiz_state.json");
        _retarChallengesPath = Path.Combine(Environment.CurrentDirectory, "retar_challenges.json");
        LoadQuizState();
        LoadVotes();
        LoadRetarChallenges();
        _client = new DiscordSocketClient(config);
        _csvFilePath = Environment.GetEnvironmentVariable("CSV_FILE_PATH") ?? "user_reactions.csv";
        _ignoredUsersFilePath = Environment.GetEnvironmentVariable("IGNORED_USERS_FILE_PATH") ?? "ignored_users.csv";
        _rewardsFilePath = Environment.GetEnvironmentVariable("REWARDS_FILE_PATH") ?? "rewards.csv";
        _votesFilePath = Environment.GetEnvironmentVariable("VOTES_FILE_PATH") ?? "votes.csv";
        LoadVotes();
        _revelarLeaderboardPath = Environment.GetEnvironmentVariable("REVELAR_LEADERBOARD_PATH") ?? "revelar_leaderboard.json";
        LoadRevelarLeaderboard();
        _dailyTaskTime = Environment.GetEnvironmentVariable("DAILY_TASK_TIME") ?? "18:00";
        _dailyTaskReward = Environment.GetEnvironmentVariable("DAILY_TASK_REWARD") ?? "image";
        _guildId = ulong.Parse(Environment.GetEnvironmentVariable("GUILD_ID") ?? throw new InvalidOperationException());
        _adminId = ulong.Parse(Environment.GetEnvironmentVariable("ADMIN_USER_ID") ?? throw new InvalidOperationException());
        _apiUrl = (Environment.GetEnvironmentVariable("API_URL") ?? throw new InvalidOperationException());
        _apiChatUrl = (Environment.GetEnvironmentVariable("API_CHAT_URL") ?? throw new InvalidOperationException());
        _safeKey = Convert.ToString(Environment.GetEnvironmentVariable("SAFE_KEY") ?? throw new InvalidOperationException());

        
        if (!int.TryParse(Environment.GetEnvironmentVariable("PREGUNTAR_PRICE"), out _preguntarPrice))
        {
            _preguntarPrice = 30; // Default value if the environment variable is not set or invalid
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable("MEME_PRICE"), out _memePrice))
        {
            _memePrice = 25; // Default value if the environment variable is not set or invalid
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable("DAILY_QUIZ_REWARD_1"), out _dailyQuizReward_1))
        {
            _dailyQuizReward_1 =18; // Default value if the environment variable is not set or invalid
        }
        
        if (!int.TryParse(Environment.GetEnvironmentVariable("DAILY_QUIZ_REWARD_2"), out _dailyQuizReward_2))
        {
            _dailyQuizReward_2 = 10; // Default value if the environment variable is not set or invalid
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable("DAILY_QUIZ_REWARD_3"), out _dailyQuizReward_3))
        {
            _dailyQuizReward_3 = 5; // Default value if the environment variable is not set or invalid
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable("REACTION_INCREMENT"), out _reactionIncrement))
        {
            _reactionIncrement = 1; // Default value if the environment variable is not set or invalid
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable("RECUERDATE_PRICE"), out _recuerdatePrice))
        {
            _recuerdatePrice = 15; // Default value if the environment variable is not set or invalid
        }

        var interactionService = new InteractionService(_client.Rest);
        new ServiceCollection()
            .AddSingleton(_client)
            .AddSingleton(interactionService)
            .BuildServiceProvider();

    // Ensure quiz state file exists on startup
    SaveQuizState();
    }
    
    private void LoadRetarChallenges()
    {
        try
        {
            if (!File.Exists(_retarChallengesPath))
            {
                _activeRetarChallenges.Clear();
                return;
            }
            var json = File.ReadAllText(_retarChallengesPath, Encoding.UTF8);
            var challenges = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, RetarChallenge>>(json);
            if (challenges != null)
            {
                _activeRetarChallenges.Clear();
                foreach (var kvp in challenges)
                {
                    // Remove expired challenges (older than 1 hour)
                    if (DateTime.Now - kvp.Value.CreatedAt < TimeSpan.FromHours(1))
                    {
                        _activeRetarChallenges[kvp.Key] = kvp.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading retar challenges: {ex.Message}");
            _activeRetarChallenges.Clear();
        }
    }
    
    private void SaveRetarChallenges()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_activeRetarChallenges, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_retarChallengesPath, json, Encoding.UTF8);
            Console.WriteLine($"[RETAR] Challenges saved to {_retarChallengesPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving retar challenges: {ex.Message}");
        }
    }

    private void LoadVotes()
    {
        try
        {
            if (!File.Exists(_votesFilePath))
            {
                using (var writer = new StreamWriter(_votesFilePath))
                using (var csvWriter = new CsvWriter(writer, new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)))
                {
                    csvWriter.WriteHeader<VoteRecord>();
                    csvWriter.NextRecord();
                }
                _votes = new List<VoteRecord>();
                return;
            }
            using (var reader = new StreamReader(_votesFilePath))
            using (var csv = new CsvReader(reader, new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)))
            {
                _votes = csv.GetRecords<VoteRecord>().ToList();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading votes: {ex.Message}");
            _votes = new List<VoteRecord>();
        }
    }

    private void SaveVotes()
    {
        try
        {
            using (var writer = new StreamWriter(_votesFilePath))
            using (var csv = new CsvWriter(writer, new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)))
            {
                csv.WriteHeader<VoteRecord>();
                csv.NextRecord();
                foreach (var vote in _votes)
                {
                    csv.WriteRecord(vote);
                    csv.NextRecord();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving votes: {ex.Message}");
        }
    }


    // Loads the /revelar leaderboard from a JSON file. Handles missing or corrupted files gracefully.
    private static void LoadRevelarLeaderboard()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_revelarLeaderboardPath))
                _revelarLeaderboardPath = "revelar_leaderboard.json";
            if (!File.Exists(_revelarLeaderboardPath))
            {
                _revelarLeaderboard = new Dictionary<ulong, int>();
                File.WriteAllText(_revelarLeaderboardPath, "{}", Encoding.UTF8);
                Console.WriteLine($"Leaderboard file not found, created new at '{_revelarLeaderboardPath}'.");
                return;
            }
            var json = File.ReadAllText(_revelarLeaderboardPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                _revelarLeaderboard = new Dictionary<ulong, int>();
                Console.WriteLine("Leaderboard file was empty, initialized new leaderboard.");
                return;
            }
            _revelarLeaderboard = System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, int>>(json) ?? new Dictionary<ulong, int>();
            Console.WriteLine($"Loaded leaderboard with {_revelarLeaderboard.Count} entries.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading leaderboard: {ex.Message}");
            _revelarLeaderboard = new Dictionary<ulong, int>();
        }
    }

    // Saves the /revelar leaderboard to a JSON file. Handles errors gracefully.
    private static void SaveRevelarLeaderboard()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_revelarLeaderboardPath))
                _revelarLeaderboardPath = "revelar_leaderboard.json";
            var json = System.Text.Json.JsonSerializer.Serialize(_revelarLeaderboard, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_revelarLeaderboardPath, json, Encoding.UTF8);
            Console.WriteLine($"Leaderboard saved to '{_revelarLeaderboardPath}'.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving leaderboard: {ex.Message}");
        }
    }

    public async Task StartAsync()
    {
        _client.Log += LogAsync;
        _client.ReactionAdded += ReactionAddedAsync;
        _client.Ready += ReadyAsync;
        _client.InteractionCreated += InteractionCreated;
        _client.MessageReceived += MessageReceived;

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
        Console.WriteLine($"PREGUNTAR_PRICE: {_preguntarPrice}");
        Console.WriteLine($"MEME_PRICE: {_memePrice}");
        Console.WriteLine($"REACTION_INCREMENT: {_reactionIncrement}");
        
        ScheduleMonthlyRedistribution(int.Parse(Environment.GetEnvironmentVariable("CREDIT_PERCENTAGE") ?? throw new InvalidOperationException()));
        Console.WriteLine($"CREDIT_PERCENTAGE:" + int.Parse(Environment.GetEnvironmentVariable("CREDIT_PERCENTAGE") ?? throw new InvalidOperationException()));
        
        // Schedule daily task
        ScheduleDailyTask();
        Console.WriteLine($"DAILY_TASK_TIME: {_dailyTaskTime}");
        Console.WriteLine($"DAILY_TASK_REWARD: {_dailyTaskReward}");

        // Schedule monthly leaderboard announcement
        ScheduleMonthlyLeaderboardAnnouncement();
    }

    // Schedules leaderboard announcement on the first day of each month at 00:05
    private void ScheduleMonthlyLeaderboardAnnouncement()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                DateTime now = DateTime.Now;
                DateTime nextRun = new DateTime(now.Year, now.Month, 1, 0, 5, 0); // 00:05 first day of month
                if (now >= nextRun)
                {
                    // If after the time, schedule for next month
                    nextRun = nextRun.AddMonths(1);
                }
                else if (now.Day != 1 || now.TimeOfDay > new TimeSpan(0,5,0))
                {
                    // If not the first day or past 00:05, move to next month
                    nextRun = new DateTime(now.Year, now.Month, 1, 0, 5, 0).AddMonths(1);
                }
                TimeSpan waitTime = nextRun - now;
                Console.WriteLine($"Monthly leaderboard announcement scheduled for: {nextRun:yyyy-MM-dd HH:mm:ss} (in {waitTime.TotalMinutes:F1} minutes)");
                await Task.Delay(waitTime);
                try
                {
                    await SendLeaderboardAnnouncementAsync(true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending leaderboard announcement: {ex.Message}");
                }
            }
        });
    }

    // Helper to get previous month name in Spanish
    private string GetPreviousMonthNameSpanish()
    {
        var now = DateTime.Now;
        int prevMonth = now.Month - 1;
        int year = now.Year;
        if (prevMonth == 0)
        {
            prevMonth = 12;
            year -= 1;
        }
        var prevMonthDate = new DateTime(year, prevMonth, 1);
        return prevMonthDate.ToString("MMMM", new System.Globalization.CultureInfo("es-ES"));
    }

    // Sends the leaderboard as an embed with ASCII table formatting
    // If resetAfterSend is true, reset and save the leaderboard after sending
    private async Task SendLeaderboardAnnouncementAsync(bool resetAfterSend = false)
    {
        var channelIdStr = Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "";
        if (!ulong.TryParse(channelIdStr, out var channelId))
        {
            Console.WriteLine("TARGET_CHANNEL_ID not set or invalid. Skipping leaderboard announcement.");
            return;
        }
        var targetChannel = _client.GetChannel(channelId) as IMessageChannel;
        if (targetChannel == null)
        {
            Console.WriteLine($"Could not find target channel with ID: {channelId}");
            return;
        }
        if (_revelarLeaderboard.Count == 0)
        {
            await targetChannel.SendMessageAsync(":trophy: No leaderboard data for this month!");
            return;
        }
        // Sort leaderboard by points descending, then by user id for tie-break
        var sorted = _revelarLeaderboard.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
        int pageSize = 10;
        int pageCount = (int)Math.Ceiling(sorted.Count / (double)pageSize);
        for (int page = 0; page < pageCount; page++)
        {
            var pageEntries = sorted.Skip(page * pageSize).Take(pageSize).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("```");
            for (int i = 0; i < pageEntries.Count; i++)
            {
                var entry = pageEntries[i];
                int rank = page + 1 + i + page * (pageSize - 1);
                string username = await GetUsernameOrMention(entry.Key);
                sb.AppendLine($"#{rank} - {username} ({entry.Value} puntos)");
            }
            sb.AppendLine("```");
            var embed = new EmbedBuilder()
                .WithTitle($":trophy: Clasificacion de {GetPreviousMonthNameSpanish()}")
                .WithDescription(sb.ToString())
                .WithColor(Color.Gold)
                .Build();
            await targetChannel.SendMessageAsync(embed: embed);
        }
        // Grant first-place reward before clearing leaderboard
        if (sorted.Count > 0)
        {
            var firstPlace = sorted[0];
            ulong firstPlaceUserId = firstPlace.Key;
            int firstPlacePoints = firstPlace.Value;
            int rewardCredits = 0;
            int.TryParse(Environment.GetEnvironmentVariable("FIRST_PLACE_REWARD"), out rewardCredits);
            if (rewardCredits > 0)
            {
                if (_userReactionCounts.ContainsKey(firstPlaceUserId))
                {
                    _userReactionCounts[firstPlaceUserId] += rewardCredits;
                }
                else
                {
                    _userReactionCounts[firstPlaceUserId] = rewardCredits;
                }
                SaveData();
            }
            // --- VOTING PAYOUT LOGIC ---
            decimal voteMultiplier = 1;
            decimal.TryParse(Environment.GetEnvironmentVariable("VOTE_MULTIPLIER"), out voteMultiplier);
            decimal majorityVoteMultiplier = voteMultiplier;
            decimal.TryParse(Environment.GetEnvironmentVariable("MAJORITY_VOTE_MULTIPLIER"), out majorityVoteMultiplier);
            LoadVotes();
            var thisMonthVotes = _votes.Where(v => v.Timestamp.Month == DateTime.Now.Month && v.Timestamp.Year == DateTime.Now.Year).ToList();
            var correctVotes = thisMonthVotes.Where(v => v.VotedForId == firstPlaceUserId).ToList();
            decimal usedMultiplier = voteMultiplier;
            bool isMajority = (thisMonthVotes.Count > 0 && correctVotes.Count > thisMonthVotes.Count / 2);
            if (isMajority)
                usedMultiplier = majorityVoteMultiplier;
            if (correctVotes.Count > 0)
            {
                foreach (var vote in correctVotes)
                {
                    int extra = (int)Math.Round(vote.BetAmount * (usedMultiplier - 1), 0, MidpointRounding.AwayFromZero);
                    int total = (int)Math.Round(vote.BetAmount * usedMultiplier, 0, MidpointRounding.AwayFromZero);
                    if (_userReactionCounts.ContainsKey(vote.VoterId))
                        _userReactionCounts[vote.VoterId] += extra; // they already paid the bet, so just add the extra
                    else
                        _userReactionCounts[vote.VoterId] = total;
                }
                SaveData();
                var winnerMentions = string.Join(", ", correctVotes.Select(v => $"<@{v.VoterId}>").Distinct());
                string multiplierType = isMajority ? "mayoría" : "normal";
                await targetChannel.SendMessageAsync($":moneybag: ¡Las apuestas correctas han sido multiplicadas por {usedMultiplier:0.##} ({multiplierType})! Ganadores: {winnerMentions}");
                _votes.Clear();
                SaveVotes();
            }
            string firstPlaceUsername = await GetUsernameOrMention(firstPlaceUserId);
            await targetChannel.SendMessageAsync($":gift: ¡{firstPlaceUsername} ha ganado el premio por terminar en el primer puesto de la clasificacion! (+{rewardCredits} créditos)");
        }
        if (resetAfterSend)
        {
            _revelarLeaderboard.Clear();
            SaveRevelarLeaderboard();
            Console.WriteLine("Leaderboard has been reset after monthly announcement.");
        }
    }

    // Helper to resolve username (with discriminator if available)
    private async Task<string> GetUsernameOrMention(ulong userId)
    {
        var user = _client.GetUser(userId);
        if (user != null)
            return user.Username;
        // fallback: try to fetch from guild
        try
        {
            var guild = _client.GetGuild(_guildId);
            if (guild != null)
            {
                var member = guild.GetUser(userId);
                if (member != null)
                    return member.Username;
            }
        }
        catch { }
        // fallback: try REST API
        try
        {
            var restUser = await _client.Rest.GetUserAsync(userId);
            if (restUser != null)
                return restUser.Username;
        }
        catch { }
        // fallback: plain id
        return $"{userId}";
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

        // Download all guild members after bot is ready
        try
        {
            var guild = _client.GetGuild(_guildId);
            if (guild != null)
            {
                Console.WriteLine($"[Ready] Downloading all users for guild {_guildId}...");
                await guild.DownloadUsersAsync();
                Console.WriteLine("[Ready] All users downloaded.");
            }
            else
            {
                Console.WriteLine($"[Ready] Guild not found for ID {_guildId}, cannot download users.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ready] Error downloading users: {ex.Message}");
        }
    }

    private async Task RegisterSlashCommands()
    {
        var redeemRecuerdateCommand = new SlashCommandBuilder()
            .WithName("recuerdate")
            .WithDescription($"Canjea una recompensa 'Recuerdate' ({_recuerdatePrice})")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cantidad")
                .WithDescription($"Cantidad de 'Recuerdate' a canjear ({_recuerdatePrice} cada)")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer));
        
        var redeemRecuerdateGuildCommand = redeemRecuerdateCommand.Build();
        await _client.Rest.CreateGuildCommand(redeemRecuerdateGuildCommand, _guildId);
        Console.WriteLine("Slash command 'recuerdate' registered for the guild.");
        
        var redeemMemeCommand = new SlashCommandBuilder()
            .WithName("meme")
            .WithDescription($"Canjea una recompensa 'Recuerdate' version meme ({_memePrice})" )
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cantidad")
                .WithDescription($"Cantidad de 'Recuerdate' version meme a canjear ({_memePrice} cada)")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer));
        
        var redeemMemeGuildCommand = redeemMemeCommand.Build();
        await _client.Rest.CreateGuildCommand(redeemMemeGuildCommand, _guildId);
        Console.WriteLine("Slash command 'meme' registered for the guild.");
        
        var addCreditsCommand = new SlashCommandBuilder()
            .WithName("añadir")
            .WithDescription("Añade créditos a un usuario (solo admin)")
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
        
        var addCreditsGuildCommand = addCreditsCommand.Build();
        await _client.Rest.CreateGuildCommand(addCreditsGuildCommand, _guildId);
        Console.WriteLine("Slash command 'añadir' registered for the guild.");
        
        var removeCreditsCommand = new SlashCommandBuilder()
            .WithName("descontar")
            .WithDescription("Descuenta créditos a un usuario (solo admin)")
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
        
        var removeCreditsGuildCommand = removeCreditsCommand.Build();
        await _client.Rest.CreateGuildCommand(removeCreditsGuildCommand, _guildId);
        Console.WriteLine("Slash command 'descontar' registered for the guild.");
        
        var requestChatbotCommand = new SlashCommandBuilder()
            .WithName("preguntar")
            .WithDescription($"Realiza una pregunta a el espejismo de un usuario ({_preguntarPrice})")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("Usuario al que preguntar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.User))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("pregunta")
                .WithDescription("Pregunta a realizar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.String));
        
        var requestChatbotGuildCommand = requestChatbotCommand.Build();
        await _client.Rest.CreateGuildCommand(requestChatbotGuildCommand, _guildId);
        Console.WriteLine("Slash command 'preguntar' registered for the guild.");

        var dailyQuizCommand = new SlashCommandBuilder()
            .WithName("revelar")
            .WithDescription($"Revela a el usuario que compartio la imagen")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("Usuario a revelar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.User));
        
        var dailyQuizGuildCommand = dailyQuizCommand.Build();
        await _client.Rest.CreateGuildCommand(dailyQuizGuildCommand, _guildId);
        Console.WriteLine($"Slash command 'revelar' registered for the guild.");

        var voteCommand = new SlashCommandBuilder()
            .WithName("votar")
            .WithDescription("Vota por el usuario que crees que ganará y apuesta créditos")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("Usuario a votar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.User))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cantidad")
                .WithDescription("Cantidad de créditos a apostar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer));
        
        var voteGuildCommand = voteCommand.Build();
        await _client.Rest.CreateGuildCommand(voteGuildCommand, _guildId);
        Console.WriteLine($"Slash command 'votar' registered for the guild.");

        var giftCommand = new SlashCommandBuilder()
            .WithName("regalar")
            .WithDescription($"Regala créditos a un usuario")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("Usuario a regalar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.User))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cantidad")
                .WithDescription("Cantidad de créditos a regalar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer));
        
        var giftGuildCommand = giftCommand.Build();
        await _client.Rest.CreateGuildCommand(giftGuildCommand, _guildId);
        Console.WriteLine($"Slash command 'regalar' registered for the guild.");

        var checkCreditsCommand = new SlashCommandBuilder()
            .WithName("saldo")
            .WithDescription("Comprueba tu saldo disponible");
        
        var checkCreditsGuildCommand = checkCreditsCommand.Build();
        await _client.Rest.CreateGuildCommand(checkCreditsGuildCommand, _guildId);
        Console.WriteLine("Slash command 'saldo' registered for the guild.");

        // Manual leaderboard trigger (admin only)
        var leaderboardCommand = new SlashCommandBuilder()
            .WithName("leaderboard")
            .WithDescription("Envía el leaderboard manualmente (solo admin)");
        var leaderboardGuildCommand = leaderboardCommand.Build();
        await _client.Rest.CreateGuildCommand(leaderboardGuildCommand, _guildId);
        Console.WriteLine("Slash command 'leaderboard' registered for the guild.");

        // Retar challenge command
        var retarCommand = new SlashCommandBuilder()
            .WithName("retar")
            .WithDescription("Reta a otro usuario a una apuesta con imagen")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("Usuario a retar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.User))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("creditos")
                .WithDescription("Cantidad de créditos a apostar")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer));
        
        var retarGuildCommand = retarCommand.Build();
        await _client.Rest.CreateGuildCommand(retarGuildCommand, _guildId);
        Console.WriteLine("Slash command 'retar' registered for the guild.");

        // Accept challenge command
        var aceptoCommand = new SlashCommandBuilder()
            .WithName("acepto")
            .WithDescription("Acepta el reto pendiente dirigido a ti");
        
        var aceptoGuildCommand = aceptoCommand.Build();
        await _client.Rest.CreateGuildCommand(aceptoGuildCommand, _guildId);
        Console.WriteLine("Slash command 'acepto' registered for the guild.");

        // Reject challenge command
        var rechazoCommand = new SlashCommandBuilder()
            .WithName("rechazo")
            .WithDescription("Rechaza el reto pendiente dirigido a ti");
        
        var rechazoGuildCommand = rechazoCommand.Build();
        await _client.Rest.CreateGuildCommand(rechazoGuildCommand, _guildId);
        Console.WriteLine("Slash command 'rechazo' registered for the guild.");

        // Guess challenge command
        var adivinoCommand = new SlashCommandBuilder()
            .WithName("adivino")
            .WithDescription("Adivina quien compartio la imagen en tu reto activo")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("Usuario que crees que compartio la imagen")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.User));
        
        var adivinoGuildCommand = adivinoCommand.Build();
        await _client.Rest.CreateGuildCommand(adivinoGuildCommand, _guildId);
        Console.WriteLine("Slash command 'adivino' registered for the guild.");
    }
    
    private void ScheduleMonthlyRedistribution(decimal percentage)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                DateTime now = DateTime.Now;
                int lastDay = DateTime.DaysInMonth(now.Year, now.Month);
                int penultimateDay = lastDay - 1;
                DateTime nextRun = new DateTime(now.Year, now.Month, penultimateDay, 23, 59, 59);

                TimeSpan waitTime = nextRun - now;
                Console.WriteLine($"{waitTime}%");
                await Task.Delay(waitTime);

                // Send votation day message to target channel
                try
                {
                    var channelIdStr = Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "";
                    if (!ulong.TryParse(channelIdStr, out var channelId))
                    {
                        Console.WriteLine("TARGET_CHANNEL_ID not set or invalid. Skipping votation day announcement.");
                    }
                    else
                    {
                        var targetChannel = _client.GetChannel(channelId) as IMessageChannel;
                        if (targetChannel == null)
                        {
                            Console.WriteLine($"Could not find target channel with ID: {channelId}");
                        }
                        else
                        {
                            await targetChannel.SendMessageAsync(":ballot_box: ¡Hoy es dia de votacion! ¡Participa y vota por quien crees que ganara este mes!");
                            Console.WriteLine("Votation day announcement sent.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending votation day announcement: {ex.Message}");
                }

                // RedistributeWealth(percentage); // Redistribution disabled
            }
        });
    }
    
    private bool IsQuizFreezePeriod()
    {
        var today = DateTime.Now;
        int lastDay = DateTime.DaysInMonth(today.Year, today.Month);
        return today.Day == lastDay;
    }

private void ScheduleDailyTask()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                DateTime now = DateTime.Now;
                
                // Parse the time from environment variable (format: HH:MM)
                TimeSpan targetTime;
                if (!TimeSpan.TryParseExact(_dailyTaskTime, @"hh\:mm", null, out targetTime) && 
                    !TimeSpan.TryParseExact(_dailyTaskTime, @"h\:mm", null, out targetTime))
                {
                    Console.WriteLine($"Invalid DAILY_TASK_TIME format: '{_dailyTaskTime}'. Expected HH:MM format. Using default 20:00.");
                    targetTime = new TimeSpan(20, 0, 0); // Default to 20:00
                }
                
                // Calculate next run time
                DateTime nextRun = now.Date.Add(targetTime);
                
                // If the time has already passed today, schedule for tomorrow
                if (nextRun <= now)
                {
                    nextRun = nextRun.AddDays(1);
                }
                
                TimeSpan waitTime = nextRun - now;
                Console.WriteLine($"Daily task scheduled for: {nextRun:yyyy-MM-dd HH:mm:ss} (in {waitTime.TotalMinutes:F1} minutes)");
                await Task.Delay(waitTime);
                
                // Execute the daily task
                Console.WriteLine($"Executing daily task: SendPostRequestAsync with reward '{_dailyTaskReward}'");
                if (!IsQuizFreezePeriod())
                {
                    await SendPostRequestAsync(_dailyTaskReward);
                }
                else
                {
                    Console.WriteLine("Quiz image posting is frozen until the first of the month.");
                }
            }   
        });
    }
    
    private static readonly HttpClient Client = new HttpClient();

    public async Task<string?> SendPostRequestAsync(string reward)
    {
        try
        {
            var url = $"{_apiUrl}{reward}";
            Console.WriteLine(url);

            var jsonData = "{ \"yourField\": \"value\" }";
            var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            var response = await Client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine("Response: " + responseBody);

            // Parse JSON and extract 'uploader'
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("uploader", out var uploaderProp))
                {
                    this._uploader = uploaderProp.GetString();
                    this._revelarTriedUsers.Clear();
                    this._revelarCorrectUsers.Clear();
                    SaveQuizState(); // Now persist the uploader and cleared lists to disk
                    Console.WriteLine("Uploader: " + _uploader);
                    return this._uploader;
                }
                else
                {
                    Console.WriteLine("No 'uploader' property found in response.");
                    return null;
                }
            }
            catch (Exception jsonEx)
            {
                Console.WriteLine($"Error parsing JSON: {jsonEx.Message}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return null;
        }
    }
    
    public static async Task SendChatBotRequestAsync(string requestedUser)
    {
            // Build the chatbot API URL using variables for the base and key parameter
            var apiUrl = $"{_apiChatUrl}{requestedUser}&key={_safeKey}";
            
            Console.WriteLine(apiUrl);
            
            var response = await Client.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine("Response: " + responseBody);
    }

    // Separate method for retar challenges that doesn't interfere with /revelar
    public async Task<string?> SendRetarImageAsync()
    {
        try
        {
            var url = $"{_apiUrl}image";
            Console.WriteLine($"[RETAR] Sending image request: {url}");

            var jsonData = "{ \"yourField\": \"value\" }";
            var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            var response = await Client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[RETAR] Response: {responseBody}");

            // Parse JSON and extract 'uploader' for this specific challenge
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("uploader", out var uploaderProp))
                {
                    var uploader = uploaderProp.GetString();
                    Console.WriteLine($"[RETAR] Challenge uploader: {uploader}");
                    return uploader;
                }
                else
                {
                    Console.WriteLine("[RETAR] No 'uploader' property found in response.");
                    return null;
                }
            }
            catch (Exception jsonEx)
            {
                Console.WriteLine($"[RETAR] Error parsing JSON: {jsonEx.Message}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RETAR] Error: {ex.Message}");
            return null;
        }
    }
    
    private async Task InteractionCreated(SocketInteraction interaction)
    {
        if (interaction is SocketSlashCommand command)
        {
            if (command.Data.Name == "preguntar")
            {
                var requestedUser = command.Data.Options.First(opt => opt.Name == "usuario").Value.ToString();
                var pregunta = command.Data.Options.First(opt => opt.Name == "pregunta").Value.ToString();
                var commanduser = command.User;

                if (commanduser != null && requestedUser == "outerwinnie" || requestedUser == "otromono" || requestedUser == "esguille" ||requestedUser == "falsatortuga" || requestedUser == "potajito" || requestedUser == "deparki" || requestedUser == "casinocaster" )
                {
                    LoadData();
                    
                    var userId = commanduser!.Id;
                    var reactionsReceived = GetUserReactionCount(userId);
                    if (reactionsReceived >= _preguntarPrice)
                    {
                        // Subtract the _preguntarPrice from reactionsReceived
                        reactionsReceived -= _preguntarPrice;
                        
                        Console.WriteLine(reactionsReceived);
                    
                        // Update the reaction count
                        _userReactionCounts[userId] = reactionsReceived;
                    
                        // Write the updated count to the CSV file
                        SaveData();
                        
                        // Sending a message to a specific channel
                        var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? ""); // Replace with your channel ID if not using env var
                        var targetChannel = _client.GetChannel(channelId) as IMessageChannel;
                        
                        if (targetChannel != null)
                        {
                            
                            await targetChannel.SendMessageAsync($"{commanduser.Mention} ha canjeado una nueva recompensa 'Consulta' por { _preguntarPrice} créditos.");
                            await targetChannel.SendMessageAsync($"**Pregunta** a {requestedUser}: " + pregunta);

                            // Respond to the interaction
                            await command.RespondAsync("Créditos restantes: " + reactionsReceived, ephemeral: true);
                        
                            await SendChatBotRequestAsync(requestedUser);
                        }
                    }
                    else
                    {
                        
                        await command.RespondAsync($"No tienes suficiente credito social. Necesitas {_preguntarPrice} creditos.", ephemeral: true);
                    }
                }
                else
                {
                    await command.RespondAsync($"Usuario no disponible.", ephemeral: true);
                }
            }
            
            else if (command.Data.Name == "leaderboard")
            {
                ulong authorizedUserId = _adminId;
                if (command.User.Id != authorizedUserId)
                {
                    await command.RespondAsync("No tienes permiso para usar este comando.", ephemeral: true);
                    return;
                }
                await command.DeferAsync(ephemeral: true); // Defer immediately
                await SendLeaderboardAnnouncementAsync();
                await command.FollowupAsync(":trophy: Leaderboard enviado al canal.", ephemeral: true);
            }
            else if (command.Data.Name == "añadir")
            {
                var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "usuario");
                var amountOption = command.Data.Options.FirstOrDefault(o => o.Name == "cantidad");

                // Define the authorized user ID (replace with the actual user ID)
                ulong authorizedUserId = _adminId; // Replace with the actual Discord user ID of the authorized user

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
                        await command.RespondAsync($"Se han añadido {amount} créditos al usuario <@{userId}>. Créditos actuales: {_userReactionCounts[userId]}", ephemeral: false);
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
                ulong authorizedUserId = _adminId; // Replace with the actual Discord user ID of the authorized user

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
                        await command.RespondAsync($"Se han descontado {amount} créditos al usuario <@{userId}>. Créditos actuales: {_userReactionCounts[userId]}", ephemeral: false);
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

            else if (command.Data.Name == "saldo")
            {
                var userId = command.User.Id;
                var reactionsReceived = GetUserReactionCount(userId);
                await command.RespondAsync($"Posees {reactionsReceived} créditos.", ephemeral: true);
            }

            else if (command.Data.Name == "revelar")
            {
                if (IsQuizFreezePeriod())
                {
                    await command.RespondAsync(":snowflake: El juego volvera mañana. No se pueden enviar nuevas imágenes. Ahora es el turno de las votaciones.", ephemeral: true);
                    return;
                }

                if (_uploader == string.Empty)
                {
                    await command.RespondAsync("La imagen aun no ha sido enviada, espera a que se envie y vuelve a intentarlo.", ephemeral: true);
                    return;
                }
                
                var userId = command.User.Id;
                if (_revelarTriedUsers.Contains(userId) || _revelarCorrectUsers.Contains(userId))
                {
                    await command.RespondAsync("Ya has intentado revelar al posteador de esta imagen.", ephemeral: true);
                    return;
                }
                _revelarTriedUsers.Add(userId);
                Console.WriteLine($"[DEBUG] User {userId} added to _revelarTriedUsers. Saving quiz state...");
                SaveQuizState();
                Console.WriteLine($"[DEBUG] Quiz state saved after user {userId} failed quiz.");
                var choosenUser = command.Data.Options.First(opt => opt.Name == "usuario").Value.ToString();

                if (_uploader == choosenUser)
                {
                    if (_revelarCorrectUsers.Count >= 3)
                    {
                        await command.RespondAsync("Ya hay 3 ganadores para esta ronda. Espera la siguiente imagen para participar de nuevo.", ephemeral: true);
                        return;
                    }

                    int reward;
                    if (_revelarCorrectUsers.Count == 0)
                        reward = _dailyQuizReward_1;
                    else if (_revelarCorrectUsers.Count == 1)
                        reward = _dailyQuizReward_2;
                    else
                        reward = _dailyQuizReward_3;

                    _revelarCorrectUsers.Add(userId);

                    await command.RespondAsync($"<@{userId}> ¡Correcto! Has ganado {reward} créditos.");

                    if (userId != 0)
                    {
                        LoadData();
                        // Add credits to the user
                        if (!_userReactionCounts.ContainsKey(userId))
                        {
                            _userReactionCounts[userId] = 0;
                        }
                        _userReactionCounts[userId] += reward;
                        SaveData(); // Save updated data to CSV

                        // Update leaderboard
                        if (_revelarLeaderboard.ContainsKey(userId))
                            _revelarLeaderboard[userId]++;
                        else
                            _revelarLeaderboard[userId] = 1;
                        SaveRevelarLeaderboard();
                        SaveQuizState(); // Save quiz state after correct answer
                    }

                    // After rewarding, check if this was the third winner
                    if (_revelarCorrectUsers.Count == 3)
                    {
                        // Announce new round and send new image
                        var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "");
                        var targetChannel = _client.GetChannel(channelId) as IMessageChannel;
                        if (targetChannel != null)
                        {
                            await targetChannel.SendMessageAsync($":tada: ¡Se han alcanzado 3 ganadores! La respuesta correcta era: \"{_uploader}\". Comienza una nueva ronda...");
                        }
                        if (!IsQuizFreezePeriod())
                        {
                            await SendPostRequestAsync("image");
                        }
                        else
                        {
                            if (targetChannel != null)
                            {
                                await targetChannel.SendMessageAsync(":snowflake: El juego volvera mañana. No se pueden enviar nuevas imágenes. Ahora es el turno de las votaciones.");
                            }
                        }
                        SaveQuizState(); // Save after new round/image
                    }
                }
                else
                {
                    await command.RespondAsync($"<@{userId}> ¡Incorrecto!");
                }
            }
            
            else if (command.Data.Name == "recuerdate")
            {
                if (IsQuizFreezePeriod())
                {
                    await command.RespondAsync(":snowflake: El juego volvera mañana. No se pueden enviar nuevas imágenes. Ahora es el turno de las votaciones.", ephemeral: true);
                    return;
                }

                var amountOption = command.Data.Options.FirstOrDefault(o => o.Name == "cantidad");
                
                // Default multiplier is 1 if "cantidad" is not provided or invalid
                int multiplier = 1;

                if (amountOption != null && int.TryParse(amountOption.Value?.ToString(), out int parsedMultiplier))
                {
                    multiplier = parsedMultiplier;
                }
                
                //Load updated count of the CSV file.
                LoadData();

                var totalprice = _recuerdatePrice * multiplier;

                var userId = command.User.Id;
                var reactionsReceived = GetUserReactionCount(userId);
                if (reactionsReceived >= totalprice)
                {
                    // Subtract the _recuerdatePrice from reactionsReceived
                    reactionsReceived -= totalprice;
                    
                    // Update the reaction count
                    _userReactionCounts[userId] = reactionsReceived;
                    
                    // Write the updated count to the CSV file
                    SaveData();

                    // Respond to the interaction
                    await command.RespondAsync("Créditos restantes: " + reactionsReceived, ephemeral: true);
                        
                    // Sending a message to a specific channel
                    var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? ""); // Replace with your channel ID if not using env var
                    var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

                    if (targetChannel != null)
                    {
                        // Sending a message to the specific channel and tagging the user
                        var userMention = command.User.Mention; // This will mention the user who used the option
                        await targetChannel.SendMessageAsync($"{userMention} ha canjeado {multiplier} 'Recuerdate' por {totalprice} créditos.");
                    }
                    else
                    {
                        Console.WriteLine($"Could not find the target channel with ID: {channelId}");
                    }

                    // Run SendPostRequestAsync as many times as specified by the multiplier
                    for (int i = 0; i < multiplier; i++)
                    {
                        await SendPostRequestAsync(reward:"image");
                    }
                }
                else
                {
                    await command.RespondAsync($"No tienes suficiente credito social. Necesitas {totalprice} creditos.", ephemeral: true);
                }
            }
            
            else if (command.Data.Name == "votar")
            {
                var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "usuario");
                var betOption = command.Data.Options.FirstOrDefault(o => o.Name == "cantidad" || o.Name == "bet" || o.Name == "apuesta");

                if (userOption == null)
                {
                    await command.RespondAsync("Debes especificar el usuario a votar.", ephemeral: true);
                    return;
                }

                var votedForUser = userOption.Value as SocketUser;
                if (votedForUser == null)
                {
                    await command.RespondAsync("Usuario inválido para votar.", ephemeral: true);
                    return;
                }

                ulong voterId = command.User.Id;
                ulong votedForId = votedForUser.Id;

                if (voterId == votedForId)
                {
                    await command.RespondAsync("No puedes votar por ti mismo.", ephemeral: true);
                    return;
                }

                int betAmount = 1; // Default bet if not present
                if (betOption != null && int.TryParse(betOption.Value?.ToString(), out int parsedBet))
                {
                    betAmount = parsedBet;
                }
                if (betAmount <= 0)
                {
                    await command.RespondAsync("La cantidad apostada debe ser un número positivo.", ephemeral: true);
                    return;
                }

                // Load votes from CSV to ensure up-to-date
                LoadVotes();
                var existingVote = _votes.FirstOrDefault(v => v.VoterId == voterId && v.Timestamp.Month == DateTime.Now.Month && v.Timestamp.Year == DateTime.Now.Year);
                if (existingVote != null)
                {
                    existingVote.VotedForId = votedForId;
                    existingVote.BetAmount = betAmount;
                    existingVote.Timestamp = DateTime.Now;
                }
                else
                {
                    _votes.Add(new VoteRecord
                    {
                        VoterId = voterId,
                        VotedForId = votedForId,
                        BetAmount = betAmount,
                        Timestamp = DateTime.Now
                    });
                }
                SaveVotes();
                await command.RespondAsync($"<@{voterId}> ha votado por <@{votedForId}> con una apuesta de {betAmount}.");
            }
            else if (command.Data.Name == "regalar")
{
    var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "usuario");
    var amountOption = command.Data.Options.FirstOrDefault(o => o.Name == "cantidad");

    if (userOption == null || amountOption == null)
    {
        await command.RespondAsync("No es posible, comprueba los parametros.", ephemeral: true);
        return;
    }

    ulong senderId = command.User.Id;
    ulong recipientId = (userOption.Value as SocketUser)?.Id ?? 0;
    int amount = 0;
    if (!int.TryParse(amountOption.Value?.ToString(), out amount) || amount <= 0)
    {
        await command.RespondAsync("La cantidad debe ser un numero positivo.", ephemeral: true);
        return;
    }
    if (recipientId == 0)
    {
        await command.RespondAsync("Usuario destino invalido.", ephemeral: true);
        return;
    }
    if (senderId == recipientId)
    {
        await command.RespondAsync("No puedes regalarte creditos a ti mismo.", ephemeral: true);
        return;
    }

    LoadData();
    if (!_userReactionCounts.ContainsKey(senderId) || _userReactionCounts[senderId] < amount)
    {
        await command.RespondAsync($"No tienes suficientes creditos para regalar {amount}.", ephemeral: true);
        return;
    }

    // Subtract from sender
    _userReactionCounts[senderId] -= amount;
    // Add to recipient
    if (!_userReactionCounts.ContainsKey(recipientId))
        _userReactionCounts[recipientId] = 0;
    _userReactionCounts[recipientId] += amount;
    SaveData();

    // Confirmation to sender
    await command.RespondAsync($"Has regalado {amount} creditos a <@{recipientId}>. Tu saldo restante: {_userReactionCounts[senderId]}", ephemeral: true);

    // Notify recipient in channel
    var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "");
    var targetChannel = _client.GetChannel(channelId) as IMessageChannel;
    if (targetChannel != null)
    {
        await targetChannel.SendMessageAsync($":gift: <@{senderId}> ha regalado {amount} creditos a <@{recipientId}>!");
    }

    Console.WriteLine($"[REGALAR] {senderId} -> {recipientId} : {amount} creditos");
}
else if (command.Data.Name == "meme")
            {
                var amountOption = command.Data.Options.FirstOrDefault(o => o.Name == "cantidad");
                
                // Default multiplier is 1 if "cantidad" is not provided or invalid
                int multiplier = 1;

                if (amountOption != null && int.TryParse(amountOption.Value?.ToString(), out int parsedMultiplier))
                {
                    multiplier = parsedMultiplier;
                }
                
                //Load updated count of the CSV file.
                LoadData();

                var totalprice = _memePrice * multiplier;

                var userId = command.User.Id;
                var reactionsReceived = GetUserReactionCount(userId);
                if (reactionsReceived >= totalprice)
                {
                    // Subtract the _recuerdatePrice from reactionsReceived
                    reactionsReceived -= totalprice;
                    
                    // Update the reaction count
                    _userReactionCounts[userId] = reactionsReceived;
                    
                    // Write the updated count to the CSV file
                    SaveData();

                    // Respond to the interaction
                    await command.RespondAsync("Créditos restantes: " + reactionsReceived, ephemeral: true);
                        
                    // Sending a message to a specific channel
                    var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? ""); // Replace with your channel ID if not using env var
                    var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

                    if (targetChannel != null)
                    {
                        // Sending a message to the specific channel and tagging the user
                        var userMention = command.User.Mention; // This will mention the user who used the option
                        await targetChannel.SendMessageAsync($"{userMention} ha canjeado {multiplier} 'Recuerdate version meme' por {totalprice} créditos.");
                    }
                    else
                    {
                        Console.WriteLine($"Could not find the target channel with ID: {channelId}");
                    }

                    // Run SendPostRequestAsync as many times as specified by the multiplier
                    for (int i = 0; i < multiplier; i++)
                    {
                        await SendPostRequestAsync(reward:"meme");
                    }
                }
                else
                {
                    await command.RespondAsync($"No tienes suficiente credito social. Necesitas {totalprice} creditos.", ephemeral: true);
                }
            }
            else if (command.Data.Name == "retar")
            {
                var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "usuario");
                var creditosOption = command.Data.Options.FirstOrDefault(o => o.Name == "creditos");

                if (userOption == null || creditosOption == null)
                {
                    await command.RespondAsync("Faltan argumentos. Debes especificar usuario y créditos.", ephemeral: true);
                    return;
                }

                var challengedUser = userOption.Value as SocketUser;
                if (challengedUser == null)
                {
                    await command.RespondAsync("Usuario inválido.", ephemeral: true);
                    return;
                }

                if (!int.TryParse(creditosOption.Value?.ToString(), out int betAmount) || betAmount <= 0)
                {
                    await command.RespondAsync("La cantidad de créditos debe ser un número positivo.", ephemeral: true);
                    return;
                }

                ulong challengerId = command.User.Id;
                ulong challengedId = challengedUser.Id;

                if (challengerId == challengedId)
                {
                    await command.RespondAsync("No puedes retarte a ti mismo.", ephemeral: true);
                    return;
                }

                // Check if challenger has enough credits
                LoadData();
                if (!_userReactionCounts.ContainsKey(challengerId) || _userReactionCounts[challengerId] < betAmount)
                {
                    await command.RespondAsync($"No tienes suficientes créditos. Necesitas {betAmount} créditos para esta apuesta.", ephemeral: true);
                    return;
                }

                // Check if challenged user has enough credits
                if (!_userReactionCounts.ContainsKey(challengedId) || _userReactionCounts[challengedId] < betAmount)
                {
                    await command.RespondAsync($"<@{challengedId}> no tiene suficientes créditos para aceptar esta apuesta.", ephemeral: true);
                    return;
                }

                // Check for existing active challenges between these users
                var existingChallenge = _activeRetarChallenges.Values.FirstOrDefault(c => 
                    (c.ChallengerId == challengerId && c.ChallengedId == challengedId) ||
                    (c.ChallengerId == challengedId && c.ChallengedId == challengerId));

                if (existingChallenge != null && !existingChallenge.IsCompleted)
                {
                    await command.RespondAsync("Ya existe un reto activo entre ustedes. Completen el reto actual antes de crear uno nuevo.", ephemeral: true);
                    return;
                }

                // Create new challenge
                string challengeId = Guid.NewGuid().ToString();
                var challenge = new RetarChallenge
                {
                    ChallengeId = challengeId,
                    ChallengerId = challengerId,
                    ChallengedId = challengedId,
                    BetAmount = betAmount,
                    CreatedAt = DateTime.Now,
                    IsAccepted = false,
                    IsCompleted = false
                };

                _activeRetarChallenges[challengeId] = challenge;
                SaveRetarChallenges();

                // Send challenge message to target channel
                var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "");
                var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

                if (targetChannel != null)
                {
                    var embed = new EmbedBuilder()
                        .WithTitle("🎯 ¡Nuevo Reto!")
                        .WithDescription($"<@{challengerId}> ha retado a <@{challengedId}> a una apuesta de **{betAmount} créditos**!")
                        .WithColor(Color.Orange)
                        .AddField("💰 Apuesta", $"{betAmount} créditos", true)
                        .AddField("⏰ Expira en", "1 hora", true)
                        .AddField("📝 Instrucciones", 
                            $"<@{challengedId}> puede usar `/acepto` o `/rechazo` para responder", false)
                        .WithFooter($"ID del reto: {challengeId}")
                        .WithTimestamp(DateTimeOffset.Now)
                        .Build();

                    await targetChannel.SendMessageAsync(embed: embed);
                }

                await command.RespondAsync($"¡Reto enviado! <@{challengedId}> tiene 1 hora para aceptar o rechazar tu desafío de {betAmount} créditos.", ephemeral: true);
                Console.WriteLine($"[RETAR] Challenge created: {challengerId} -> {challengedId} for {betAmount} credits");
            }
            else if (command.Data.Name == "acepto")
            {
                var userId = command.User.Id;
                
                // Find active challenge where this user is the challenged party
                var challenge = _activeRetarChallenges.Values.FirstOrDefault(c => 
                    c.ChallengedId == userId && !c.IsAccepted && !c.IsCompleted);

                if (challenge == null)
                {
                    await command.RespondAsync("No tienes ningún reto pendiente para aceptar.", ephemeral: true);
                    return;
                }

                // Verify both users still have enough credits
                LoadData();
                if (!_userReactionCounts.ContainsKey(challenge.ChallengerId) || _userReactionCounts[challenge.ChallengerId] < challenge.BetAmount)
                {
                    await command.RespondAsync("El retador ya no tiene suficientes créditos.", ephemeral: true);
                    _activeRetarChallenges.Remove(challenge.ChallengeId);
                    SaveRetarChallenges();
                    return;
                }

                if (!_userReactionCounts.ContainsKey(challenge.ChallengedId) || _userReactionCounts[challenge.ChallengedId] < challenge.BetAmount)
                {
                    await command.RespondAsync("No tienes suficientes créditos para aceptar este reto.", ephemeral: true);
                    return;
                }

                // Accept the challenge and deduct credits from both users
                _userReactionCounts[challenge.ChallengerId] -= challenge.BetAmount;
                _userReactionCounts[challenge.ChallengedId] -= challenge.BetAmount;
                SaveData();

                challenge.IsAccepted = true;
                SaveRetarChallenges();

                await command.DeferAsync();

                try
                {
                    // Send image for the challenge using separate method that doesn't affect /revelar
                    var imageUploader = await SendRetarImageAsync();
                    challenge.ImageUrl = imageUploader; // Store the uploader info
                    SaveRetarChallenges();

                    var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "");
                    var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

                    if (targetChannel != null)
                    {
                        var embed = new EmbedBuilder()
                            .WithTitle("🎯 ¡Reto Aceptado!")
                            .WithDescription($"<@{challenge.ChallengedId}> ha aceptado el reto de <@{challenge.ChallengerId}>!")
                            .WithColor(Color.Green)
                            .AddField("💰 Apuesta Total", $"{challenge.BetAmount * 2} créditos", true)
                            .AddField("🎮 Reglas", 
                                "• Solo los participantes pueden adivinar\n" +
                                "• Cada uno tiene 2 intentos\n" +
                                "• El ganador se lleva todos los créditos\n" +
                                "• Si ambos fallan, los créditos se pierden", false)
                            .AddField("📝 Cómo jugar", 
                                "Usen `/adivino [respuesta]` para participar", false)
                            .WithFooter($"ID del reto: {challenge.ChallengeId}")
                            .WithTimestamp(DateTimeOffset.Now)
                            .Build();

                        await targetChannel.SendMessageAsync(embed: embed);
                    }

                    await command.FollowupAsync("¡Reto aceptado! Se ha enviado una imagen al canal. ¡Que comience la competencia!", ephemeral: true);
                    Console.WriteLine($"[RETAR] Challenge accepted: {challenge.ChallengeId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending challenge image: {ex.Message}");
                    // Refund credits if image sending fails
                    _userReactionCounts[challenge.ChallengerId] += challenge.BetAmount;
                    _userReactionCounts[challenge.ChallengedId] += challenge.BetAmount;
                    SaveData();
                    
                    _activeRetarChallenges.Remove(challenge.ChallengeId);
                    SaveRetarChallenges();
                    
                    await command.FollowupAsync("Error al enviar la imagen del reto. Se han reembolsado los créditos.", ephemeral: true);
                }
            }
            else if (command.Data.Name == "rechazo")
            {
                var userId = command.User.Id;
                
                // Find active challenge where this user is the challenged party
                var challenge = _activeRetarChallenges.Values.FirstOrDefault(c => 
                    c.ChallengedId == userId && !c.IsAccepted && !c.IsCompleted);

                if (challenge == null)
                {
                    await command.RespondAsync("No tienes ningún reto pendiente para rechazar.", ephemeral: true);
                    return;
                }

                // Remove the challenge
                _activeRetarChallenges.Remove(challenge.ChallengeId);
                SaveRetarChallenges();

                var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "");
                var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

                if (targetChannel != null)
                {
                    await targetChannel.SendMessageAsync($"❌ <@{challenge.ChallengedId}> ha rechazado el reto de <@{challenge.ChallengerId}>.");
                }

                await command.RespondAsync("Has rechazado el reto.", ephemeral: true);
                Console.WriteLine($"[RETAR] Challenge rejected: {challenge.ChallengeId}");
            }
            else if (command.Data.Name == "adivino")
            {
                var userId = command.User.Id;
                var guessedUser = (IUser)command.Data.Options.First().Value;
                var guessedUsername = guessedUser.Username;

                // Find active challenge where this user is a participant
                var challenge = _activeRetarChallenges.Values.FirstOrDefault(c => 
                    (c.ChallengerId == userId || c.ChallengedId == userId) && 
                    c.IsAccepted && !c.IsCompleted);

                if (challenge == null)
                {
                    await command.RespondAsync("No tienes ningún reto activo para adivinar.", ephemeral: true);
                    return;
                }

                // Check if user has attempts left
                if (userId == challenge.ChallengerId)
                {
                    if (challenge.ChallengerAttempts >= 2)
                    {
                        await command.RespondAsync("Ya has usado todos tus intentos.", ephemeral: true);
                        return;
                    }
                    challenge.ChallengerAttempts++;
                }
                else
                {
                    if (challenge.ChallengedAttempts >= 2)
                    {
                        await command.RespondAsync("Ya has usado todos tus intentos.", ephemeral: true);
                        return;
                    }
                    challenge.ChallengedAttempts++;
                }

                SaveRetarChallenges();

                // Check answer by comparing with the uploader stored in challenge.ImageUrl
                try
                {
                    bool isCorrect = challenge.ImageUrl != null && challenge.ImageUrl.Equals(guessedUsername, StringComparison.OrdinalIgnoreCase);

                    var channelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "");
                    var targetChannel = _client.GetChannel(channelId) as IMessageChannel;

                    if (isCorrect)
                    {
                        // User wins - transfer credits
                        LoadData();
                        _userReactionCounts[userId] += challenge.BetAmount * 2;
                        SaveData();

                        challenge.IsCompleted = true;
                        challenge.WinnerId = userId;
                        SaveRetarChallenges();

                        if (targetChannel != null)
                        {
                            var embed = new EmbedBuilder()
                                .WithTitle("🎉 ¡Reto Completado!")
                                .WithDescription($"<@{userId}> ha adivinado correctamente!")
                                .WithColor(Color.Green)
                                .AddField("🏆 Ganador", $"<@{userId}>", true)
                                .AddField("💰 Premio", $"{challenge.BetAmount * 2} créditos", true)
                                .AddField("✅ Respuesta Correcta", $"@{guessedUsername}", false)
                                .WithFooter($"ID del reto: {challenge.ChallengeId}")
                                .WithTimestamp(DateTimeOffset.Now)
                                .Build();

                            await targetChannel.SendMessageAsync(embed: embed);
                        }

                        await command.RespondAsync($"¡Correcto! Has ganado {challenge.BetAmount * 2} créditos.", ephemeral: true);
                        Console.WriteLine($"[RETAR] Challenge won by {userId}: {challenge.ChallengeId}");
                    }
                    else
                    {
                        // Wrong answer
                        int attemptsLeft = userId == challenge.ChallengerId ? 
                            (2 - challenge.ChallengerAttempts) : (2 - challenge.ChallengedAttempts);

                        if (targetChannel != null)
                        {
                            await targetChannel.SendMessageAsync($"❌ <@{userId}> falló. Adivinó: @{guessedUsername}. Le quedan {attemptsLeft} intentos.");
                        }

                        await command.RespondAsync($"Incorrecto. Te quedan {attemptsLeft} intentos.", ephemeral: true);

                        // Check if both players have used all attempts
                        if (challenge.ChallengerAttempts >= 2 && challenge.ChallengedAttempts >= 2)
                        {
                            challenge.IsCompleted = true;
                            SaveRetarChallenges();

                            if (targetChannel != null)
                            {
                                var embed = new EmbedBuilder()
                                    .WithTitle("💸 Reto Fallido")
                                    .WithDescription("Ambos jugadores han fallado todas sus oportunidades.")
                                    .WithColor(Color.Red)
                                    .AddField("💔 Resultado", "Nadie gana", true)
                                    .AddField("💸 Créditos perdidos", $"{challenge.BetAmount * 2} créditos", true)
                                    .AddField("✅ Respuesta Correcta", $"@{challenge.ImageUrl ?? "Desconocida"}", false)
                                    .WithFooter($"ID del reto: {challenge.ChallengeId}")
                                    .WithTimestamp(DateTimeOffset.Now)
                                    .Build();

                                await targetChannel.SendMessageAsync(embed: embed);
                            }

                            Console.WriteLine($"[RETAR] Challenge failed by both players: {challenge.ChallengeId}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error checking answer: {ex.Message}");
                    await command.RespondAsync("Error al verificar la respuesta. Inténtalo de nuevo.", ephemeral: true);
                }
            }
        }
        
        // Handle message-based challenge responses
        if (interaction is SocketMessageComponent component)
        {
            // Handle button interactions for challenge acceptance/rejection if needed
        }
    }

    private async Task MessageReceived(SocketMessage message)
    {
        // Ignore messages from bots
        if (message.Author.IsBot) return;

        // Only process messages in the target channel
        var targetChannelId = ulong.Parse(Environment.GetEnvironmentVariable("TARGET_CHANNEL_ID") ?? "");
        if (message.Channel.Id != targetChannelId) return;

        var content = message.Content.ToLower().Trim();
        var userId = message.Author.Id;

        // Handle challenge acceptance
        if (content.StartsWith("acepto "))
        {
            var challengeId = content.Substring(7).Trim();
            await HandleChallengeAcceptance(message, challengeId, userId);
        }
        // Handle challenge rejection
        else if (content.StartsWith("rechazo "))
        {
            var challengeId = content.Substring(8).Trim();
            await HandleChallengeRejection(message, challengeId, userId);
        }
        // Handle guessing (format: "adivino [challengeId] [answer]")
        else if (content.StartsWith("adivino "))
        {
            var parts = content.Substring(8).Split(' ', 2);
            if (parts.Length >= 2)
            {
                var challengeId = parts[0].Trim();
                var answer = parts[1].Trim();
                await HandleChallengeGuess(message, challengeId, userId, answer);
            }
        }
    }

    private async Task HandleChallengeAcceptance(SocketMessage message, string challengeId, ulong userId)
    {
        if (!_activeRetarChallenges.ContainsKey(challengeId))
        {
            await message.Channel.SendMessageAsync("Reto no encontrado o ya expirado.");
            return;
        }

        var challenge = _activeRetarChallenges[challengeId];
        if (userId != challenge.ChallengedId)
        {
            await message.Channel.SendMessageAsync("Solo el usuario retado puede aceptar este desafío.");
            return;
        }

        if (challenge.IsAccepted)
        {
            await message.Channel.SendMessageAsync("Este reto ya ha sido aceptado.");
            return;
        }

        // Verify both users still have enough credits
        LoadData();
        if (!_userReactionCounts.ContainsKey(challenge.ChallengerId) || _userReactionCounts[challenge.ChallengerId] < challenge.BetAmount)
        {
            await message.Channel.SendMessageAsync("El retador ya no tiene suficientes créditos.");
            _activeRetarChallenges.Remove(challengeId);
            SaveRetarChallenges();
            return;
        }

        if (!_userReactionCounts.ContainsKey(challenge.ChallengedId) || _userReactionCounts[challenge.ChallengedId] < challenge.BetAmount)
        {
            await message.Channel.SendMessageAsync("No tienes suficientes créditos para aceptar este reto.");
            return;
        }

        // Accept the challenge and deduct credits from both users
        _userReactionCounts[challenge.ChallengerId] -= challenge.BetAmount;
        _userReactionCounts[challenge.ChallengedId] -= challenge.BetAmount;
        SaveData();

        challenge.IsAccepted = true;
        SaveRetarChallenges();

        try
        {
            // Send image for the challenge
            var imageUploader = await SendPostRequestAsync("image");
            challenge.ImageUrl = imageUploader; // Store the uploader info
            SaveRetarChallenges();

            var embed = new EmbedBuilder()
                .WithTitle("🎯 ¡Reto Aceptado!")
                .WithDescription($"<@{challenge.ChallengedId}> ha aceptado el reto de <@{challenge.ChallengerId}>!")
                .WithColor(Color.Green)
                .AddField("💰 Apuesta Total", $"{challenge.BetAmount * 2} créditos", true)
                .AddField("🎮 Reglas", 
                    "• Solo los participantes pueden adivinar\n" +
                    "• Cada uno tiene 2 intentos\n" +
                    "• El ganador se lleva todos los créditos\n" +
                    "• Si ambos fallan, los créditos se pierden", false)
                .AddField("📝 Cómo jugar", 
                    $"Usen 'adivino {challengeId} [respuesta]' para participar", false)
                .WithFooter($"ID del reto: {challengeId}")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await message.Channel.SendMessageAsync(embed: embed);
            Console.WriteLine($"[RETAR] Challenge accepted: {challengeId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending challenge image: {ex.Message}");
            // Refund credits if image sending fails
            _userReactionCounts[challenge.ChallengerId] += challenge.BetAmount;
            _userReactionCounts[challenge.ChallengedId] += challenge.BetAmount;
            SaveData();
            
            _activeRetarChallenges.Remove(challengeId);
            SaveRetarChallenges();
            
            await message.Channel.SendMessageAsync("Error al enviar la imagen del reto. Se han reembolsado los créditos.");
        }
    }

    private async Task HandleChallengeRejection(SocketMessage message, string challengeId, ulong userId)
    {
        if (!_activeRetarChallenges.ContainsKey(challengeId))
        {
            await message.Channel.SendMessageAsync("Reto no encontrado o ya expirado.");
            return;
        }

        var challenge = _activeRetarChallenges[challengeId];
        if (userId != challenge.ChallengedId)
        {
            await message.Channel.SendMessageAsync("Solo el usuario retado puede rechazar este desafío.");
            return;
        }

        if (challenge.IsAccepted)
        {
            await message.Channel.SendMessageAsync("Este reto ya ha sido aceptado y no puede ser rechazado.");
            return;
        }

        // Remove the challenge
        _activeRetarChallenges.Remove(challengeId);
        SaveRetarChallenges();

        await message.Channel.SendMessageAsync($"❌ <@{challenge.ChallengedId}> ha rechazado el reto de <@{challenge.ChallengerId}>.");
        Console.WriteLine($"[RETAR] Challenge rejected: {challengeId}");
    }

    private async Task HandleChallengeGuess(SocketMessage message, string challengeId, ulong userId, string answer)
    {
        if (!_activeRetarChallenges.ContainsKey(challengeId))
        {
            await message.Channel.SendMessageAsync("Reto no encontrado o ya completado.");
            return;
        }

        var challenge = _activeRetarChallenges[challengeId];
        if (!challenge.IsAccepted)
        {
            await message.Channel.SendMessageAsync("Este reto aún no ha sido aceptado.");
            return;
        }

        if (challenge.IsCompleted)
        {
            await message.Channel.SendMessageAsync("Este reto ya ha sido completado.");
            return;
        }

        if (userId != challenge.ChallengerId && userId != challenge.ChallengedId)
        {
            await message.Channel.SendMessageAsync("Solo los participantes del reto pueden adivinar.");
            return;
        }

        // Check attempts
        if (!challenge.GuessAttempts.ContainsKey(userId))
        {
            challenge.GuessAttempts[userId] = 0;
        }

        if (challenge.GuessAttempts[userId] >= 2)
        {
            await message.Channel.SendMessageAsync("Ya has usado tus 2 intentos.");
            return;
        }

        challenge.GuessAttempts[userId]++;
        SaveRetarChallenges();

        // Check if the guess is correct
        bool isCorrect = string.Equals(answer.Trim(), challenge.ImageUrl?.Trim(), StringComparison.OrdinalIgnoreCase);

        if (isCorrect)
        {
            // Winner found!
            challenge.WinnerId = userId;
            challenge.IsCompleted = true;

            // Award all credits to winner
            LoadData();
            if (!_userReactionCounts.ContainsKey(userId))
                _userReactionCounts[userId] = 0;
            
            _userReactionCounts[userId] += challenge.BetAmount * 2;
            SaveData();

            _activeRetarChallenges.Remove(challengeId);
            SaveRetarChallenges();

            var embed = new EmbedBuilder()
                .WithTitle("🎉 ¡Tenemos un Ganador!")
                .WithDescription($"<@{userId}> ha adivinado correctamente!")
                .WithColor(Color.Gold)
                .AddField("🏆 Ganador", $"<@{userId}>", true)
                .AddField("💰 Premio", $"{challenge.BetAmount * 2} créditos", true)
                .AddField("✅ Respuesta Correcta", answer, false)
                .WithFooter($"Reto completado")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await message.Channel.SendMessageAsync(embed: embed);
            Console.WriteLine($"[RETAR] Challenge won by {userId}: {challengeId}");
        }
        else
        {
            int remainingAttempts = 2 - challenge.GuessAttempts[userId];
            
            await message.Channel.SendMessageAsync($"❌ <@{userId}> ha fallado. Le quedan {remainingAttempts} intentos.");

            if (remainingAttempts == 0)
            {
                // Check if both players have used all attempts
                bool bothPlayersExhausted = challenge.GuessAttempts.ContainsKey(challenge.ChallengerId) && 
                                          challenge.GuessAttempts.ContainsKey(challenge.ChallengedId) &&
                                          challenge.GuessAttempts[challenge.ChallengerId] >= 2 &&
                                          challenge.GuessAttempts[challenge.ChallengedId] >= 2;

                if (bothPlayersExhausted)
                {
                    // Both failed - credits are lost
                    challenge.IsCompleted = true;
                    _activeRetarChallenges.Remove(challengeId);
                    SaveRetarChallenges();

                    var embed = new EmbedBuilder()
                        .WithTitle("💸 Reto Fallido")
                        .WithDescription("Ambos jugadores han agotado sus intentos.")
                        .WithColor(Color.Red)
                        .AddField("💔 Resultado", "Los créditos se han perdido", true)
                        .AddField("✅ Respuesta Correcta", challenge.ImageUrl ?? "Desconocida", false)
                        .WithFooter($"Reto completado")
                        .WithTimestamp(DateTimeOffset.Now)
                        .Build();

                    await message.Channel.SendMessageAsync(embed: embed);
                    Console.WriteLine($"[RETAR] Challenge failed by both players: {challengeId}");
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
            targetChannel.SendMessageAsync($"Redistribuidos {amountToRedistribute} creditos del usuario <@{wealthiestUserId}>. Cada usuario recibira {amountPerUser} creditos. https://c.tenor.com/4wo9yEcmBcsAAAAd/tenor.gif");
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
