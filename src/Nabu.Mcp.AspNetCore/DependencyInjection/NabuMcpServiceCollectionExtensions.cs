using System;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using Nabu.Mcp.AspNetCore.Schema;
using Nabu.Mcp.AspNetCore.Server;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>Registration helpers for the Nabu MCP server.</summary>
    public static class NabuMcpServiceCollectionExtensions
    {
        /// <summary>
        /// Adds everything needed to expose annotated Web API actions over MCP. Pair it with
        /// <c>app.UseNabuMcp()</c> in the request pipeline.
        /// </summary>
        public static IServiceCollection AddNabuMcp(this IServiceCollection services, Action<NabuMcpOptions>? configure = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            services.AddOptions();
            services.AddHttpContextAccessor();

            if (configure != null)
            {
                services.Configure(configure);
            }

            services.PostConfigure<NabuMcpOptions>(options =>
            {
                if (string.IsNullOrEmpty(options.ServerName))
                {
                    options.ServerName = Assembly.GetEntryAssembly()?.GetName().Name ?? "nabu-mcp";
                }

                if (string.IsNullOrEmpty(options.ServerVersion))
                {
                    options.ServerVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
                }

                if (string.IsNullOrEmpty(options.Path))
                {
                    options.Path = "/mcp";
                }
                else if (!options.Path.StartsWith("/", StringComparison.Ordinal))
                {
                    options.Path = "/" + options.Path;
                }
            });

            services.TryAddSingleton<McpPipelineProvider>();
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<IStartupFilter, McpPipelineCaptureStartupFilter>(
                    sp => new McpPipelineCaptureStartupFilter(sp.GetRequiredService<McpPipelineProvider>())));

            services.TryAddSingleton<IXmlDocumentationProvider>(sp =>
                sp.GetRequiredService<IOptions<NabuMcpOptions>>().Value.UseXmlDocumentation
                    ? (IXmlDocumentationProvider)new XmlDocumentationProvider()
                    : NullXmlDocumentationProvider.Instance);

            services.TryAddSingleton(sp =>
            {
                var options = sp.GetRequiredService<IOptions<NabuMcpOptions>>().Value;
                return new JsonSchemaGenerator(sp.GetRequiredService<IXmlDocumentationProvider>(), options.MaxSchemaDepth);
            });

#if NETSTANDARD2_0
            services.TryAddSingleton<IMcpToolRegistry>(sp => new McpToolRegistry(
                sp.GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>(),
                sp.GetRequiredService<IOptions<NabuMcpOptions>>(),
                sp.GetRequiredService<JsonSchemaGenerator>(),
                sp.GetRequiredService<IXmlDocumentationProvider>(),
                sp.GetService<ILogger<McpToolRegistry>>(),
                sp.GetServices<IMcpToolSource>()));
#else
            // Both discovery sources are optional: an app without controllers still exposes its
            // Minimal API endpoints, and vice versa.
            services.TryAddSingleton<IMcpToolRegistry>(sp => new McpToolRegistry(
                sp.GetService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>(),
                sp.GetRequiredService<IOptions<NabuMcpOptions>>(),
                sp.GetRequiredService<JsonSchemaGenerator>(),
                sp.GetRequiredService<IXmlDocumentationProvider>(),
                sp.GetService<Microsoft.AspNetCore.Routing.EndpointDataSource>(),
                sp.GetService<IServiceProviderIsService>(),
                sp.GetService<ILogger<McpToolRegistry>>(),
                sp.GetServices<IMcpToolSource>()));
#endif

            services.TryAddSingleton(sp => McpJsonCompatibility.Detect(
                sp,
                sp.GetRequiredService<IOptions<NabuMcpOptions>>().Value.StringEnumsInRequestBody));

            services.TryAddSingleton<IMcpToolInvoker>(sp => new McpToolInvoker(
                sp.GetRequiredService<McpPipelineProvider>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptions<NabuMcpOptions>>(),
                sp.GetRequiredService<McpJsonCompatibility>(),
                sp.GetService<IHttpContextFactory>(),
                sp.GetService<IHttpContextAccessor>(),
                sp.GetService<ILogger<McpToolInvoker>>()));

            services.TryAddSingleton<IMcpToolAuthorizationEvaluator>(sp => new McpToolAuthorizationEvaluator(
                sp.GetRequiredService<IOptions<NabuMcpOptions>>(),
                sp.GetService<ILogger<McpToolAuthorizationEvaluator>>()));

            services.TryAddSingleton(sp => new McpRequestHandler(
                sp.GetRequiredService<IMcpToolRegistry>(),
                sp.GetRequiredService<IMcpToolInvoker>(),
                sp.GetRequiredService<IOptions<NabuMcpOptions>>(),
                sp.GetService<ILogger<McpRequestHandler>>(),
                sp.GetRequiredService<IMcpToolAuthorizationEvaluator>()));

            return services;
        }
    }
}
