using WorkLedger.Server.Auth;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// No database and no host — this is the one piece of S6 that is pure computation. What matters is that
/// a hash is salted (so two identical passwords do not produce one string an attacker can look up) and
/// that verification is not accidentally comparing plaintext.
/// </summary>
public class PasswordServiceTests
{
    private const string Password = "correct horse battery staple";

    private readonly PasswordService _passwords = new();

    [Fact]
    public void Verify_WithTheRightPassword_Succeeds()
    {
        var hash = _passwords.Hash(Password);

        Assert.Equal(PasswordVerification.Success, _passwords.Verify(hash, Password));
    }

    [Theory]
    [InlineData("correct horse battery stapl")]
    [InlineData("Correct horse battery staple")]
    [InlineData("")]
    public void Verify_WithTheWrongPassword_Fails(string attempt)
    {
        var hash = _passwords.Hash(Password);

        Assert.Equal(PasswordVerification.Failed, _passwords.Verify(hash, attempt));
    }

    [Fact]
    public void Hash_IsSaltedSoTheSamePasswordHashesDifferentlyEveryTime()
    {
        var first = _passwords.Hash(Password);
        var second = _passwords.Hash(Password);

        Assert.NotEqual(first, second);
        Assert.Equal(PasswordVerification.Success, _passwords.Verify(first, Password));
        Assert.Equal(PasswordVerification.Success, _passwords.Verify(second, Password));
    }

    [Fact]
    public void Hash_DoesNotContainThePlaintext()
    {
        var hash = _passwords.Hash(Password);

        Assert.DoesNotContain(Password, hash);
        Assert.DoesNotContain("correct", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_AgainstGarbage_FailsRatherThanThrowing()
    {
        Assert.Equal(PasswordVerification.Failed, _passwords.Verify("not-a-hash", Password));
    }
}
