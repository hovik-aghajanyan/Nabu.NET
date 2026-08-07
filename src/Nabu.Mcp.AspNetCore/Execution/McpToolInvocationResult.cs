using System.Collections.Generic;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>The raw HTTP outcome of replaying a tool call through the application pipeline.</summary>
    public sealed class McpToolInvocationResult
    {
        public McpToolInvocationResult(int statusCode, string? contentType, string body, IDictionary<string, string> headers)
        {
            StatusCode = statusCode;
            ContentType = contentType;
            Body = body;
            Headers = headers;
        }

        public int StatusCode { get; }

        public string? ContentType { get; }

        /// <summary>Response body, decoded as UTF-8 and possibly truncated.</summary>
        public string Body { get; }

        public IDictionary<string, string> Headers { get; }

        /// <summary>True when the body exceeded <see cref="NabuMcpOptions.MaxResponseBytes"/>.</summary>
        public bool Truncated { get; set; }

        /// <summary>The path and query the tool call was replayed against. Useful for diagnostics.</summary>
        public string? RequestUri { get; set; }

        public bool IsSuccess
        {
            get { return StatusCode >= 200 && StatusCode < 400; }
        }
    }
}
