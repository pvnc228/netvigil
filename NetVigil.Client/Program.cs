using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using NetVigil.Client;
using NetVigil.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["Api:BaseUrl"];
if (string.IsNullOrWhiteSpace(apiBase))
    apiBase = builder.HostEnvironment.BaseAddress;
apiBase = apiBase.TrimEnd('/');
builder.Configuration["Api:BaseUrl"] = apiBase;

builder.Services.AddBlazoredLocalStorage();

builder.Services.AddScoped<JwtAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthStateProvider>());
builder.Services.AddAuthorizationCore();

builder.Services.AddTransient<AuthHttpHandler>();

builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<AuthHttpHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(apiBase) };
});

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<LanguageService>();
builder.Services.AddScoped<DashboardHubClient>();
builder.Services.AddScoped<AppState>();

var host = builder.Build();

var theme = host.Services.GetRequiredService<ThemeService>();
await theme.InitAsync();
var lang = host.Services.GetRequiredService<LanguageService>();
await lang.InitAsync();

await host.RunAsync();
