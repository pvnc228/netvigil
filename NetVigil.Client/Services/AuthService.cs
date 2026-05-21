using System.Net.Http.Json;
using Blazored.LocalStorage;
using NetVigil.Shared;

namespace NetVigil.Client.Services
{
    public class AuthService
    {
        private const string TokenKey = "netvigil.token";
        private const string UserKey = "netvigil.user";

        private readonly HttpClient _http;
        private readonly ILocalStorageService _storage;
        private readonly JwtAuthStateProvider _authState;

        public AuthService(HttpClient http, ILocalStorageService storage, JwtAuthStateProvider authState)
        {
            _http = http;
            _storage = storage;
            _authState = authState;
        }

        public async Task<LoginResponse?> LoginAsync(string username, string password)
        {
            try
            {
                var resp = await _http.PostAsJsonAsync("api/auth/login", new LoginRequest { Username = username, Password = password });
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
                if (body is null || string.IsNullOrEmpty(body.Token)) return null;

                await _storage.SetItemAsync(TokenKey, body.Token);
                await _storage.SetItemAsync(UserKey, new StoredUser
                {
                    Username = body.Username,
                    Role = body.Role,
                    ExpiresAt = body.ExpiresAt
                });
                _authState.NotifyChanged();
                return body;
            }
            catch
            {
                return null;
            }
        }

        public async Task LogoutAsync()
        {
            await _storage.RemoveItemAsync(TokenKey);
            await _storage.RemoveItemAsync(UserKey);
            _authState.NotifyChanged();
        }

        public async Task<string?> GetTokenAsync()
        {
            try { return await _storage.GetItemAsync<string>(TokenKey); }
            catch { return null; }
        }

        public async Task<StoredUser?> GetCurrentUserAsync()
        {
            try
            {
                var user = await _storage.GetItemAsync<StoredUser>(UserKey);
                if (user is null) return null;
                if (user.ExpiresAt < DateTime.UtcNow)
                {
                    await LogoutAsync();
                    return null;
                }
                return user;
            }
            catch
            {
                return null;
            }
        }

        public class StoredUser
        {
            public string Username { get; set; } = string.Empty;
            public UserRole Role { get; set; }
            public DateTime ExpiresAt { get; set; }
        }
    }
}
