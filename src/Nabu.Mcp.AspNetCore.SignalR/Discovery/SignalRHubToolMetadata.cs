using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;

namespace Nabu.Mcp.AspNetCore.SignalR.Discovery
{
    /// <summary>
    /// What the invoker needs to run one hub-method tool, attached to the descriptor at discovery
    /// time. The descriptor itself stays source-agnostic; this rides alongside it.
    /// </summary>
    internal sealed class SignalRHubToolMetadata
    {
        public SignalRHubToolMetadata(
            Type hubType,
            MethodInfo method,
            bool isStreaming,
            IReadOnlyList<ParameterInfo> invocationParameters,
            IReadOnlyList<IAuthorizeData> connectionAuthorizeData,
            bool connectionAllowsAnonymous)
        {
            HubType = hubType;
            Method = method;
            IsStreaming = isStreaming;
            InvocationParameters = invocationParameters;
            ConnectionAuthorizeData = connectionAuthorizeData;
            ConnectionAllowsAnonymous = connectionAllowsAnonymous;
        }

        public Type HubType { get; }

        public MethodInfo Method { get; }

        /// <summary>True when the method returns a stream the invoker must collect item by item.</summary>
        public bool IsStreaming { get; }

        /// <summary>
        /// The method's parameters in declaration order, excluding the ones SignalR itself supplies
        /// (currently <see cref="System.Threading.CancellationToken"/>). The invoker builds the
        /// positional argument array from this list.
        /// </summary>
        public IReadOnlyList<ParameterInfo> InvocationParameters { get; }

        /// <summary>
        /// The hub-class-level <c>[Authorize]</c> requirement. The real dispatcher never sees it -
        /// on a live connection it is enforced by the HTTP negotiate - so the invoker evaluates it
        /// itself before opening the synthetic connection.
        /// </summary>
        public IReadOnlyList<IAuthorizeData> ConnectionAuthorizeData { get; }

        /// <summary>True when the hub class carries <c>[AllowAnonymous]</c>.</summary>
        public bool ConnectionAllowsAnonymous { get; }
    }
}
