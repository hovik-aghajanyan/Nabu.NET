using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Server;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>Pipeline helpers for the Nabu MCP server.</summary>
    public static class NabuMcpApplicationBuilderExtensions
    {
        /// <summary>
        /// Mounts the MCP endpoint. Place this after <c>UseAuthentication</c>/<c>UseAuthorization</c> so
        /// that the MCP endpoint itself is protected by the application's own authentication.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="path">Overrides <see cref="NabuMcpOptions.Path"/> when supplied.</param>
        public static IApplicationBuilder UseNabuMcp(this IApplicationBuilder app, string? path = null)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            var provider = app.ApplicationServices.GetService<McpPipelineProvider>();
            if (provider == null)
            {
                throw new InvalidOperationException(
                    "UseNabuMcp() requires AddNabuMcp() to have been called on the service collection.");
            }

            if (!string.IsNullOrEmpty(path))
            {
                var options = app.ApplicationServices.GetRequiredService<IOptions<NabuMcpOptions>>().Value;
                options.Path = path!.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
            }

            return app.UseMiddleware<McpMiddleware>();
        }
    }
}
