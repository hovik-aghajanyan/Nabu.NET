using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
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
        "deleting an item requires an administrator token.";

    // The MCP endpoint itself requires an authenticated caller. Individual actions are still
    // authorized independently by their own [Authorize] attributes.
    options.RequireAuthorization = true;
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Mounted after authentication so the MCP endpoint sees the caller's identity.
app.UseNabuMcp();

app.MapControllers();

app.Run();

/// <summary>Exposed so the integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program
{
}
