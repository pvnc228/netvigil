using System.Net.Http.Headers;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;

namespace NetVigil.Client.Services
{
    public class AuthHttpHandler : DelegatingHandler
    {
        private const string TokenKey = "netvigil.token";
        private readonly ILocalStorageService _storage;
        private readonly NavigationManager _nav;
        private readonly JwtAuthStateProvider _authState;

        private static int _handling401;
        private static volatile bool _redirectedThisBurst;

        public AuthHttpHandler(ILocalStorageService storage, NavigationManager nav, JwtAuthStateProvider authState)
        {
            _storage = storage;
            _nav = nav;
            _authState = authState;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            string? token = null;
            try { token = await _storage.GetItemAsync<string>(TokenKey, ct); }
            catch { }
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var resp = await base.SendAsync(request, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                if (Interlocked.Exchange(ref _handling401, 1) == 0)
                {
                    try { await _storage.RemoveItemAsync(TokenKey, ct); } catch { }
                    _authState.NotifyChanged();
                    if (!_redirectedThisBurst &&
                        !_nav.Uri.EndsWith("/login", StringComparison.OrdinalIgnoreCase))
                    {
                        _redirectedThisBurst = true;
                        _nav.NavigateTo("/login", forceLoad: false);
                    }
                    Interlocked.Exchange(ref _handling401, 0);
                }
            }
            else if (resp.IsSuccessStatusCode)
            {
                _redirectedThisBurst = false;
            }
            return resp;
        }
    }
}
