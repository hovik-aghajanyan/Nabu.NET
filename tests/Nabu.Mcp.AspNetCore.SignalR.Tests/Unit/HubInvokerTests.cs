using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using Nabu.Mcp.AspNetCore.Schema;
using Nabu.Mcp.AspNetCore.SignalR.Discovery;
using Nabu.Mcp.AspNetCore.SignalR.Execution;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Unit
{
    public class HubInvokerTests
    {
        public class EchoHub : Hub
        {
            [McpTool]
            public Task<string> Echo(string text) => Task.FromResult("echo:" + text);

            [McpTool]
            public Task Fails() => throw new HubException("deliberate failure");

            [McpTool]
            public async Task<int> ReplyToCaller(string text)
            {
                await Clients.Caller.SendAsync("reply", text.ToUpperInvariant());
                return text.Length;
            }

            [McpTool]
            public async IAsyncEnumerable<int> Countdown(int from, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                for (var i = from; i > 0; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return i;
                    await Task.Yield();
                }
            }
        }

        [Microsoft.AspNetCore.Authorization.Authorize]
        public class GatedHub : Hub
        {
            [McpTool]
            public Task<string> Plain() => Task.FromResult("gated ok");
        }

        [Microsoft.AspNetCore.Authorization.Authorize(Policy = "AdminOnly")]
        public class AdminGatedHub : Hub
        {
            [McpTool]
            public Task<string> Plain() => Task.FromResult("admin ok");
        }

        private static Task<(McpToolInvocationResult Result, McpToolDescriptor Tool)> InvokeAsync(
            string toolName,
            JsonObject arguments,
            Action<NabuMcpSignalROptions>? configure = null)
        {
            return InvokeAsync<EchoHub>(toolName, arguments, configure);
        }

        private static async Task<(McpToolInvocationResult Result, McpToolDescriptor Tool)> InvokeAsync<THub>(
            string toolName,
            JsonObject arguments,
            Action<NabuMcpSignalROptions>? configure = null,
            System.Security.Claims.ClaimsPrincipal? user = null)
            where THub : Hub
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSignalR();
            services.AddAuthorization(options => options.AddPolicy(
                "AdminOnly",
                policy => policy.RequireRole("admin")));
            var provider = services.BuildServiceProvider();

            var options = new NabuMcpSignalROptions();
            configure?.Invoke(options);

            var source = new SignalRHubToolSource(
                endpointDataSource: null,
                Options.Create(new NabuMcpOptions()),
                Options.Create(options),
                new JsonSchemaGenerator(NullXmlDocumentationProvider.Instance),
                NullXmlDocumentationProvider.Instance);

            var tools = source.CreateHubTools(typeof(THub), "/hubs/test", new HashSet<string>(StringComparer.Ordinal));
            var tool = tools.Single(t => t.Name == toolName);

            var context = new DefaultHttpContext { RequestServices = provider };
            if (user != null)
            {
                context.User = user;
            }

            var invoker = new SignalRHubToolInvoker(Options.Create(options));

            var result = await invoker.InvokeAsync(tool, arguments, context, CancellationToken.None);
            return (result, tool);
        }

        private static System.Security.Claims.ClaimsPrincipal Authenticated(string name, params string[] roles)
        {
            var claims = new List<System.Security.Claims.Claim>
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, name),
            };
            foreach (var role in roles)
            {
                claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
            }

            return new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(claims, authenticationType: "Test"));
        }

        [Fact]
        public async Task The_class_gate_refuses_an_anonymous_caller_with_401()
        {
            var (result, _) = await InvokeAsync<GatedHub>("gated_plain", new JsonObject());

            Assert.False(result.IsSuccess);
            Assert.Equal(401, result.StatusCode);
            Assert.Contains("authorized caller", result.Body);
        }

        [Fact]
        public async Task The_class_gate_admits_an_authenticated_caller()
        {
            var (result, _) = await InvokeAsync<GatedHub>(
                "gated_plain", new JsonObject(), user: Authenticated("alice"));

            Assert.True(result.IsSuccess);
            Assert.Equal("\"gated ok\"", result.Body);
        }

        [Fact]
        public async Task A_class_level_policy_refuses_an_authenticated_caller_that_lacks_it_with_403()
        {
            var (result, _) = await InvokeAsync<AdminGatedHub>(
                "admin_gated_plain", new JsonObject(), user: Authenticated("alice", "user"));

            Assert.False(result.IsSuccess);
            Assert.Equal(403, result.StatusCode);
        }

        [Fact]
        public async Task A_class_level_policy_admits_a_caller_that_satisfies_it()
        {
            var (result, _) = await InvokeAsync<AdminGatedHub>(
                "admin_gated_plain", new JsonObject(), user: Authenticated("root", "admin"));

            Assert.True(result.IsSuccess);
            Assert.Equal("\"admin ok\"", result.Body);
        }

        [Fact]
        public async Task Invokes_a_hub_method_and_returns_its_result()
        {
            var (result, _) = await InvokeAsync("echo_echo", new JsonObject { ["text"] = "hi" });

            Assert.True(result.IsSuccess);
            Assert.Equal("\"echo:hi\"", result.Body);
        }

        [Fact]
        public async Task Hub_errors_become_tool_errors()
        {
            var (result, _) = await InvokeAsync("echo_fails", new JsonObject());

            Assert.False(result.IsSuccess);
            Assert.Contains("deliberate failure", result.Body);
        }

        [Fact]
        public async Task Caller_directed_messages_are_captured_into_the_result()
        {
            var (result, _) = await InvokeAsync("echo_reply_to_caller", new JsonObject { ["text"] = "hi" });

            Assert.True(result.IsSuccess);
            var body = JsonNode.Parse(result.Body)!.AsObject();
            Assert.Equal(2, body["result"]!.GetValue<int>());
            var messages = body["callerMessages"]!.AsArray();
            var message = Assert.Single(messages)!.AsObject();
            Assert.Equal("reply", message["method"]!.GetValue<string>());
            Assert.Equal("HI", message["arguments"]![0]!.GetValue<string>());
        }

        [Fact]
        public async Task Streaming_methods_collect_their_items()
        {
            var (result, _) = await InvokeAsync("echo_countdown", new JsonObject { ["from"] = 3 });

            Assert.True(result.IsSuccess);
            var body = JsonNode.Parse(result.Body)!.AsObject();
            var items = body["streamItems"]!.AsArray().Select(node => node!.GetValue<int>()).ToArray();
            Assert.Equal(new[] { 3, 2, 1 }, items);
        }

        [Fact]
        public async Task Streams_beyond_the_cap_are_truncated()
        {
            var (result, _) = await InvokeAsync(
                "echo_countdown",
                new JsonObject { ["from"] = 100 },
                o => o.MaxStreamItems = 5);

            Assert.True(result.IsSuccess);
            var body = JsonNode.Parse(result.Body)!.AsObject();
            Assert.Equal(5, body["streamItems"]!.AsArray().Count);
            Assert.True(body["truncated"]!.GetValue<bool>());
        }
    }
}
