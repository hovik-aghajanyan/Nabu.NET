using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.TodoApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<ITodoRepository, InMemoryTodoRepository>();
builder.Services.AddSingleton<TokenService>();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

// ---------------------------------------------------------------------------
// This is the entire MCP setup. Every action carrying [McpTool] is published,
// and each tool call is replayed through the pipeline configured below.
// ---------------------------------------------------------------------------
builder.Services.AddNabuMcp(options =>
{
    options.ServerName = "nabu-todo-sample";
    options.ServerVersion = "1.0.0";
    options.Path = "/mcp";
    options.Instructions =
        "Todo and weather tools. Todo tools act on the signed-in user's own items; " +
        "deleting an item requires an administrator token. Sign in to see the todo tools.";

    // The MCP endpoint requires an authenticated caller. Individual actions are still authorized
    // independently by their own [Authorize] attributes.
    options.RequireAuthorization = true;

    // ...except that a client may be added before it has a token. It then sees only the weather tools -
    // WeatherController carries [AllowAnonymous], so those actions are reachable by anyone - and may
    // call them. The todo tools are neither advertised nor callable until it signs in, because
    // TodosController carries [Authorize]; asking for one answers 401, which is the signal a client
    // needs to go and authenticate.
    options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;

    // Each caller is advertised what its own credentials reach: anonymous callers see the weather
    // tools, alice sees hers as well, and only an administrator is shown todos_delete.
    options.ToolVisibility = McpToolVisibility.Authorized;
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Mounted after authentication so the MCP endpoint sees the caller's identity.
app.UseNabuMcp();

app.MapControllers();

// ---------------------------------------------------------------------------
// Minimal APIs publish the same way - no controller involved. The route handler
// is discovered from its endpoint metadata, and a tool call is replayed through
// the same pipeline as everything else.
// ---------------------------------------------------------------------------
app.MapGet("/api/time", () => new { utcNow = DateTimeOffset.UtcNow })
   .McpTool("server_time_now", "Reports the server's current UTC time.");

// [AsParameters]: the handler gathers its inputs into one record, and each member is published as
// an individual tool input with the same binding inference it would get as a top-level parameter -
// here `timeZone` from the route and `format` from the query string.
app.MapGet("/api/time/{timeZone}", ([AsParameters] TimeInZoneQuery query) =>
{
    TimeZoneInfo zone;
    try
    {
        zone = TimeZoneInfo.FindSystemTimeZoneById(query.TimeZone);
    }
    catch (TimeZoneNotFoundException)
    {
        return Results.NotFound(new { message = "Unknown time zone '" + query.TimeZone + "'." });
    }

    var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
    try
    {
        return Results.Ok(new
        {
            timeZone = zone.Id,
            now = now.ToString(query.Format ?? "O", System.Globalization.CultureInfo.InvariantCulture),
            utcOffset = zone.GetUtcOffset(now).ToString(),
        });
    }
    catch (FormatException)
    {
        return Results.BadRequest(new { message = "Invalid format string '" + query.Format + "'." });
    }
})
.McpTool("server_time_in_zone", "Reports the server's current time in a named time zone.");

app.Run();

/// <summary>Exposed so the integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program
{
}

/// <summary>Inputs of the <c>server_time_in_zone</c> tool, gathered with <c>[AsParameters]</c>.</summary>
/// <param name="TimeZone">IANA or Windows time zone identifier, for example <c>Asia/Yerevan</c>.</param>
/// <param name="Format">Optional .NET date format string. Defaults to round-trip (<c>O</c>).</param>
public record TimeInZoneQuery(string TimeZone, string? Format = null);
