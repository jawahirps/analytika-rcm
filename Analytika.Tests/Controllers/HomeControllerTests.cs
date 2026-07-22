using Analytika.Controllers;
using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Analytika.Tests.Controllers;

public class HomeControllerTests : IDisposable
{
    private readonly Mock<SignInManager<ApplicationUser>> _signInManagerMock;
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
    private readonly Mock<IDashboardService> _dashboardMock;
    private readonly Mock<ILogger<HomeController>> _loggerMock;
    private readonly IMemoryCache _cache;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly HomeController _sut;

    public HomeControllerTests()
    {
        // Set up UserManager mock
        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _userManagerMock = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object,
            null!, null!, null!, null!, null!, null!, null!, null!);

        // Set up SignInManager mock
        var contextAccessorMock = new Mock<IHttpContextAccessor>();
        var claimsFactoryMock = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        _signInManagerMock = new Mock<SignInManager<ApplicationUser>>(
            _userManagerMock.Object,
            contextAccessorMock.Object,
            claimsFactoryMock.Object,
            null!, null!, null!, null!);

        _dashboardMock = new Mock<IDashboardService>();
        _loggerMock = new Mock<ILogger<HomeController>>();
        _cache = new MemoryCache(new MemoryCacheOptions());

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _sut = new HomeController(
            _signInManagerMock.Object,
            _userManagerMock.Object,
            _dashboardMock.Object,
            _db,
            _cache,
            _loggerMock.Object);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private void SetUser(bool authenticated, string? name = "test@test.com")
    {
        var claims = new List<Claim>();
        if (name != null)
            claims.Add(new Claim(ClaimTypes.Name, name));

        var identity = new ClaimsIdentity(claims, authenticated ? "TestAuth" : null);
        var principal = new ClaimsPrincipal(identity);

        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public void Index_Get_UnauthenticatedUser_ReturnsLandingView()
    {
        // Arrange — "/" serves the marketing landing page since the routing
        // rewire (login moved to /login → Login()).
        SetUser(authenticated: false);

        // Act
        var result = _sut.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        viewResult.ViewName.Should().Be("Landing");
    }

    [Fact]
    public void Index_Get_AuthenticatedUser_RedirectsToDashboard()
    {
        // Arrange
        SetUser(authenticated: true);

        // Act
        var result = _sut.Index();

        // Assert
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Dashboard");
    }

    [Fact]
    public async Task LogOut_Post_RedirectsToLogin()
    {
        // Arrange
        SetUser(authenticated: true);
        _signInManagerMock.Setup(s => s.SignOutAsync()).Returns(Task.CompletedTask);

        // Act
        var result = await _sut.LogOut();

        // Assert — logout lands on the login page (not the marketing landing).
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Login");
        _signInManagerMock.Verify(s => s.SignOutAsync(), Times.Once);
    }

    [Fact]
    public async Task Index_Post_LockedOut_ReturnsViewWithLockoutMessage()
    {
        // Arrange
        SetUser(authenticated: false);
        var model = new LoginViewModel { Email = "locked@test.com", Password = "password" };

        _userManagerMock
            .Setup(u => u.FindByEmailAsync("locked@test.com"))
            .ReturnsAsync(new ApplicationUser { Email = "locked@test.com", IsActive = true });

        _signInManagerMock
            .Setup(s => s.PasswordSignInAsync("locked@test.com", "password", true, true))
            .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.LockedOut);

        // Act
        var result = await _sut.Login(model);

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        viewResult.Model.Should().BeOfType<LoginViewModel>();
        _sut.ModelState.ErrorCount.Should().BeGreaterThan(0);
        _sut.ModelState[string.Empty]!.Errors
            .Should().Contain(e => e.ErrorMessage.Contains("locked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Index_Post_InactiveUser_ReturnsViewWithInactiveMessage()
    {
        // Arrange
        SetUser(authenticated: false);
        var model = new LoginViewModel { Email = "inactive@test.com", Password = "password" };

        _userManagerMock
            .Setup(u => u.FindByEmailAsync("inactive@test.com"))
            .ReturnsAsync(new ApplicationUser { Email = "inactive@test.com", IsActive = false });

        // Act
        var result = await _sut.Login(model);

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        _sut.ModelState[string.Empty]!.Errors
            .Should().Contain(e => e.ErrorMessage.Contains("inactive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Error_ReturnsViewResult()
    {
        // Arrange
        SetUser(authenticated: false);

        // Act
        var result = _sut.Error();

        // Assert
        result.Should().BeOfType<ViewResult>();
    }
}
