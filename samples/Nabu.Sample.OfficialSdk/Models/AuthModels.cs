using System.ComponentModel.DataAnnotations;

namespace Nabu.Sample.OfficialSdk.Models
{
    /// <summary>Credentials exchanged for an access token.</summary>
    public class LoginRequest
    {
        /// <summary>User login. The sample accepts "alice" (user) and "root" (administrator).</summary>
        [Required]
        public string Username { get; set; } = string.Empty;

        /// <summary>Password. Every sample account uses "password".</summary>
        [Required]
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>An issued access token.</summary>
    public class LoginResponse
    {
        public string AccessToken { get; set; } = string.Empty;

        public string TokenType { get; set; } = "Bearer";

        public int ExpiresInSeconds { get; set; }
    }
}
