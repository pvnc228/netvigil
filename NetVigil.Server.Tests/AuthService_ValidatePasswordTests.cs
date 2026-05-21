using NetVigil.Server.Services.Auth;
using Xunit;

namespace NetVigil.Server.Tests;

public class AuthService_ValidatePasswordTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short1")]            // < 8 chars
    [InlineData("only-letters")]      // no digits
    [InlineData("12345678")]          // no letters
    [InlineData("        9")]         
    public void Rejects_invalid_passwords(string bad)
    {
        var (ok, err) = AuthService.ValidatePassword(bad);
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Theory]
    [InlineData("hunter22")]          
    [InlineData("p4ssw0rdy")]
    [InlineData("verylongpasswordwith1digit")]
    public void Accepts_valid_passwords(string good)
    {
        var (ok, err) = AuthService.ValidatePassword(good);
        Assert.True(ok, $"expected accept, got error: {err}");
        Assert.Null(err);
    }
}
