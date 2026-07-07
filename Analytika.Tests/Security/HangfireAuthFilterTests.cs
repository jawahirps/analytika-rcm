using FluentAssertions;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Analytika.Tests.Security;

public class HangfireAuthFilterTests
{
    private readonly HangfireAuthorizationFilter _sut = new();

    private static DashboardContext CreateDashboardContext(bool isAuthenticated)
    {
        var identity = new ClaimsIdentity(
            isAuthenticated ? new[] { new Claim(ClaimTypes.Name, "admin@test.com") } : Array.Empty<Claim>(),
            isAuthenticated ? "TestAuth" : null);
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };

        var storageMock = new Mock<JobStorage>();
        var options = new Hangfire.DashboardOptions();

        return new AspNetCoreDashboardContext(
            storageMock.Object, options, httpContext);
    }

    [Fact]
    public void Authorize_AuthenticatedUser_ReturnsTrue()
    {
        var context = CreateDashboardContext(isAuthenticated: true);

        var result = _sut.Authorize(context);

        result.Should().BeTrue();
    }

    [Fact]
    public void Authorize_UnauthenticatedUser_ReturnsFalse()
    {
        var context = CreateDashboardContext(isAuthenticated: false);

        var result = _sut.Authorize(context);

        result.Should().BeFalse();
    }

    [Fact]
    public void Authorize_NullIdentity_ReturnsFalse()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(),
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        var storageMock = new Mock<JobStorage>();
        var options = new Hangfire.DashboardOptions();
        var context = new AspNetCoreDashboardContext(
            storageMock.Object, options, httpContext);

        var result = _sut.Authorize(context);

        result.Should().BeFalse();
    }
}
