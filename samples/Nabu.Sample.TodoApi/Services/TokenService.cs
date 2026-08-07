using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Nabu.Sample.TodoApi.Services
{
    /// <summary>Settings for the sample's JWT bearer authentication.</summary>
    public sealed class JwtOptions
    {
        public const string SectionName = "Jwt";

        public string Issuer { get; set; } = "nabu-sample";

        public string Audience { get; set; } = "nabu-sample";

        /// <summary>
        /// Symmetric signing key. The sample ships a development value in appsettings.json; a real
        /// deployment must supply its own through configuration or a secret store.
        /// </summary>
        public string SigningKey { get; set; } = string.Empty;

        public int LifetimeMinutes { get; set; } = 60;
    }

    /// <summary>Issues the access tokens used by the sample's login endpoint.</summary>
    public sealed class TokenService
    {
        private readonly JwtOptions _options;

        public TokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options)
        {
            _options = options.Value;
        }

        public (string Token, int ExpiresInSeconds) Issue(string username, IEnumerable<string> roles)
        {
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, username),
                new Claim(ClaimTypes.Name, username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            };

            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expires = DateTime.UtcNow.AddMinutes(_options.LifetimeMinutes);

            var token = new JwtSecurityToken(
                issuer: _options.Issuer,
                audience: _options.Audience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: expires,
                signingCredentials: credentials);

            return (new JwtSecurityTokenHandler().WriteToken(token), _options.LifetimeMinutes * 60);
        }
    }
}
