namespace Nabu.Mcp.AspNetCore
{
    /// <summary>Protocol level constants used by the MCP endpoint.</summary>
    public static class McpConstants
    {
        /// <summary>The MCP revision this server implements.</summary>
        public const string ProtocolVersion = "2025-06-18";

        /// <summary>Protocol revisions this server is able to speak.</summary>
        public static readonly string[] SupportedProtocolVersions =
        {
            "2025-06-18",
            "2025-03-26",
            "2024-11-05",
        };

        public const string SessionIdHeader = "Mcp-Session-Id";
        public const string ProtocolVersionHeader = "MCP-Protocol-Version";

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
    }
}
