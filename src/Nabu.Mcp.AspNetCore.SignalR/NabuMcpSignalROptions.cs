using System;

namespace Nabu.Mcp.AspNetCore.SignalR
{
    /// <summary>Options for publishing SignalR hub methods as MCP tools.</summary>
    public class NabuMcpSignalROptions
    {
        /// <summary>
        /// Publish every public hub method of every mapped hub, not just the ones annotated with
        /// <c>[McpTool]</c>. <c>[McpIgnore]</c> still wins. Defaults to <c>false</c> - publishing a
        /// hub method hands every authenticated MCP caller the ability to invoke it, so opt in
        /// deliberately, exactly as with controller actions.
        /// </summary>
        public bool ExposeAllHubMethods { get; set; }

        /// <summary>
        /// Cap on the number of items collected from a streaming hub method
        /// (<c>IAsyncEnumerable&lt;T&gt;</c> / <c>ChannelReader&lt;T&gt;</c>) before the stream is
        /// cancelled and the result flagged as truncated. Bounds memory the way
        /// <see cref="NabuMcpOptions.MaxResponseBytes"/> bounds HTTP bodies.
        /// </summary>
        public int MaxStreamItems { get; set; } = 1000;

        /// <summary>
        /// Cap on the number of messages sent to <c>Clients.Caller</c> that are captured into the
        /// tool result. Messages beyond the cap are dropped and the result flagged as truncated.
        /// </summary>
        public int MaxCallerMessages { get; set; } = 100;

        /// <summary>
        /// How long a single hub method invocation - handshake included - may run before the
        /// synthetic connection is torn down and the tool call fails. Defaults to 30 seconds.
        /// </summary>
        public TimeSpan InvocationTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }
}
