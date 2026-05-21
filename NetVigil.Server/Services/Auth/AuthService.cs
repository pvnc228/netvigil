using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NetVigil.Server.Data;
using NetVigil.Shared;

namespace NetVigil.Server.Services.Auth
{
    public class JwtOptions
    {
        public string Secret { get; set; } = string.Empty;
        public string Issuer { get; set; } = "NetVigil";
        public string Audience { get; set; } = "NetVigil.Client";
        public int ExpiryMinutes { get; set; } = 480;
    }

    public class AuthService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly JwtOptions _jwt;
        private readonly ILogger<AuthService> _logger;

        public AuthService(IServiceScopeFactory scopeFactory, JwtOptions jwt, ILogger<AuthService> logger)
        {
            _scopeFactory = scopeFactory;
            _jwt = jwt;
            _logger = logger;
        }

        public async Task EnsureSeededAsync(string defaultAdminUsername, string defaultAdminPassword)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();

            if (await db.Users.AnyAsync()) return;

            var admin = new User
            {
                Username = defaultAdminUsername,
                PasswordHash = HashPassword(defaultAdminPassword),
                Role = UserRole.Admin,
                CreatedAt = DateTime.UtcNow
            };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            _logger.LogWarning(
                "Seeded default admin '{User}'. CHANGE the password after first login.",
                defaultAdminUsername);
        }

        public async Task<LoginResponse?> LoginAsync(string username, string password)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user is null) return null;
            if (!VerifyPassword(password, user.PasswordHash)) return null;

            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var (token, expires) = IssueToken(user);
            return new LoginResponse
            {
                Token = token,
                Username = user.Username,
                Role = user.Role,
                ExpiresAt = expires,
                MustChangePassword = user.MustChangePassword
            };
        }

        public async Task<bool> ChangePasswordAsync(string username, string currentPassword, string newPassword)
        {
            if (!ValidatePassword(newPassword).Ok) return false;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user is null) return false;
            if (!VerifyPassword(currentPassword, user.PasswordHash)) return false;
            if (VerifyPassword(newPassword, user.PasswordHash)) return false;
            user.PasswordHash = HashPassword(newPassword);
            user.MustChangePassword = false;
            await db.SaveChangesAsync();
            return true;
        }

        public async Task<List<UserSummary>> ListUsersAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            return await db.Users
                .OrderBy(u => u.Id)
                .Select(u => new UserSummary
                {
                    Id = u.Id,
                    Username = u.Username,
                    Role = u.Role,
                    CreatedAt = u.CreatedAt,
                    LastLoginAt = u.LastLoginAt,
                    MustChangePassword = u.MustChangePassword
                })
                .ToListAsync();
        }

        public async Task<CreateUserResponse?> CreateUserAsync(string username, UserRole role)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            if (await db.Users.AnyAsync(u => u.Username == username)) return null;

            var generated = GeneratePassword();
            var user = new User
            {
                Username = username,
                PasswordHash = HashPassword(generated),
                Role = role,
                CreatedAt = DateTime.UtcNow,
                MustChangePassword = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return new CreateUserResponse
            {
                User = new UserSummary
                {
                    Id = user.Id,
                    Username = user.Username,
                    Role = user.Role,
                    CreatedAt = user.CreatedAt,
                    LastLoginAt = user.LastLoginAt,
                    MustChangePassword = user.MustChangePassword
                },
                GeneratedPassword = generated
            };
        }

        public async Task<bool> DeleteUserAsync(long id)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            var user = await db.Users.FindAsync(id);
            if (user is null) return false;
            if (user.Role == UserRole.Admin &&
                await db.Users.CountAsync(u => u.Role == UserRole.Admin) <= 1)
                return false;
            db.Users.Remove(user);
            await db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> UpdateUserRoleAsync(long id, UserRole role)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            var user = await db.Users.FindAsync(id);
            if (user is null) return false;
            if (user.Role == UserRole.Admin && role != UserRole.Admin &&
                await db.Users.CountAsync(u => u.Role == UserRole.Admin) <= 1)
                return false;
            user.Role = role;
            await db.SaveChangesAsync();
            return true;
        }

        public async Task<string?> ResetPasswordAsync(long id)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            var user = await db.Users.FindAsync(id);
            if (user is null) return null;
            var generated = GeneratePassword();
            user.PasswordHash = HashPassword(generated);
            user.MustChangePassword = true;
            await db.SaveChangesAsync();
            return generated;
        }

        private (string token, DateTime expires) IssueToken(User user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiryMinutes);
            var token = new JwtSecurityToken(
                issuer: _jwt.Issuer,
                audience: _jwt.Audience,
                claims: claims,
                expires: expires,
                signingCredentials: creds);

            return (new JwtSecurityTokenHandler().WriteToken(token), expires);
        }

        public static string GeneratePassword(int length = 14)
        {
            const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz";
            const string digits  = "23456789";
            const string all     = letters + digits;
            if (length < 4) length = 14;

            var buf = new char[length];
            buf[0] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
            buf[1] = digits [RandomNumberGenerator.GetInt32(digits.Length)];
            for (int i = 2; i < length; i++)
                buf[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

            for (int i = buf.Length - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (buf[i], buf[j]) = (buf[j], buf[i]);
            }
            return new string(buf);
        }

        public static string HashPassword(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
            return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        public static (bool Ok, string? Error) ValidatePassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password)) return (false, "Password is required.");
            if (password.Length < 8) return (false, "Password must be at least 8 characters.");
            bool hasLetter = false, hasDigit = false;
            foreach (var c in password)
            {
                if (char.IsLetter(c)) hasLetter = true;
                else if (char.IsDigit(c)) hasDigit = true;
                if (hasLetter && hasDigit) return (true, null);
            }
            return (false, "Password must contain both letters and digits.");
        }

        public static bool VerifyPassword(string password, string stored)
        {
            var parts = stored.Split('.');
            if (parts.Length != 2) return false;
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
    }
}
