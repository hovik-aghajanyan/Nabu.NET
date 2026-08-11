using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.OfficialSdk.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<IBookCatalog, InMemoryBookCatalog>();
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
// Nabu discovers the [McpTool] actions, generates their schemas and replays
// each call through the pipeline - exactly as in the Todo sample. The one
// difference is UseOfficialMcpProtocol(): the wire protocol (transport,
// protocol revisions, sessions) is handled by the official ModelContextProtocol
// C# SDK instead of Nabu's built-in layer. The authorization options below
// behave identically on both layers.
// ---------------------------------------------------------------------------
builder.Services.AddNabuMcp(options =>
{
    options.ServerName = "nabu-official-sdk-sample";
    options.ServerVersion = "1.0.0";
    options.Instructions =
        "A small book catalog. Search before adding to avoid duplicates. " +
        "Adding a book requires a signed-in caller; removing one requires an administrator token.";

    // The MCP endpoint requires an authenticated caller. Individual actions are still authorized
    // independently by their own [Authorize] attributes.
    options.RequireAuthorization = true;

    // ...except that a client may be added before it has a token. It then sees only the search
    // tools - Search and Get carry [AllowAnonymous], so those actions are reachable by anyone -
    // and may call them. books_add and books_remove are neither advertised nor callable until it
    // signs in; asking for one answers 401, which is the signal a client needs to authenticate.
    options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;

    // Each caller is advertised what its own credentials reach: anonymous callers see the search
    // tools, alice can also add books, and only an administrator is shown books_remove.
    options.ToolVisibility = McpToolVisibility.Authorized;
})
.UseOfficialMcpProtocol();
// The SDK's transport can be tuned through the optional delegate, for example
// to opt back into its session tracking:
//   .UseOfficialMcpProtocol(transport => transport.Stateless = false);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Same mount point as the built-in layer; the path argument overrides the
// default /mcp route (options.Path works too, the argument wins). Mounted
// after authentication so the MCP endpoint sees the caller's identity.
app.UseNabuMcp("/books/mcp");

app.MapControllers();

app.Run();
