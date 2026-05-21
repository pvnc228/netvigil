using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using NetVigil.Server.Data;
using NetVigil.Server.Services;
using NetVigil.Server.Services.Anomaly;
using NetVigil.Server.Services.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "NetVigil API", Version = "v1" });
    var bearer = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header
    };
    c.AddSecurityDefinition("Bearer", bearer);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, o => o.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(8081, o => o.Protocols = HttpProtocols.Http2);
});

var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
var corsOrigins = configuredOrigins is { Length: > 0 }
    ? configuredOrigins
    : new[]
    {
        "https://localhost:7031",
        "http://localhost:5001",
        "http://localhost:5173"
    };
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit  = 5,
                Window       = TimeSpan.FromMinutes(1),
                QueueLimit   = 0,
                AutoReplenishment = true
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var dpKeyPath = builder.Configuration["DataProtection:KeyPath"] ?? "/app/data/dpkeys";
try { Directory.CreateDirectory(dpKeyPath); } catch {}
builder.Services.AddDataProtection()
    .SetApplicationName("NetVigil")
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeyPath));

builder.Services.AddHealthChecks();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<NetVigilDbContext>(opts =>
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        var sqlitePath = Path.Combine(AppContext.BaseDirectory, "netvigil.db");
        opts.UseSqlite($"Data Source={sqlitePath}");
    }
    else if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
             connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase))
    {
        opts.UseNpgsql(connectionString);
    }
    else
    {
        opts.UseSqlite(connectionString);
    }
});

var jwtSection = builder.Configuration.GetSection("Jwt");
var configuredSecret = jwtSection["Secret"];
var jwt = new JwtOptions
{
    Secret = string.IsNullOrWhiteSpace(configuredSecret) ? GenerateDevSecret() : configuredSecret,
    Issuer = jwtSection["Issuer"] ?? "NetVigil",
    Audience = jwtSection["Audience"] ?? "NetVigil.Client",
    ExpiryMinutes = int.TryParse(jwtSection["ExpiryMinutes"], out var m) ? m : 480
};
builder.Services.AddSingleton(jwt);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret))
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<SettingsStore>();

var detectorKind = (builder.Configuration["Anomaly:Detector"] ?? "zscore").ToLowerInvariant();
if (detectorKind is "isolation-forest" or "if" or "isoforest")
    builder.Services.AddSingleton<IAnomalyDetector, IsolationForestDetector>();
else
    builder.Services.AddSingleton<IAnomalyDetector, RollingZScoreDetector>();

builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddHostedService<MetricsFlushWorker>();
builder.Services.AddHostedService<DashboardBroadcaster>();
builder.Services.AddSignalR();
builder.Services.AddGrpc();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
    var migratorLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    await WaitForDatabaseAsync(db, migratorLogger, TimeSpan.FromSeconds(60));

    db.Database.EnsureCreated();
    await NetVigil.Server.Data.SchemaMigrator.ApplyAsync(db, migratorLogger);
    await NetVigil.Server.Data.SchemaMigrator.ApplyTimescaleAsync(db, migratorLogger);

    var auth = app.Services.GetRequiredService<AuthService>();
    var defaultUser = builder.Configuration["Auth:DefaultAdmin:Username"] ?? "admin";
    var defaultPwd = builder.Configuration["Auth:DefaultAdmin:Password"] ?? "admin";
    await auth.EnsureSeededAsync(defaultUser, defaultPwd);

    var settings = app.Services.GetRequiredService<SettingsStore>();
    await settings.LoadAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapGrpcService<NetVigil.Server.Services.GrpcScanService>();
app.MapControllers();
app.MapHub<NetVigil.Server.Hubs.DashboardHub>("/hubs/dashboard");

app.Run();

static string GenerateDevSecret()
{
    var bytes = new byte[64];
    using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
    rng.GetBytes(bytes);
    return Convert.ToBase64String(bytes);
}

static async Task WaitForDatabaseAsync(NetVigilDbContext db, ILogger logger, TimeSpan timeout)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var attempt = 0;
    while (true)
    {
        try
        {
            if (await db.Database.CanConnectAsync())
                return;
        }
        catch (Exception ex)
        {
            if (sw.Elapsed >= timeout)
            {
                logger.LogError(ex, "Database not reachable after {Timeout}s", (int)timeout.TotalSeconds);
                throw;
            }
            if (++attempt % 5 == 1)
                logger.LogInformation("Waiting for database... ({Reason})", ex.GetBaseException().Message);
        }

        if (sw.Elapsed >= timeout)
            throw new TimeoutException($"Database did not become reachable within {(int)timeout.TotalSeconds}s.");

        await Task.Delay(1000);
    }
}
