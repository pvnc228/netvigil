using System;

namespace NetVigil.Shared
{
    public class User
    {
        public long Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.Viewer;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastLoginAt { get; set; }

        public bool MustChangePassword { get; set; }
    }

    public enum UserRole
    {
        Viewer = 0,
        Admin = 1
    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginResponse
    {
        public string Token { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public UserRole Role { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool MustChangePassword { get; set; }
    }

    public class AppSettings
    {
        public bool TelegramEnabled { get; set; }
        public string TelegramBotToken { get; set; } = string.Empty;
        public long TelegramChatId { get; set; }
        public int ScanIntervalSeconds { get; set; } = 10;
        public double AnomalyThresholdZScore { get; set; } = 3.0;
    }

    public class UserSummary
    {
        public long Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public UserRole Role { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public bool MustChangePassword { get; set; }
    }

    public class CreateUserRequest
    {
        public string Username { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.Viewer;
    }

    public class CreateUserResponse
    {
        public UserSummary User { get; set; } = new();
        public string GeneratedPassword { get; set; } = string.Empty;
    }

    public class UpdateUserRoleRequest
    {
        public UserRole Role { get; set; }
    }

    public class FlagDeviceRequest
    {
        public bool IsFlagged { get; set; }
        public string? Reason { get; set; }
    }
}
