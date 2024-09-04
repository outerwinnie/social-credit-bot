# Use the official .NET SDK image to build the application
FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build

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
FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS runtime

# Set the working directory
WORKDIR /app

# Copy the built application from the build stage
COPY --from=build /app/publish .

# Set environment variables with default values
ENV DISCORD_BOT_TOKEN=""
ENV CSV_FILE_PATH="/app/user_reactions.csv"
ENV REACTION_INCREMENT="1"

# Set the entry point to your application
ENTRYPOINT ["dotnet", "Social-Credit-Bot.dll"]
