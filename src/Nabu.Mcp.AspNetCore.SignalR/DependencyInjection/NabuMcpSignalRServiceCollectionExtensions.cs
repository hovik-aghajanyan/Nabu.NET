using System;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Schema;
using Nabu.Mcp.AspNetCore.SignalR.Discovery;
using Nabu.Mcp.AspNetCore.SignalR.Execution;

namespace Nabu.Mcp.AspNetCore.SignalR
{
    /// <summary>Registration helpers for publishing SignalR hub methods as MCP tools.</summary>
    public static class NabuMcpSignalRServiceCollectionExtensions
    {
        /// <summary>
        /// Publishes the application's <c>[McpTool]</c>-annotated SignalR hub methods as MCP tools,
        /// merged into the same catalogue as the HTTP tools. Call it alongside
        /// <c>AddNabuMcp()</c> and <c>AddSignalR()</c>:
        /// <code>
        /// builder.Services.AddSignalR();
        /// builder.Services.AddNabuMcp(options => { ... })
        ///                 .AddNabuMcpSignalR();
        /// </code>
        /// </summary>
        public static IServiceCollection AddNabuMcpSignalR(this IServiceCollection services, Action<NabuMcpSignalROptions>? configure = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            services.AddOptions();

            if (configure != null)
            {
                services.Configure(configure);
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpToolSource, SignalRHubToolSource>(sp => new SignalRHubToolSource(
                sp.GetService<EndpointDataSource>(),
                sp.GetRequiredService<IOptions<NabuMcpOptions>>(),
                sp.GetRequiredService<IOptions<NabuMcpSignalROptions>>(),
                sp.GetRequiredService<JsonSchemaGenerator>(),
                sp.GetRequiredService<IXmlDocumentationProvider>(),
                sp.GetService<ILogger<SignalRHubToolSource>>())));

            services.TryAddSingleton<SignalRHubToolInvoker>();

            return services;
        }
    }
}
