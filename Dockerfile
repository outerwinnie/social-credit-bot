# Use the official .NET SDK image to build the application
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Set the working directory
WORKDIR /app

# Copy the .csproj file and restore any dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy the rest of the application source code
COPY . ./

# Build the application
RUN dotnet publish -c Release -o /app/publish

# Use the official .NET Runtime image to run the application
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# Set the working directory
WORKDIR /app

# Copy the built application from the build stage
COPY --from=build /app/publish .

# Set environment variables with default values
ENV DISCORD_BOT_TOKEN=""
ENV CSV_FILE_PATH="/app/user_reactions.csv"
ENV REACTION_INCREMENT="1"
ENV IGNORED_USERS_FILE_PATH="/app/ignored_users.csv"
ENV REVELAR_LEADERBOARD_PATH="/app/revelar_leaderboard.json"
ENV RECUERDATE_PRICE="15"
ENV MEME_PRICE="20"
ENV TARGET_CHANNEL_ID=""
ENV ADMIN_USER_ID=""
ENV CREDIT_PERCENTAGE="50"
ENV GUILD_ID=""
ENV API_URL=""
ENV PREGUNTAR_PRICE="25"
ENV SAFE_KEY=""
ENV DAILY_TASK_TIME="18:00"
ENV DAILY_TASK_REWARD="image"
ENV DAILY_QUIZ_REWARD_1="35"
ENV DAILY_QUIZ_REWARD_2="25"
ENV DAILY_QUIZ_REWARD_3="15"
ENV API_CHAT_URL=""

# Set the entry point to your application
ENTRYPOINT ["dotnet", "Social-Credit-Bot.dll"]
