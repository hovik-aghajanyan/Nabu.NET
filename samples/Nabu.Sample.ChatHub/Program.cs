using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.SignalR;
using Nabu.Sample.ChatHub.Hubs;
using Nabu.Sample.ChatHub.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ChatHistory>();
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

        // Browsers cannot set an Authorization header on a WebSocket, so the SignalR JavaScript
        // client sends its token as ?access_token=... - the standard SignalR setup.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return System.Threading.Tasks.Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

// ---------------------------------------------------------------------------
// The MCP setup: AddNabuMcp() as always, plus AddNabuMcpSignalR() to publish
// the [McpTool]-annotated hub methods into the same catalogue. A hub tool call
// opens a synthetic in-process SignalR connection through the application's own
// hub pipeline, so [Authorize], hub filters and broadcasts all behave exactly
// as they do for the browser clients connected to /hubs/chat.
// ---------------------------------------------------------------------------
builder.Services.AddNabuMcp(options =>
{
    options.ServerName = "nabu-chat-sample";
    options.ServerVersion = "1.0.0";
    options.Path = "/mcp";
    options.Instructions =
        "Chat room tools backed by SignalR. Reading messages is open to everyone; sending " +
        "requires a signed-in caller, and deleting a message requires an administrator. " +
        "Messages sent here are broadcast live to every connected browser.";

    options.RequireAuthorization = true;
    options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;
    options.ToolVisibility = McpToolVisibility.Authorized;
});

builder.Services.AddNabuMcpSignalR();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Mounted after authentication so the MCP endpoint sees the caller's identity.
app.UseNabuMcp();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<PresenceHub>("/hubs/presence");

// An ordinary HTTP tool, published by the core package - it lands in the same catalogue as the
// hub tools above, which is the point: one MCP endpoint, every kind of tool.
app.MapGet("/api/room", (ChatHistory history) => new { messageCount = history.GetRecent(int.MaxValue).Count })
   .McpTool("chat_room_info", "Reports how many messages the chat room currently holds.");

app.Run();

/// <summary>Exposed so the integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program
{
}
