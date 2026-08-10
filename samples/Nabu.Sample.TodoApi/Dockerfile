# Build from the repository root so the project reference to src/ resolves:
#   docker build -f samples/Nabu.Sample.TodoApi/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props ./
COPY src/Nabu.Mcp.AspNetCore/Nabu.Mcp.AspNetCore.csproj src/Nabu.Mcp.AspNetCore/
COPY samples/Nabu.Sample.TodoApi/Nabu.Sample.TodoApi.csproj samples/Nabu.Sample.TodoApi/
RUN dotnet restore samples/Nabu.Sample.TodoApi/Nabu.Sample.TodoApi.csproj

COPY src/ src/
COPY samples/ samples/
RUN dotnet publish samples/Nabu.Sample.TodoApi/Nabu.Sample.TodoApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nabu.Sample.TodoApi.dll"]
