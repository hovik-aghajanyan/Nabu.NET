# Build from the repository root so the project references to src/ resolve:
#   docker build -f samples/Nabu.Sample.ChatHub/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props ./
COPY src/Nabu.Mcp.AspNetCore/Nabu.Mcp.AspNetCore.csproj src/Nabu.Mcp.AspNetCore/
COPY src/Nabu.Mcp.AspNetCore.SignalR/Nabu.Mcp.AspNetCore.SignalR.csproj src/Nabu.Mcp.AspNetCore.SignalR/
COPY samples/Nabu.Sample.ChatHub/Nabu.Sample.ChatHub.csproj samples/Nabu.Sample.ChatHub/
RUN dotnet restore samples/Nabu.Sample.ChatHub/Nabu.Sample.ChatHub.csproj

COPY src/ src/
COPY samples/ samples/
RUN dotnet publish samples/Nabu.Sample.ChatHub/Nabu.Sample.ChatHub.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nabu.Sample.ChatHub.dll"]
