using System.Text.Json.Nodes;

namespace Nabu.Mcp.AspNetCore.Protocol
{
    /// <summary>A parsed JSON-RPC 2.0 request or notification.</summary>
    public sealed class JsonRpcRequest
    {
        public JsonRpcRequest(JsonNode? id, string method, JsonNode? parameters)
        {
            Id = id;
            Method = method;
            Parameters = parameters;
        }

        /// <summary>Request id. <c>null</c> means this is a notification and must not be answered.</summary>
        public JsonNode? Id { get; }

        public string Method { get; }

        public JsonNode? Parameters { get; }

        public bool IsNotification
        {
            get { return Id == null; }
        }
    }

    /// <summary>Builders for JSON-RPC 2.0 envelopes.</summary>
    public static class JsonRpc
    {
        public const string Version = "2.0";

        public static JsonObject Result(JsonNode? id, JsonNode? result)
        {
            return new JsonObject
            {
                ["jsonrpc"] = Version,
                ["id"] = id == null ? null : JsonNode.Parse(id.ToJsonString()),
                ["result"] = result ?? new JsonObject(),
            };
        }

        public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
        {
            var error = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            };

            if (data != null)
            {
                error["data"] = data;
            }

            return new JsonObject
            {
                ["jsonrpc"] = Version,
                ["id"] = id == null ? null : JsonNode.Parse(id.ToJsonString()),
                ["error"] = error,
            };
        }
    }
}
