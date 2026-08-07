using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// Hosts the sample application in memory and exposes small helpers for driving its MCP endpoint.
    /// Shared across a test class so the application is started once.
    /// </summary>
    public class McpTestFixture : WebApplicationFactory<Program>
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
            var id = System.Threading.Interlocked.Increment(ref _nextId);
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

        /// <summary>Calls a tool and returns the <c>result</c> object, failing if the server sent an error.</summary>
        public async Task<JsonObject> CallToolAsync(
            string name,
            JsonObject arguments,
            AuthenticationHeaderValue? token = null)
        {
            var envelope = await RpcAsync(
                "tools/call",
                new JsonObject { ["name"] = name, ["arguments"] = arguments },
                token);

            var error = envelope["error"];
            Assert.True(error == null, "tools/call for '" + name + "' returned a JSON-RPC error: " + error?.ToJsonString());

            return envelope["result"]!.AsObject();
        }

        /// <summary>The text content of a tool result.</summary>
        public static string TextOf(JsonObject result)
        {
            return result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        }

        /// <summary>The text content of a tool result, parsed as JSON.</summary>
        public static JsonNode JsonOf(JsonObject result)
        {
            return JsonNode.Parse(TextOf(result))!;
        }

        public static bool IsError(JsonObject result)
        {
            return result["isError"]?.GetValue<bool>() ?? false;
        }
    }

    [CollectionDefinition(Name)]
    public class McpTestCollection : ICollectionFixture<McpTestFixture>
    {
        public const string Name = "mcp-sample-app";
    }
}
