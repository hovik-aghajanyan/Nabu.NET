using System;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Nabu.Mcp.AspNetCore.Protocol
{
    /// <summary>
    /// Per-request mechanics of protocol revision 2026-07-28, where the protocol version, client
    /// identity and capabilities travel in <c>_meta</c> on every request instead of being negotiated
    /// through an <c>initialize</c> handshake.
    /// </summary>
    internal static class ModernProtocol
    {
        private const string Base64Prefix = "=?base64?";
        private const string Base64Suffix = "?=";

        /// <summary>The protocol version declared in the request's <c>_meta</c>, or <c>null</c>.</summary>
        internal static string? DeclaredVersion(JsonRpcRequest request)
        {
            var meta = request.Parameters?["_meta"] as JsonObject;
            var node = meta?[McpConstants.ProtocolVersionMetaKey];
            return node is JsonValue value && value.TryGetValue<string>(out var version) ? version : null;
        }

        /// <summary>
        /// Whether this request must be served under per-request-metadata semantics. Declaring a
        /// protocol version in <c>_meta</c> is what separates the eras - legacy clients never send
        /// that key - and <c>server/discover</c> only exists in the modern protocol, so it counts
        /// even when the metadata was left out.
        /// </summary>
        internal static bool IsModern(JsonRpcRequest request)
        {
            return DeclaredVersion(request) != null ||
                   string.Equals(request.Method, "server/discover", StringComparison.Ordinal);
        }

        internal static bool IsSupportedVersion(string version)
        {
            foreach (var supported in McpConstants.ModernProtocolVersions)
            {
                if (string.Equals(supported, version, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Validates the request headers the transport mirrors from the body
        /// (<c>MCP-Protocol-Version</c>, <c>Mcp-Method</c>, <c>Mcp-Name</c>), so that an intermediary
        /// routing on a header can never disagree with what the server executes. Returns the message
        /// for a <c>HeaderMismatch</c> error, or <c>null</c> when the request is valid.
        /// </summary>
        internal static string? ValidateHeaders(HttpContext context, JsonRpcRequest request, string? declaredVersion)
        {
            var headerVersion = context.Request.Headers[McpConstants.ProtocolVersionHeader].ToString();
            if (string.IsNullOrEmpty(headerVersion))
            {
                return "The " + McpConstants.ProtocolVersionHeader + " header is required.";
            }

            if (declaredVersion == null)
            {
                return "The '" + McpConstants.ProtocolVersionMetaKey + "' _meta field is required.";
            }

            if (!string.Equals(headerVersion, declaredVersion, StringComparison.Ordinal))
            {
                return "Header mismatch: " + McpConstants.ProtocolVersionHeader + " value '" + headerVersion +
                       "' does not match the _meta value '" + declaredVersion + "'.";
            }

            var headerMethod = context.Request.Headers[McpConstants.MethodHeader].ToString();
            if (string.IsNullOrEmpty(headerMethod))
            {
                return "The " + McpConstants.MethodHeader + " header is required.";
            }

            if (!string.Equals(headerMethod, request.Method, StringComparison.Ordinal))
            {
                return "Header mismatch: " + McpConstants.MethodHeader + " value '" + headerMethod +
                       "' does not match the request method '" + request.Method + "'.";
            }

            // Mcp-Name mirrors params.name (or params.uri). When the body value is missing the
            // header is not expected, and the handler reports the missing parameter instead.
            var bodyName = NamedValue(request);
            if (bodyName != null)
            {
                var headerName = context.Request.Headers[McpConstants.NameHeader].ToString();
                if (string.IsNullOrEmpty(headerName))
                {
                    return "The " + McpConstants.NameHeader + " header is required for " + request.Method + ".";
                }

                var decoded = DecodeHeaderValue(headerName);
                if (decoded == null || !string.Equals(decoded, bodyName, StringComparison.Ordinal))
                {
                    return "Header mismatch: " + McpConstants.NameHeader + " value '" + headerName +
                           "' does not match the body value '" + bodyName + "'.";
                }
            }

            return null;
        }

        /// <summary>The body value the <c>Mcp-Name</c> header mirrors, for methods that carry one.</summary>
        private static string? NamedValue(JsonRpcRequest request)
        {
            switch (request.Method)
            {
                case "tools/call":
                case "prompts/get":
                    return AsString(request.Parameters?["name"]);

                case "resources/read":
                    return AsString(request.Parameters?["uri"]);

                default:
                    return null;
            }
        }

        private static string? AsString(JsonNode? node)
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }

        /// <summary>
        /// Undoes the transport's Base64 sentinel encoding (<c>=?base64?...?=</c>) used for header
        /// values that are not header-safe ASCII. Returns <c>null</c> for a malformed encoding.
        /// </summary>
        internal static string? DecodeHeaderValue(string value)
        {
            if (!value.StartsWith(Base64Prefix, StringComparison.Ordinal) ||
                !value.EndsWith(Base64Suffix, StringComparison.Ordinal) ||
                value.Length < Base64Prefix.Length + Base64Suffix.Length)
            {
                return value;
            }

            var encoded = value.Substring(Base64Prefix.Length, value.Length - Base64Prefix.Length - Base64Suffix.Length);
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
