using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.ChatHub.Models;
using Nabu.Sample.ChatHub.Services;

namespace Nabu.Sample.ChatHub.Controllers
{
    /// <summary>
    /// Issues the tokens used by the rest of the sample. Credential handling is never exposed as an
    /// MCP tool, so the whole controller carries <see cref="McpIgnoreAttribute"/>.
    /// </summary>
    [ApiController]
    [Route("api/auth")]
    [AllowAnonymous]
    [McpIgnore]
    public class AuthController : ControllerBase
    {
        // Sample-only accounts. A real application would look users up in a store.
        private static readonly Dictionary<string, string[]> Accounts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new[] { "user" },
            ["root"] = new[] { "user", "admin" },
        };

        private readonly TokenService _tokens;

        public AuthController(TokenService tokens)
        {
            _tokens = tokens;
        }

        /// <summary>Exchanges sample credentials for a bearer token.</summary>
        [HttpPost("login")]
        public ActionResult<LoginResponse> Login([FromBody] LoginRequest request)
        {
            string[]? roles;
            if (!Accounts.TryGetValue(request.Username, out roles) || request.Password != "password")
            {
                return Unauthorized(new { message = "Invalid username or password." });
            }

            var issued = _tokens.Issue(request.Username, roles!);

            return Ok(new LoginResponse
            {
                AccessToken = issued.Token,
                ExpiresInSeconds = issued.ExpiresInSeconds,
            });
        }
    }
}
