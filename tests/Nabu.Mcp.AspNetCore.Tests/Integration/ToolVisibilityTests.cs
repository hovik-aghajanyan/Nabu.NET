using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// Authenticates whoever the <c>X-Test-User</c> header names, in <c>name</c> or <c>name:role,role</c>
    /// form. Enough of a scheme to drive real <c>[Authorize]</c> attributes and policies.
    /// </summary>
    public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";
        public const string UserHeader = "X-Test-User";

        /// <summary>A header value the handler refuses, standing in for an expired or malformed token.</summary>
        public const string RejectedUser = "!rejected";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers[UserHeader].ToString();
            if (string.IsNullOrEmpty(header))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            if (string.Equals(header, RejectedUser, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.Fail("The credentials were rejected."));
            }

            var parts = header.Split(':');
            var claims = new List<Claim> { new Claim(ClaimTypes.Name, parts[0]) };

            if (parts.Length > 1)
            {
                foreach (var role in parts[1].Split(','))
                {
                    if (!string.IsNullOrWhiteSpace(role))
                    {
                        claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
                    }
                }
            }

            var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    /// <summary>Actions with different authorization demands, used only by <see cref="ToolVisibilityTests"/>.</summary>
    [ApiController]
    [Route("visibility")]
    public class VisibilityController : ControllerBase
    {
        /// <summary>Reachable by anyone.</summary>
        [HttpGet("open")]
        [AllowAnonymous]
        public IActionResult Open() => Ok(new { ok = true });

        /// <summary>Requires an authenticated caller.</summary>
        [HttpGet("secure")]
        [Authorize]
        public IActionResult Secure() => Ok(new { user = User.Identity?.Name });

        /// <summary>Requires the admin role.</summary>
        [HttpGet("admin")]
        [Authorize(Policy = "AdminOnly")]
        public IActionResult Admin() => Ok(new { ok = true });
    }

    /// <summary>
    /// Actions carrying no authorization attributes at all, for an application that secures everything
    /// with <c>AuthorizationOptions.FallbackPolicy</c> and opens individual actions with
    /// <c>[AllowAnonymous]</c>.
    /// </summary>
    [ApiController]
    [Route("fallback")]
    public class FallbackController : ControllerBase
    {
        /// <summary>Carries nothing, so the fallback policy protects it.</summary>
        [HttpGet("plain")]
        public IActionResult Plain() => Ok(new { ok = true });

        /// <summary>Opted out of the fallback policy.</summary>
        [HttpGet("open")]
        [AllowAnonymous]
        public IActionResult Open() => Ok(new { ok = true });
    }

    /// <summary>
    /// A client that has not authorized yet should be advertised the tools it can actually use, and the
    /// rest should appear once it has.
    /// </summary>
    public class ToolVisibilityTests
    {
        private const string Alice = "alice";
        private const string Root = "root:admin";

        private static IHost CreateServer(Action<NabuMcpOptions> configure)
        {
            return CreateServer(typeof(VisibilityController), configure, fallbackPolicy: false);
        }

        private static IHost CreateFallbackServer(Action<NabuMcpOptions> configure)
        {
            return CreateServer(typeof(FallbackController), configure, fallbackPolicy: true);
        }

        /// <summary>
        /// Hosts an in-memory application. WebHostBuilder is deprecated (ASPDEPR004), so the server is
        /// built on the generic host; the returned <see cref="IHost"/> owns the TestServer and must be
        /// disposed by the caller.
        /// </summary>
        private static IHost CreateServer(Type controller, Action<NabuMcpOptions> configure, bool fallbackPolicy)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services
                            .AddControllers()
                            .AddApplicationPart(typeof(VisibilityController).Assembly)
                            .OnlyControllers(controller);

                        services
                            .AddAuthentication(TestAuthHandler.SchemeName)
                            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                        services.AddAuthorization(options =>
                        {
                            options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));

                            if (fallbackPolicy)
                            {
                                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
                            }
                        });

                        services.AddNabuMcp(options =>
                        {
                            options.ExposeAllActions = true;

                            // So the replayed request re-authenticates the same way a real one would.
                            options.ForwardedHeaders.Add(TestAuthHandler.UserHeader);

                            configure(options);
                        });
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();

                        // Mounted before UseAuthorization: a fallback policy applies to every request
                        // that matches no endpoint, and the MCP endpoint is middleware rather than an
                        // endpoint, so AuthorizationMiddleware would otherwise challenge it before Nabu
                        // ever saw it. Tool calls still traverse the whole pipeline from the top and
                        // meet the policy at the action.
                        app.UseNabuMcp();

                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    }))
                .Build();

            host.Start();
            return host;
        }

        private static async Task<HttpResponseMessage> PostAsync(
            IHost server,
            string method,
            JsonObject? parameters = null,
            string? user = null)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = method,
            };

            if (parameters != null)
            {
                request["params"] = parameters;
            }

            var client = server.GetTestClient();
            if (user != null)
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
            }

            return await client.PostAsync(
                "/mcp",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        }

        private static async Task<JsonObject> RpcAsync(
            IHost server,
            string method,
            JsonObject? parameters = null,
            string? user = null)
        {
            using var response = await PostAsync(server, method, parameters, user);
            var text = await response.Content.ReadAsStringAsync();

            Assert.False(
                string.IsNullOrWhiteSpace(text),
                "Expected a JSON-RPC response for '" + method + "' but the body was empty (HTTP " + (int)response.StatusCode + ").");

            return JsonNode.Parse(text)!.AsObject();
        }

        private static async Task<string[]> ToolNamesAsync(IHost server, string? user = null)
        {
            var envelope = await RpcAsync(server, "tools/list", user: user);
            return envelope["result"]!["tools"]!.AsArray()
                .Select(t => t!["name"]!.GetValue<string>())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }

        private static Task<JsonObject> CallAsync(IHost server, string tool, string? user = null)
        {
            return RpcAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() },
                user);
        }

        [Fact]
        public async Task Every_tool_is_advertised_by_default()
        {
            using var server = CreateServer(options => { });

            Assert.Equal(
                new[] { "visibility_admin", "visibility_open", "visibility_secure" },
                await ToolNamesAsync(server));
        }

        [Fact]
        public async Task An_unauthorized_caller_sees_only_the_tools_that_need_no_authorization()
        {
            using var server = CreateServer(options => options.ToolVisibility = McpToolVisibility.Authorized);

            Assert.Equal(new[] { "visibility_open" }, await ToolNamesAsync(server));
        }

        [Fact]
        public async Task The_rest_of_the_tools_appear_once_the_caller_has_authorized()
        {
            using var server = CreateServer(options => options.ToolVisibility = McpToolVisibility.Authorized);

            Assert.Equal(new[] { "visibility_open" }, await ToolNamesAsync(server));

            Assert.Equal(
                new[] { "visibility_open", "visibility_secure" },
                await ToolNamesAsync(server, Alice));

            Assert.Equal(
                new[] { "visibility_admin", "visibility_open", "visibility_secure" },
                await ToolNamesAsync(server, Root));
        }

        [Fact]
        public async Task Authenticated_visibility_does_not_look_at_policies()
        {
            using var server = CreateServer(options => options.ToolVisibility = McpToolVisibility.Authenticated);

            Assert.Equal(new[] { "visibility_open" }, await ToolNamesAsync(server));

            // alice cannot call the admin tool, but she is signed in, which is all this mode asks.
            Assert.Contains("visibility_admin", await ToolNamesAsync(server, Alice));
        }

        [Fact]
        public async Task Hiding_a_tool_does_not_replace_the_authorization_the_action_performs()
        {
            using var server = CreateServer(options => options.ToolVisibility = McpToolVisibility.Authorized);

            // The tool is hidden from this caller, but the answer still comes from the pipeline.
            var hidden = await CallAsync(server, "visibility_secure");
            Assert.True(hidden["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("401", hidden["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());

            var allowed = await CallAsync(server, "visibility_secure", Alice);
            Assert.False(allowed["result"]!["isError"]!.GetValue<bool>());

            var refused = await CallAsync(server, "visibility_admin", Alice);
            Assert.True(refused["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("403", refused["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_protected_endpoint_still_rejects_anonymous_callers_outright()
        {
            using var server = CreateServer(options =>
            {
                options.RequireAuthorization = true;
                options.ToolVisibility = McpToolVisibility.Authorized;
            });

            using var response = await PostAsync(server, "tools/list");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Anonymous_discovery_lets_a_client_be_added_before_it_has_credentials()
        {
            using var server = CreateServer(options =>
            {
                options.RequireAuthorization = true;
                options.AnonymousAccess = McpAnonymousAccess.Discovery;
                options.ToolVisibility = McpToolVisibility.Authorized;
            });

            using var initialize = await PostAsync(server, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" });
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

            Assert.Equal(new[] { "visibility_open" }, await ToolNamesAsync(server));

            // Discovery only: calling anything at all still requires credentials.
            using var call = await PostAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = "visibility_open", ["arguments"] = new JsonObject() });
            Assert.Equal(HttpStatusCode.Unauthorized, call.StatusCode);

            Assert.Equal(
                new[] { "visibility_admin", "visibility_open", "visibility_secure" },
                await ToolNamesAsync(server, Root));
        }

        [Fact]
        public async Task Anonymous_access_can_extend_to_the_tools_that_need_no_authorization()
        {
            using var server = CreateServer(options =>
            {
                options.RequireAuthorization = true;
                options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;
                options.ToolVisibility = McpToolVisibility.Authorized;
            });

            var open = await CallAsync(server, "visibility_open");
            Assert.False(open["result"]!["isError"]!.GetValue<bool>());

            // Everything else keeps challenging, so the client knows it has to authorize.
            using var secure = await PostAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = "visibility_secure", ["arguments"] = new JsonObject() });
            Assert.Equal(HttpStatusCode.Unauthorized, secure.StatusCode);

            var authorized = await CallAsync(server, "visibility_secure", Alice);
            Assert.False(authorized["result"]!["isError"]!.GetValue<bool>());
        }

        [Fact]
        public async Task An_unknown_tool_never_slips_through_the_anonymous_door()
        {
            using var server = CreateServer(options =>
            {
                options.RequireAuthorization = true;
                options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;
                options.ToolVisibility = McpToolVisibility.Authorized;
            });

            using var response = await PostAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = "no_such_tool", ["arguments"] = new JsonObject() });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task A_fallback_policy_makes_an_action_without_attributes_protected()
        {
            using var server = CreateFallbackServer(options => options.ToolVisibility = McpToolVisibility.Authorized);

            // fallback_plain carries no attributes at all, so the application's fallback policy applies
            // to it exactly as it would to a browser request; only [AllowAnonymous] opts out.
            Assert.Equal(new[] { "fallback_open" }, await ToolNamesAsync(server));
            Assert.Equal(new[] { "fallback_open", "fallback_plain" }, await ToolNamesAsync(server, Alice));
        }

        [Fact]
        public async Task The_fallback_policy_verdict_matches_what_the_pipeline_does()
        {
            using var server = CreateFallbackServer(options => options.ToolVisibility = McpToolVisibility.Authorized);

            var hidden = await CallAsync(server, "fallback_plain");
            Assert.True(hidden["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("401", hidden["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());

            var advertised = await CallAsync(server, "fallback_open");
            Assert.False(advertised["result"]!["isError"]!.GetValue<bool>());
        }

        [Fact]
        public async Task The_anonymous_door_is_closed_for_an_action_the_fallback_policy_protects()
        {
            using var server = CreateFallbackServer(options =>
            {
                options.RequireAuthorization = true;
                options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;
                options.ToolVisibility = McpToolVisibility.Authorized;
            });

            var open = await CallAsync(server, "fallback_open");
            Assert.False(open["result"]!["isError"]!.GetValue<bool>());

            using var plain = await PostAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = "fallback_plain", ["arguments"] = new JsonObject() });
            Assert.Equal(HttpStatusCode.Unauthorized, plain.StatusCode);
        }

        [Fact]
        public async Task Credentials_that_were_rejected_are_not_treated_as_an_anonymous_caller()
        {
            using var server = CreateServer(options =>
            {
                options.RequireAuthorization = true;
                options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;
                options.ToolVisibility = McpToolVisibility.Authorized;
            });

            using var response = await PostAsync(server, "tools/list", user: TestAuthHandler.RejectedUser);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public void Discovery_records_what_each_action_demands()
        {
            using var server = CreateServer(options => { });

            var tools = server.Services.GetRequiredService<IMcpToolRegistry>().GetTools()
                .ToDictionary(t => t.Name, StringComparer.Ordinal);

            Assert.True(tools["visibility_open"].Authorization.AllowsAnonymous);
            Assert.False(tools["visibility_open"].Authorization.RequiresAuthorization);

            Assert.True(tools["visibility_secure"].Authorization.RequiresAuthorization);
            Assert.NotEmpty(tools["visibility_admin"].Authorization.AuthorizeData);
        }
    }
}
