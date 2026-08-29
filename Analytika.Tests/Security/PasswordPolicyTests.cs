using Xunit;

namespace Analytika.Tests.Security;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Short1!", false)]        // too short (7 chars)
    [InlineData("Admin@123", true)]       // valid
    [InlineData("password", false)]       // no upper, digit, special
    [InlineData("PASSWORD1!", false)]     // no lowercase
    [InlineData("Password1", false)]      // no special char
    [InlineData("Pa1!Pa1!", true)]        // valid - 8 chars with all requirements
    public void Password_MeetsPolicy(string password, bool expected)
    {
        const int minLength = 8;
        var hasDigit = password.Any(char.IsDigit);
        var hasUpper = password.Any(char.IsUpper);
        var hasLower = password.Any(char.IsLower);
        var hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));
        var meetsLength = password.Length >= minLength;

        var valid = meetsLength && hasDigit && hasUpper && hasLower && hasSpecial;
        Assert.Equal(expected, valid);
    }
}
