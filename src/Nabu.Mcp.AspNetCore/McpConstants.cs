namespace Nabu.Mcp.AspNetCore
{
    /// <summary>Protocol level constants used by the MCP endpoint.</summary>
    public static class McpConstants
    {
        /// <summary>The legacy MCP revision this server negotiates at <c>initialize</c>.</summary>
        public const string ProtocolVersion = "2025-06-18";

        /// <summary>
        /// Legacy protocol revisions negotiated through the <c>initialize</c> handshake.
        /// </summary>
        public static readonly string[] SupportedProtocolVersions =
        {
            "2025-06-18",
            "2025-03-26",
            "2024-11-05",
        };

        /// <summary>
        /// Modern protocol revisions, declared per request via <c>_meta</c> instead of a handshake.
        /// Kept separate from <see cref="SupportedProtocolVersions"/> because a client that picks a
        /// version from this list must not be steered towards one that only works via <c>initialize</c>.
        /// </summary>
        public static readonly string[] ModernProtocolVersions =
        {
            "2026-07-28",
        };

        public const string SessionIdHeader = "Mcp-Session-Id";
        public const string ProtocolVersionHeader = "MCP-Protocol-Version";
        public const string MethodHeader = "Mcp-Method";
        public const string NameHeader = "Mcp-Name";

        // _meta keys defined by revision 2026-07-28.
        public const string ProtocolVersionMetaKey = "io.modelcontextprotocol/protocolVersion";
        public const string ClientCapabilitiesMetaKey = "io.modelcontextprotocol/clientCapabilities";
        public const string ClientInfoMetaKey = "io.modelcontextprotocol/clientInfo";
        public const string ServerInfoMetaKey = "io.modelcontextprotocol/serverInfo";

        public const string JsonContentType = "application/json";
        public const string EventStreamContentType = "text/event-stream";

        /// <summary>
        /// <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> key set on synthetic requests
        /// produced by a tool invocation. The value is the tool name.
        /// </summary>
        public const string ToolInvocationItemKey = "Nabu.Mcp.ToolName";

        /// <summary>
        /// <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> key holding the originating MCP
        /// <see cref="Microsoft.AspNetCore.Http.HttpContext"/> of a tool invocation.
        /// </summary>
        public const string ParentHttpContextItemKey = "Nabu.Mcp.ParentHttpContext";

        internal const string DepthItemKey = "Nabu.Mcp.Depth";

        // JSON-RPC 2.0 error codes.
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        // Error codes reserved by the MCP specification (revision 2026-07-28).
        public const int HeaderMismatch = -32020;
        public const int UnsupportedProtocolVersion = -32022;
    }
}
