using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Security.Claims;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Nabu.Mcp.AspNetCore.SignalR.Execution
{
    /// <summary>
    /// The in-process connection a tool call runs over: two pipes wired back-to-back, exactly the
    /// shape SignalR's own test client uses. The server side (<see cref="ConnectionContext.Transport"/>)
    /// is handed to <c>HubConnectionHandler&lt;THub&gt;</c>; the invoker speaks the JSON hub protocol
    /// over <see cref="Application"/>. The caller's identity travels as
    /// <see cref="IConnectionUserFeature"/>, which is where <c>HubCallerContext.User</c> reads it from.
    /// </summary>
    internal sealed class SyntheticHubConnectionContext : ConnectionContext,
        IConnectionUserFeature,
        IConnectionItemsFeature,
        IHttpContextFeature
    {
        private readonly FeatureCollection _features = new FeatureCollection();

        public SyntheticHubConnectionContext(ClaimsPrincipal? user, HttpContext? httpContext)
        {
            var applicationToTransport = new Pipe();
            var transportToApplication = new Pipe();

            Transport = new SimpleDuplexPipe(applicationToTransport.Reader, transportToApplication.Writer);
            Application = new SimpleDuplexPipe(transportToApplication.Reader, applicationToTransport.Writer);

            ConnectionId = "nabu-mcp-" + Guid.NewGuid().ToString("N");
            User = user;
            HttpContext = httpContext;

            _features.Set<IConnectionUserFeature>(this);
            _features.Set<IConnectionItemsFeature>(this);
            if (httpContext != null)
            {
                _features.Set<IHttpContextFeature>(this);
            }
        }

        /// <summary>The invoker's side of the connection.</summary>
        public IDuplexPipe Application { get; }

        public override string ConnectionId { get; set; }

        public override IFeatureCollection Features => _features;

        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

        public override IDuplexPipe Transport { get; set; }

        public ClaimsPrincipal? User { get; set; }

        public HttpContext? HttpContext { get; set; }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            Application.Output.Complete(abortReason);
            Application.Input.CancelPendingRead();
        }

        private sealed class SimpleDuplexPipe : IDuplexPipe
        {
            public SimpleDuplexPipe(PipeReader input, PipeWriter output)
            {
                Input = input;
                Output = output;
            }

            public PipeReader Input { get; }

            public PipeWriter Output { get; }
        }
    }
}
