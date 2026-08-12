using System;
using System.IO;
using System.Text;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>
    /// Renders the bound form data of a tool call as an HTTP request body: multipart/form-data when a
    /// file is present, application/x-www-form-urlencoded otherwise. The synthetic request carries the
    /// matching <c>Content-Type</c>, so the action's own form model binding reads it back exactly as it
    /// would a browser upload.
    /// </summary>
    internal static class FormBodyEncoder
    {
        public static byte[] Encode(McpArgumentBinder.BoundRequest bound, out string contentType)
        {
            if (bound.FormFiles.Count == 0)
            {
                contentType = "application/x-www-form-urlencoded; charset=utf-8";
                return EncodeUrlEncoded(bound);
            }

            var boundary = "nabu-mcp-" + Guid.NewGuid().ToString("N");
            contentType = "multipart/form-data; boundary=" + boundary;
            return EncodeMultipart(bound, boundary);
        }

        private static byte[] EncodeUrlEncoded(McpArgumentBinder.BoundRequest bound)
        {
            var builder = new StringBuilder();
            foreach (var field in bound.FormFields)
            {
                if (builder.Length > 0)
                {
                    builder.Append('&');
                }

                builder.Append(Uri.EscapeDataString(field.Key))
                       .Append('=')
                       .Append(Uri.EscapeDataString(field.Value));
            }

            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        private static byte[] EncodeMultipart(McpArgumentBinder.BoundRequest bound, string boundary)
        {
            using (var stream = new MemoryStream())
            {
                foreach (var field in bound.FormFields)
                {
                    WriteText(stream, "--" + boundary + "\r\n");
                    WriteText(
                        stream,
                        "Content-Disposition: form-data; name=\"" + EscapeHeaderValue(field.Key) + "\"\r\n\r\n");
                    WriteText(stream, field.Value);
                    WriteText(stream, "\r\n");
                }

                foreach (var file in bound.FormFiles)
                {
                    WriteText(stream, "--" + boundary + "\r\n");
                    WriteText(
                        stream,
                        "Content-Disposition: form-data; name=\"" + EscapeHeaderValue(file.Name) +
                        "\"; filename=\"" + EscapeHeaderValue(file.FileName) + "\"\r\n");
                    WriteText(stream, "Content-Type: " + file.ContentType + "\r\n\r\n");
                    stream.Write(file.Content, 0, file.Content.Length);
                    WriteText(stream, "\r\n");
                }

                WriteText(stream, "--" + boundary + "--\r\n");
                return stream.ToArray();
            }
        }

        private static void WriteText(Stream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Field and file names land inside a quoted Content-Disposition value; quotes and line breaks
        /// would corrupt the part header, so they are percent-encoded the way browsers do it.
        /// </summary>
        private static string EscapeHeaderValue(string value)
        {
            return value
                .Replace("\"", "%22")
                .Replace("\r", "%0D")
                .Replace("\n", "%0A");
        }
    }
}
