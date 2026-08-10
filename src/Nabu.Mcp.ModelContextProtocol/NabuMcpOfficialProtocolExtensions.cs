using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Server;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registration for serving Nabu's tools through the official MCP C# SDK.</summary>
    public static class NabuMcpOfficialProtocolExtensions
    {
        /// <summary>
        /// Replaces Nabu's built-in MCP protocol layer with the official
        /// <c>ModelContextProtocol</c> SDK. Call it after <c>AddNabuMcp()</c>:
        /// <code>
        /// services.AddNabuMcp().UseOfficialMcpProtocol();
        /// </code>
        /// <c>app.UseNabuMcp()</c> then mounts the SDK's Streamable HTTP endpoint at the
        /// configured path instead of the built-in middleware. The SDK owns the wire protocol -
        /// transport, sessions, protocol revisions - while Nabu keeps doing controller discovery,
        /// schema generation, authorization-aware tool visibility and pipeline replay.
        /// </summary>
        /// <remarks>
        /// The transport defaults to stateless mode, matching Nabu's own design: any request can
        /// land on any server instance. Opt back into the SDK's session-tracking via
        /// <paramref name="configureTransport"/> if a deployment needs it.
        /// <see cref="NabuMcpOptions.RequireAuthorization"/> with
        /// <see cref="McpAnonymousAccess.None"/> is honoured by requiring authorization on the
        /// mapped endpoint; the partial <see cref="NabuMcpOptions.AnonymousAccess"/> modes leave
        /// the endpoint itself anonymous, and every tool call is still authorized by the
        /// application's own pipeline when it is replayed.
        /// </remarks>
        /// <param name="services">The service collection <c>AddNabuMcp()</c> was called on.</param>
        /// <param name="configureTransport">Optional adjustments to the SDK's HTTP transport.</param>
        public static IServiceCollection UseOfficialMcpProtocol(
            this IServiceCollection services,
            Action<HttpServerTransportOptions>? configureTransport = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (!services.Any(descriptor => descriptor.ServiceType == typeof(McpPipelineProvider)))
            {
                throw new InvalidOperationException(
                    "UseOfficialMcpProtocol() must be called after AddNabuMcp() on the same service collection.");
            }

            services.AddHttpContextAccessor();
            services.TryAddSingleton<Nabu.Mcp.ModelContextProtocol.NabuOfficialMcpBridge>();
            services.TryAddSingleton<INabuMcpProtocolMount, Nabu.Mcp.ModelContextProtocol.OfficialMcpProtocolMount>();

            services.AddMcpServer()
                .WithHttpTransport(transport =>
                {
                    transport.Stateless = true;
                    configureTransport?.Invoke(transport);
                })
                .WithListToolsHandler((context, cancellationToken) =>
                    Bridge(context).ListToolsAsync(cancellationToken))
                .WithCallToolHandler((context, cancellationToken) =>
                    Bridge(context).CallToolAsync(context.Params!, cancellationToken));

            services.AddOptions<McpServerOptions>().Configure<IOptions<NabuMcpOptions>>((mcpOptions, nabuOptions) =>
            {
                var nabu = nabuOptions.Value;
                mcpOptions.ServerInfo = new Implementation
                {
                    Name = nabu.ServerName ?? "nabu-mcp",
                    Version = nabu.ServerVersion ?? "1.0.0",
                };

                if (!string.IsNullOrEmpty(nabu.Instructions))
                {
                    mcpOptions.ServerInstructions = nabu.Instructions;
                }
            });

            return services;
        }

        private static Nabu.Mcp.ModelContextProtocol.NabuOfficialMcpBridge Bridge(MessageContext context)
        {
            var provider = context.Services ?? context.Server.Services;
            if (provider == null)
            {
                throw new InvalidOperationException(
                    "The MCP request context carries no service provider; the Nabu bridge cannot resolve its services.");
            }

            return provider.GetRequiredService<Nabu.Mcp.ModelContextProtocol.NabuOfficialMcpBridge>();
        }
    }
}
