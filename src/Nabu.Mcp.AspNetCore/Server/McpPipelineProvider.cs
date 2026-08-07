using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Nabu.Mcp.AspNetCore.Server
{
    /// <summary>
    /// Holds the application's fully built request pipeline so that tool calls can be replayed through
    /// it. The delegate is captured by <see cref="McpPipelineCaptureStartupFilter"/> at the very front
    /// of the pipeline, which is what makes routing, authentication, authorization, filters and
    /// exception handling apply to a tool invocation exactly as they do to a real request.
    /// </summary>
    public sealed class McpPipelineProvider
    {
        private RequestDelegate? _pipeline;

        /// <summary>True once the pipeline has been built and captured.</summary>
        public bool IsAvailable
        {
            get { return _pipeline != null; }
        }

        /// <summary>The captured pipeline.</summary>
        public RequestDelegate Pipeline
        {
            get
            {
                var pipeline = _pipeline;
                if (pipeline == null)
                {
                    throw new InvalidOperationException(
                        "The application pipeline has not been captured. Call services.AddNabuMcp() during " +
                        "service registration so Nabu can install its startup filter.");
                }

                return pipeline;
            }
        }

        internal void Capture(RequestDelegate pipeline)
        {
            // The first capture wins: it is the outermost one and therefore the complete pipeline.
            if (_pipeline == null)
            {
                _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            }
        }
    }
}
