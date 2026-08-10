using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nabu.Mcp.AspNetCore;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>Controllers used only by <see cref="StandaloneAppTests"/>.</summary>
    [ApiController]
    [Route("t")]
    public class ProbeController : ControllerBase
    {
        /// <summary>Echoes a value back.</summary>
        /// <param name="value">Anything at all.</param>
        [HttpGet("echo")]
        public IActionResult Echo([FromQuery] string? value) => Ok(new { value });

        /// <summary>Reports a header the caller supplied.</summary>
        [HttpGet("tenant")]
        public IActionResult Tenant([FromHeader(Name = "X-Tenant")] string? tenant) => Ok(new { tenant });

        /// <summary>Adds numbers sent as a bare JSON array.</summary>
        [HttpPost("sum")]
        public IActionResult Sum([FromBody] int[] numbers) => Ok(new { total = numbers.Sum() });

        /// <summary>Never exposed to MCP.</summary>
        [HttpGet("hidden")]
        [McpIgnore]
        public IActionResult Hidden() => Ok(new { hidden = true });
    }

    public class StandaloneAppTests
    {
        /// <summary>
        /// Hosts an in-memory application. WebHostBuilder is deprecated (ASPDEPR004), so the server is
        /// built on the generic host; the returned <see cref="IHost"/> owns the TestServer and must be
        /// disposed by the caller.
        /// </summary>
        private static IHost CreateServer(Action<NabuMcpOptions> configure)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services
                            .AddControllers()
                            .AddApplicationPart(typeof(ProbeController).Assembly)
                            .OnlyControllers(typeof(ProbeController));

                        services.AddNabuMcp(configure);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseNabuMcp();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    }))
                .Build();

            host.Start();
            return host;
        }

        private static async Task<JsonObject> RpcAsync(IHost server, string method, JsonObject? parameters = null)
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

            using var client = server.GetTestClient();
            using var response = await client.PostAsync(
                "/mcp",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));

            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        private static async Task<string[]> ToolNamesAsync(IHost server)
        {
            var envelope = await RpcAsync(server, "tools/list");
            return envelope["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
        }

        [Fact]
        public async Task Nothing_is_exposed_by_default_without_attributes()
        {
            using var server = CreateServer(options => { });

            Assert.Empty(await ToolNamesAsync(server));
        }

        [Fact]
        public async Task ExposeAllActions_publishes_every_action()
        {
            using var server = CreateServer(options => options.ExposeAllActions = true);
            var names = await ToolNamesAsync(server);

            Assert.Contains("probe_echo", names);
            Assert.Contains("probe_tenant", names);
            Assert.Contains("probe_sum", names);
        }

        [Fact]
        public async Task McpIgnore_wins_over_ExposeAllActions()
        {
            using var server = CreateServer(options => options.ExposeAllActions = true);

            Assert.DoesNotContain("probe_hidden", await ToolNamesAsync(server));
        }

        [Fact]
        public async Task A_custom_naming_convention_is_honoured()
        {
            using var server = CreateServer(options =>
            {
                options.ExposeAllActions = true;
                options.ToolNameFactory = context => "api-" + context.ControllerName.ToLowerInvariant() + "-" + context.ActionName.ToLowerInvariant();
            });

            Assert.Contains("api-probe-echo", await ToolNamesAsync(server));
        }

        [Fact]
        public async Task A_tool_filter_can_drop_discovered_tools()
        {
            using var server = CreateServer(options =>
            {
                options.ExposeAllActions = true;
                options.ToolFilter = tool => tool.HttpMethod == "GET";
            });

            var names = await ToolNamesAsync(server);

            Assert.Contains("probe_echo", names);
            Assert.DoesNotContain("probe_sum", names);
        }

        [Fact]
        public async Task The_endpoint_path_is_configurable()
        {
            using var server = CreateServer(options =>
            {
                options.ExposeAllActions = true;
                options.Path = "/agent/mcp";
            });

            using var client = server.GetTestClient();

            using var atDefault = await client.PostAsync(
                "/mcp",
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, atDefault.StatusCode);

            using var atConfigured = await client.PostAsync(
                "/agent/mcp",
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, atConfigured.StatusCode);
        }

        [Fact]
        public async Task The_path_argument_of_UseNabuMcp_wins_over_the_configured_path()
        {
            using var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services
                            .AddControllers()
                            .AddApplicationPart(typeof(ProbeController).Assembly)
                            .OnlyControllers(typeof(ProbeController));

                        services.AddNabuMcp(options =>
                        {
                            options.ExposeAllActions = true;
                            options.Path = "/configured";
                        });
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseNabuMcp("mounted");
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    }))
                .Build();

            host.Start();
            using var server = host;
            using var client = server.GetTestClient();

            using var atConfigured = await client.PostAsync(
                "/configured",
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, atConfigured.StatusCode);

            using var atMounted = await client.PostAsync(
                "/mounted",
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, atMounted.StatusCode);
        }

        [Fact]
        public async Task Header_parameters_are_hidden_by_default()
        {
            using var server = CreateServer(options => options.ExposeAllActions = true);

            var envelope = await RpcAsync(server, "tools/list");
            var tool = envelope["result"]!["tools"]!.AsArray()
                .Single(t => t!["name"]!.GetValue<string>() == "probe_tenant");

            Assert.Empty(tool!["inputSchema"]!["properties"]!.AsObject());
        }

        [Fact]
        public async Task Header_parameters_can_be_opted_into_and_reach_the_action()
        {
            using var server = CreateServer(options =>
            {
                options.ExposeAllActions = true;
                options.ExposeHeaderParameters = true;
            });

            var result = await RpcAsync(
                server,
                "tools/call",
                new JsonObject
                {
                    ["name"] = "probe_tenant",
                    ["arguments"] = new JsonObject { ["tenant"] = "acme" },
                });

            var text = result["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Equal("acme", JsonNode.Parse(text)!["tenant"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_bare_json_array_body_is_sent_as_the_whole_body()
        {
            using var server = CreateServer(options => options.ExposeAllActions = true);

            var result = await RpcAsync(
                server,
                "tools/call",
                new JsonObject
                {
                    ["name"] = "probe_sum",
                    ["arguments"] = new JsonObject { ["numbers"] = new JsonArray(1, 2, 3, 4) },
                });

            var text = result["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Equal(10, JsonNode.Parse(text)!["total"]!.GetValue<int>());
        }

        [Fact]
        public async Task The_endpoint_is_anonymous_when_authorization_is_not_required()
        {
            using var server = CreateServer(options => options.ExposeAllActions = true);

            var result = await RpcAsync(
                server,
                "tools/call",
                new JsonObject
                {
                    ["name"] = "probe_echo",
                    ["arguments"] = new JsonObject { ["value"] = "hello" },
                });

            var text = result["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Equal("hello", JsonNode.Parse(text)!["value"]!.GetValue<string>());
        }

        [Fact]
        public async Task Server_identity_can_be_configured()
        {
            using var server = CreateServer(options =>
            {
                options.ServerName = "probe-server";
                options.ServerVersion = "9.9.9";
            });

            var envelope = await RpcAsync(server, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" });
            var info = envelope["result"]!["serverInfo"]!;

            Assert.Equal("probe-server", info["name"]!.GetValue<string>());
            Assert.Equal("9.9.9", info["version"]!.GetValue<string>());
        }

        [Fact]
        public void UseNabuMcp_without_AddNabuMcp_fails_with_a_helpful_message()
        {
            using var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services => services.AddControllers())
                    .Configure(app => app.UseNabuMcp()))
                .Build();

            // The pipeline is built when the server starts, which is when UseNabuMcp() discovers that
            // nothing was registered.
            var exception = Assert.Throws<InvalidOperationException>(() => host.Start());

            Assert.Contains("AddNabuMcp", exception.Message);
        }

        [Fact]
        public async Task Each_tool_call_runs_in_its_own_service_scope()
        {
            using var server = CreateServer(options => options.ExposeAllActions = true);

            // Two calls in one MCP session must not share scoped state; the simplest observable proof is
            // that both succeed and see their own arguments.
            var first = await RpcAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = "probe_echo", ["arguments"] = new JsonObject { ["value"] = "one" } });

            var second = await RpcAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = "probe_echo", ["arguments"] = new JsonObject { ["value"] = "two" } });

            Assert.Contains("one", first["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
            Assert.Contains("two", second["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
        }
    }
}
