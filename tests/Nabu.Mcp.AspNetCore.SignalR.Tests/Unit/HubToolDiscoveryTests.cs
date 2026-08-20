using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Schema;
using Nabu.Mcp.AspNetCore.SignalR;
using Nabu.Mcp.AspNetCore.SignalR.Discovery;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Unit
{
    public class HubToolDiscoveryTests
    {
        public sealed record ChatMessage(Guid Id, string User, string Text);

        public class AnnotatedHub : Hub
        {
            /// <summary>Sends a chat message to everyone.</summary>
            /// <param name="text">The message text.</param>
            [McpTool]
            public Task<ChatMessage> SendMessage(string text, CancellationToken cancellationToken = default)
                => Task.FromResult(new ChatMessage(Guid.NewGuid(), "test", text));

            [McpTool("chat_history", Description = "Recent messages.")]
            public IAsyncEnumerable<ChatMessage> StreamHistory(int count) => throw new NotImplementedException();

            [McpTool]
            public Task Upload(ChannelReader<string> stream) => Task.CompletedTask;

            [McpIgnore]
            [McpTool]
            public Task Hidden() => Task.CompletedTask;

            public Task NotPublished() => Task.CompletedTask;

            public override Task OnConnectedAsync() => Task.CompletedTask;
        }

        [Authorize]
        public class SecuredHub : Hub
        {
            [McpTool]
            public Task Plain() => Task.CompletedTask;

            [AllowAnonymous]
            [McpTool]
            public Task MarkedAnonymous() => Task.CompletedTask;
        }

        public class VariantHub : Hub
        {
            /// <summary>Searches messages.</summary>
            [McpTool]
            [McpTool("messages_recent",
                Title = "Recent messages",
                Description = "Lists recent messages without filtering.",
                ExcludeParameters = new[] { "search" },
                ConstantParameters = new[] { "pageSize=100" })]
            [McpTool("messages_search",
                Title = "Search messages",
                Description = "Searches messages by text.",
                IncludeParameters = new[] { "search", "pageSize" },
                RequiredParameters = new[] { "search" })]
            public Task List(string? search, int pageSize = 20) => Task.CompletedTask;
        }

        [McpTool]
        public class BlanketHub : Hub
        {
            public Task First() => Task.CompletedTask;

            [Authorize(Policy = "AdminOnly")]
            public Task AdminThing() => Task.CompletedTask;
        }

        private static IReadOnlyList<McpToolDescriptor> Discover<THub>(
            Action<NabuMcpSignalROptions>? configure = null,
            Action<NabuMcpOptions>? configureCore = null)
        {
            var coreOptions = new NabuMcpOptions();
            configureCore?.Invoke(coreOptions);
            var options = new NabuMcpSignalROptions();
            configure?.Invoke(options);

            var source = new SignalRHubToolSource(
                endpointDataSource: null,
                Options.Create(coreOptions),
                Options.Create(options),
                new JsonSchemaGenerator(NullXmlDocumentationProvider.Instance),
                NullXmlDocumentationProvider.Instance);

            return source.CreateHubTools(typeof(THub), "/hubs/test", new HashSet<string>(StringComparer.Ordinal));
        }

        [Fact]
        public void Annotated_methods_are_published_with_generated_names()
        {
            var tools = Discover<AnnotatedHub>();

            Assert.Contains(tools, tool => tool.Name == "annotated_send_message");
            Assert.Contains(tools, tool => tool.Name == "chat_history");
        }

        [Fact]
        public void Unannotated_and_ignored_methods_stay_out()
        {
            var tools = Discover<AnnotatedHub>();

            Assert.DoesNotContain(tools, tool => tool.Name.Contains("hidden"));
            Assert.DoesNotContain(tools, tool => tool.Name.Contains("not_published"));
            Assert.DoesNotContain(tools, tool => tool.Name.Contains("on_connected"));
        }

        [Fact]
        public void Client_streaming_methods_are_skipped()
        {
            var tools = Discover<AnnotatedHub>();

            Assert.DoesNotContain(tools, tool => tool.Name.Contains("upload"));
        }

        [Fact]
        public void Cancellation_tokens_are_not_tool_inputs()
        {
            var tools = Discover<AnnotatedHub>();
            var tool = tools.Single(t => t.Name == "annotated_send_message");

            var parameter = Assert.Single(tool.Parameters);
            Assert.Equal("text", parameter.Name);
            Assert.True(parameter.IsRequired);
        }

        [Fact]
        public void Xml_documentation_flows_when_available_and_attribute_description_wins()
        {
            var tools = Discover<AnnotatedHub>();
            var history = tools.Single(t => t.Name == "chat_history");

            Assert.Equal("Recent messages.", history.Description);
        }

        [Fact]
        public void Streaming_return_is_detected()
        {
            var tools = Discover<AnnotatedHub>();

            Assert.True(SignalRHubToolSource.TryGetMetadata(tools.Single(t => t.Name == "chat_history"), out var streaming));
            Assert.True(streaming!.IsStreaming);
            Assert.True(SignalRHubToolSource.TryGetMetadata(tools.Single(t => t.Name == "annotated_send_message"), out var plain));
            Assert.False(plain!.IsStreaming);
        }

        [Fact]
        public void Tools_carry_the_signalr_invoker()
        {
            var tools = Discover<AnnotatedHub>();

            Assert.All(tools, tool => Assert.Equal(typeof(Execution.SignalRHubToolInvoker), tool.InvokerType));
            Assert.All(tools, tool => Assert.Equal(string.Empty, tool.HttpMethod));
        }

        [Fact]
        public void Hub_class_authorize_gates_every_method_even_allow_anonymous_ones()
        {
            var tools = Discover<SecuredHub>();

            var plain = tools.Single(t => t.Name == "secured_plain");
            var anonymous = tools.Single(t => t.Name == "secured_marked_anonymous");

            Assert.True(plain.Authorization.RequiresAuthorization);
            Assert.False(plain.Authorization.AllowsAnonymous);

            // Real SignalR semantics: a caller that cannot connect cannot call any method, so the
            // class-level [Authorize] wins over the method's [AllowAnonymous].
            Assert.True(anonymous.Authorization.RequiresAuthorization);
            Assert.False(anonymous.Authorization.AllowsAnonymous);
        }

        [Fact]
        public void Class_level_attribute_publishes_every_method()
        {
            var tools = Discover<BlanketHub>();

            Assert.Contains(tools, tool => tool.Name == "blanket_first");
            var admin = tools.Single(t => t.Name == "blanket_admin_thing");
            Assert.True(admin.Authorization.RequiresAuthorization);
        }

        [Fact]
        public void Expose_all_hub_methods_publishes_unannotated_methods()
        {
            var tools = Discover<AnnotatedHub>(o => o.ExposeAllHubMethods = true);

            Assert.Contains(tools, tool => tool.Name == "annotated_not_published");
            Assert.DoesNotContain(tools, tool => tool.Name.Contains("hidden"));
        }

        [Fact]
        public void One_hub_method_publishes_several_tools_with_their_own_parameter_sets()
        {
            var tools = Discover<VariantHub>();

            Assert.Equal(3, tools.Count);

            // The unnamed variant: the full method, unchanged.
            var full = tools.Single(t => t.Name == "variant_list");
            Assert.Equal(new[] { "search", "pageSize" }, full.Parameters.Select(p => p.Name).ToArray());
            Assert.DoesNotContain(full.Parameters, p => p.IsRequired);

            // Excluded input hidden (falls back to its default), constant pinned and gone from the schema.
            var recent = tools.Single(t => t.Name == "messages_recent");
            Assert.Equal("Recent messages", recent.Annotations.Title);
            Assert.Empty(recent.Parameters);
            var constant = Assert.Single(recent.Constants);
            Assert.Equal("pageSize", constant.BindingName);

            // Whitelisted inputs only, with one promoted to required.
            var search = tools.Single(t => t.Name == "messages_search");
            Assert.Equal(new[] { "search", "pageSize" }, search.Parameters.Select(p => p.Name).ToArray());
            Assert.True(search.Parameters.Single(p => p.Name == "search").IsRequired);
            Assert.False(search.Parameters.Single(p => p.Name == "pageSize").IsRequired);
        }

        [Fact]
        public void Input_schema_lists_required_parameters()
        {
            var tools = Discover<AnnotatedHub>();
            var tool = tools.Single(t => t.Name == "annotated_send_message");

            var properties = Assert.IsType<JsonObject>(tool.InputSchema["properties"]);
            Assert.True(properties.ContainsKey("text"));
            var required = Assert.IsType<JsonArray>(tool.InputSchema["required"]);
            Assert.Contains("text", required.Select(node => node!.GetValue<string>()));
        }
    }
}
