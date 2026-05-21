using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;

namespace NetVigil.Client.Services
{
    public class JwtAuthStateProvider : AuthenticationStateProvider
    {
        private const string TokenKey = "netvigil.token";
        private const string UserKey  = "netvigil.user";

        private readonly ILocalStorageService _storage;
        private readonly string _apiBase;
        private bool _serverValidated;

        private static readonly AuthenticationState Anonymous =
            new(new ClaimsPrincipal(new ClaimsIdentity()));

        public JwtAuthStateProvider(ILocalStorageService storage, IConfiguration config)
        {
            _storage = storage;
            _apiBase = (config["Api:BaseUrl"] ?? string.Empty).TrimEnd('/');
        }

        public void NotifyChanged() =>
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            string? token = null;
            try { token = await _storage.GetItemAsync<string>(TokenKey); }
            catch { return Anonymous; }

            if (string.IsNullOrEmpty(token)) return Anonymous;

            var claims = ParseJwt(token);
            if (claims is null)
            {
                await ClearAsync();
                return Anonymous;
            }

            if (!_serverValidated)
            {
                _serverValidated = true;
                try
                {
                    using var http = new HttpClient();
                    http.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                    var resp = await http.GetAsync($"{_apiBase}/api/auth/me");
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        await ClearAsync();
                        return Anonymous;
                    }
                }
                catch {}
            }

            return new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt")));
        }

        private async Task ClearAsync()
        {
            try
            {
                await _storage.RemoveItemAsync(TokenKey);
                await _storage.RemoveItemAsync(UserKey);
            }
            catch { }
        }

        private static IEnumerable<Claim>? ParseJwt(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) return null;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) & ~3, '=');
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                var dict = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, object>>(json);
                if (dict is null) return null;

                if (dict.TryGetValue("exp", out var expVal) &&
                    long.TryParse(expVal?.ToString(), out var exp) &&
                    DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow)
                    return null;

                var claims = new List<Claim>();
                foreach (var kv in dict)
                {
                    var v = kv.Value?.ToString() ?? "";
                    if (kv.Key is "role" || kv.Key.EndsWith("/role"))
                        claims.Add(new Claim(ClaimTypes.Role, v));
                    else if (kv.Key is "unique_name" || kv.Key.EndsWith("/name"))
                        claims.Add(new Claim(ClaimTypes.Name, v));
                    else
                        claims.Add(new Claim(kv.Key, v));
                }
                return claims;
            }
            catch { return null; }
        }
    }
}
