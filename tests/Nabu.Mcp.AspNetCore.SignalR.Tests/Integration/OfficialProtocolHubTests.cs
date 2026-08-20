using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nabu.Mcp.AspNetCore;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Integration
{
    /// <summary>
    /// Hub tools flow through the official-SDK protocol layer too: the same registry serves
    /// <c>tools/list</c> and the same per-descriptor invoker dispatch serves <c>tools/call</c>,
    /// whichever layer owns the wire protocol.
    /// </summary>
    public class OfficialProtocolHubTests
    {
        public class InlineHub : Hub
        {
            [McpTool]
            public Task<string> Shout(string text) => Task.FromResult(text.ToUpperInvariant());
        }

        private static IHost CreateServer()
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSignalR();
                        services.AddAuthorization();
                        services.AddNabuMcp(options =>
                        {
                            options.ServerName = "official-hub-probe";
                        }).UseOfficialMcpProtocol();
                        services.AddNabuMcpSignalR();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseNabuMcp();
                        app.UseEndpoints(endpoints => endpoints.MapHub<InlineHub>("/hubs/inline"));
                    }))
                .Build();

            host.Start();
            return host;
        }

        private static async Task<JsonObject> RpcAsync(IHost server, string method, JsonObject? parameters)
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
            using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            using var response = await client.SendAsync(message);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.IsSuccessStatusCode,
                method + " answered HTTP " + (int)response.StatusCode + ": " + body);

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

        [Fact]
        public async Task Hub_tools_are_listed_through_the_official_sdk()
        {
            using var server = CreateServer();

            var envelope = await RpcAsync(server, "tools/list", new JsonObject());
            var names = envelope["result"]!["tools"]!.AsArray()
                .Select(tool => tool!["name"]!.GetValue<string>())
                .ToArray();

            Assert.Contains("inline_shout", names);
        }

        [Fact]
        public async Task Hub_tools_are_invocable_through_the_official_sdk()
        {
            using var server = CreateServer();

            var envelope = await RpcAsync(server, "tools/call", new JsonObject
            {
                ["name"] = "inline_shout",
                ["arguments"] = new JsonObject { ["text"] = "quiet" },
            });

            var result = envelope["result"]!.AsObject();
            Assert.False(result["isError"]!.GetValue<bool>());
            Assert.Contains("QUIET", result["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
        }
    }
}
