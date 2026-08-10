using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Server;

namespace Nabu.Mcp.ModelContextProtocol
{
    /// <summary>
    /// Mounts the official SDK's Streamable HTTP endpoint where <c>UseNabuMcp()</c> would have
    /// mounted the built-in middleware, so switching protocol layers does not change how an
    /// application wires its pipeline.
    /// </summary>
    internal sealed class OfficialMcpProtocolMount : INabuMcpProtocolMount
    {
        public void Mount(IApplicationBuilder app, string path)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            if (app is IEndpointRouteBuilder endpoints)
            {
                // Minimal hosting: WebApplication is its own route builder, so the endpoint joins
                // the application's routing directly.
                Map(endpoints, path, app.ApplicationServices);
                return;
            }

            // Startup-style pipelines: mount a routing scope of our own at this position, which
            // keeps the contract that UseNabuMcp() serves MCP from wherever it was placed.
            app.UseRouting();
            app.UseEndpoints(builder => Map(builder, path, app.ApplicationServices));
        }

        private static void Map(IEndpointRouteBuilder endpoints, string path, IServiceProvider services)
        {
            var endpoint = endpoints.MapMcp(path);

            var options = services.GetRequiredService<IOptions<NabuMcpOptions>>().Value;
            if (options.RequireAuthorization && options.AnonymousAccess == McpAnonymousAccess.None)
            {
                if (string.IsNullOrEmpty(options.AuthorizationPolicy))
                {
                    endpoint.RequireAuthorization();
                }
                else
                {
                    endpoint.RequireAuthorization(options.AuthorizationPolicy!);
                }
            }
        }
    }
}
