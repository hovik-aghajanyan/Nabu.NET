using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nabu.Mcp.AspNetCore;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// The official-SDK protocol layer: <c>UseOfficialMcpProtocol()</c> hands the wire protocol to
    /// the ModelContextProtocol SDK while Nabu keeps serving discovery, visibility and invocation.
    /// </summary>
    public class OfficialProtocolTests
    {
        private static IHost CreateServer(Action<NabuMcpOptions>? configure = null, string? mountPath = null)
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

                        services.AddNabuMcp(options =>
                        {
                            options.ExposeAllActions = true;
                            options.ServerName = "official-probe";
                            options.ServerVersion = "1.2.3";
                            configure?.Invoke(options);
                        }).UseOfficialMcpProtocol();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseNabuMcp(mountPath);
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    }))
                .Build();

            host.Start();
            return host;
        }

        private static async Task<JsonObject> RpcAsync(IHost server, string method, JsonObject? parameters, string path = "/mcp")
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
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            using var response = await client.SendAsync(message);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.IsSuccessStatusCode,
                method + " at " + path + " answered HTTP " + (int)response.StatusCode + ": " + body);

            return ParseRpcResponse(response.Content.Headers.ContentType?.MediaType, body);
        }

        /// <summary>The SDK may answer with a bare JSON object or a per-request SSE stream.</summary>
        private static JsonObject ParseRpcResponse(string? mediaType, string body)
        {
            if (mediaType != null && mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var data = body
                    .Split('\n')
                    .Select(line => line.TrimEnd('\r'))
                    .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                    .Select(line => line.Substring("data: ".Length))
                    .First();

                return JsonNode.Parse(data)!.AsObject();
            }

            return JsonNode.Parse(body)!.AsObject();
        }

        private static JsonObject InitializeParams()
        {
            return new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "nabu-tests", ["version"] = "1.0.0" },
            };
        }

        [Fact]
        public async Task Initialize_reports_the_identity_configured_through_nabu_options()
        {
            using var server = CreateServer();

            var envelope = await RpcAsync(server, "initialize", InitializeParams());
            var info = envelope["result"]!["serverInfo"]!;

            Assert.Equal("official-probe", info["name"]!.GetValue<string>());
            Assert.Equal("1.2.3", info["version"]!.GetValue<string>());
        }

        [Fact]
        public async Task Tools_discovered_by_nabu_are_listed_through_the_official_sdk()
        {
            using var server = CreateServer();

            // No prior initialize on this request: the transport defaults to stateless mode.
            var envelope = await RpcAsync(server, "tools/list", new JsonObject());
            var names = envelope["result"]!["tools"]!.AsArray()
                .Select(tool => tool!["name"]!.GetValue<string>())
                .ToArray();

            Assert.Contains("probe_echo", names);
            Assert.Contains("probe_sum", names);
            Assert.DoesNotContain("probe_hidden", names);
        }

        [Fact]
        public async Task Tool_calls_replay_the_application_pipeline()
        {
            using var server = CreateServer();

            var envelope = await RpcAsync(
                server,
                "tools/call",
                new JsonObject
                {
                    ["name"] = "probe_echo",
                    ["arguments"] = new JsonObject { ["value"] = "through-the-sdk" },
                });

            var result = envelope["result"]!.AsObject();
            Assert.NotEqual(true, result["isError"]?.GetValue<bool>());

            var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Equal("through-the-sdk", JsonNode.Parse(text)!["value"]!.GetValue<string>());
        }

        [Fact]
        public async Task An_unknown_tool_is_reported_as_an_error()
        {
            using var server = CreateServer();

            var envelope = await RpcAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = "no_such_tool", ["arguments"] = new JsonObject() });

            // The SDK owns how a handler exception surfaces; depending on version it is either a
            // JSON-RPC error or an isError tool result. Either way the caller must see a failure
            // that names the tool.
            var error = envelope["error"];
            if (error != null)
            {
                Assert.Contains("no_such_tool", error["message"]!.GetValue<string>());
            }
            else
            {
                var result = envelope["result"]!.AsObject();
                Assert.True(result["isError"]?.GetValue<bool>() ?? false, "Expected an error, got: " + envelope.ToJsonString());
                Assert.Contains("no_such_tool", result["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
            }
        }

        [Fact]
        public async Task The_route_passed_to_UseNabuMcp_mounts_the_sdk_endpoint_there()
        {
            using var server = CreateServer(mountPath: "/bridge");

            var envelope = await RpcAsync(server, "tools/list", new JsonObject(), path: "/bridge");
            Assert.NotNull(envelope["result"]!["tools"]);

            using var client = server.GetTestClient();
            using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
            message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            using var atDefault = await client.SendAsync(message);
            Assert.Equal(HttpStatusCode.NotFound, atDefault.StatusCode);
        }
    }
}
