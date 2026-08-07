using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Nabu.Mcp.AspNetCore.Server
{
    /// <summary>
    /// Inserts a pass-through component at the very front of the request pipeline whose only job is to
    /// hand the rest of the pipeline to <see cref="McpPipelineProvider"/>.
    /// </summary>
    internal sealed class McpPipelineCaptureStartupFilter : IStartupFilter
    {
        private readonly McpPipelineProvider _provider;

        public McpPipelineCaptureStartupFilter(McpPipelineProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(pipeline =>
                {
                    // Runs at Build() time, after every downstream component has been composed.
                    _provider.Capture(pipeline);
                    return context => pipeline(context);
                });

                next(app);
            };
        }
    }
}
