using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Integration
{
    /// <summary>
    /// Hosts the chat sample in memory and exposes helpers for driving its MCP endpoint, mirroring
    /// the core test suite's fixture. Shared across a test class so the application starts once.
    /// </summary>
    public class ChatHubTestFixture : WebApplicationFactory<Program>
    {
        private int _nextId = 1;

        /// <summary>Logs in as a sample user and returns an <c>Authorization</c> header value.</summary>
        public async Task<AuthenticationHeaderValue> GetTokenAsync(string username)
        {
            using var client = CreateClient();
            using var response = await client.PostAsync(
                "/api/auth/login",
                new StringContent(
                    "{\"username\":\"" + username + "\",\"password\":\"password\"}",
                    Encoding.UTF8,
                    "application/json"));

            response.EnsureSuccessStatusCode();

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            return new AuthenticationHeaderValue("Bearer", body["accessToken"]!.GetValue<string>());
        }

        /// <summary>Sends a JSON-RPC message to the MCP endpoint and returns the raw HTTP response.</summary>
        public async Task<HttpResponseMessage> PostRawAsync(string json, AuthenticationHeaderValue? token = null)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = token;

            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            return await client.SendAsync(request);
        }

        /// <summary>Sends one JSON-RPC request and returns the parsed response envelope.</summary>
        public async Task<JsonObject> RpcAsync(
            string method,
            JsonObject? parameters = null,
            AuthenticationHeaderValue? token = null)
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
            };

            if (parameters != null)
            {
                request["params"] = parameters;
            }

            using var response = await PostRawAsync(request.ToJsonString(), token);
            var text = await response.Content.ReadAsStringAsync();

            Assert.False(
                string.IsNullOrWhiteSpace(text),
                "Expected a JSON-RPC response for '" + method + "' but the body was empty (HTTP " + (int)response.StatusCode + ").");

            return JsonNode.Parse(text)!.AsObject();
        }

        /// <summary>Lists the tool names advertised to the given caller.</summary>
        public async Task<string[]> ListToolNamesAsync(AuthenticationHeaderValue? token = null)
        {
            var response = await RpcAsync("tools/list", token: token);
            var tools = response["result"]!["tools"]!.AsArray();
            var names = new string[tools.Count];
            for (var i = 0; i < tools.Count; i++)
            {
                names[i] = tools[i]!["name"]!.GetValue<string>();
            }

            return names;
        }

        /// <summary>Calls a tool and returns the JSON-RPC <c>result</c> object.</summary>
        public async Task<JsonObject> CallToolAsync(
            string name,
            JsonObject? arguments = null,
            AuthenticationHeaderValue? token = null)
        {
            var response = await RpcAsync("tools/call", new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments ?? new JsonObject(),
            }, token);

            Assert.True(response["result"] != null, "Expected a tool result but got: " + response.ToJsonString());
            return response["result"]!.AsObject();
        }
    }
}
