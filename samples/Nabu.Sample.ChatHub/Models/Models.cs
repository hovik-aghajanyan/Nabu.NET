using System;
using System.ComponentModel.DataAnnotations;

namespace Nabu.Sample.ChatHub.Models
{
    /// <summary>One message in the chat room.</summary>
    /// <param name="Id">Server-assigned identifier.</param>
    /// <param name="User">Name of the user who sent it.</param>
    /// <param name="Text">The message text.</param>
    /// <param name="SentAt">When the server accepted it.</param>
    public sealed record ChatMessage(Guid Id, string User, string Text, DateTimeOffset SentAt);

    public sealed class LoginRequest
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class LoginResponse
    {
        public string AccessToken { get; set; } = string.Empty;

        public int ExpiresInSeconds { get; set; }
    }
}
