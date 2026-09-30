using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Application.Security;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;
using WTT.Infrastructure.Security;
using WTT.Infrastructure.Services;
using Xunit;

namespace WTT.Tests;

/// <summary>Real SQL Server database + real BCrypt; only the audit sink is faked.</summary>
[Trait("Category", "Integration")]
public sealed class AuthAndPersistenceTests : IAsyncLifetime
{
    private const string AdminPassword = "Admin#Secure2026";
    private readonly SqlServerTestDatabase _database = new();
    private IDbContextFactory<AppDbContext> _factory = null!;
    private readonly SecurityOptions _security = new() { BcryptWorkFactor = 10, MaxFailedLogins = 3, LockoutMinutes = 15, MinPasswordLength = 8 };
    private BcryptPasswordHasher _hasher = null!;
    private CurrentUserService _currentUser = null!;
    private AuthService _auth = null!;
    private UserService _users = null!;

    public async Task InitializeAsync()
    {
        var options = _database.CreateOptions();
        _factory = new TestFactory(options);
        await using (var db = await _factory.CreateDbContextAsync()) await db.Database.MigrateAsync();

        _hasher = new BcryptPasswordHasher(Options.Create(_security));
        _currentUser = new CurrentUserService();
        var audit = Substitute.For<IAuditService>();
        _auth = new AuthService(_factory, _hasher, _currentUser, audit, Options.Create(_security), NullLogger<AuthService>.Instance);
        _users = new UserService(_factory, _hasher, _currentUser, audit, Options.Create(_security), NullLogger<UserService>.Instance);

        var enrollment = new InitialAdminEnrollmentService(_factory, _hasher, Options.Create(_security));
        (await enrollment.IsRequiredAsync()).Should().BeTrue();
        await enrollment.EnrollAsync(new InitialAdminRequest("admin", "System Administrator", AdminPassword));
        (await enrollment.IsRequiredAsync()).Should().BeFalse();
    }

    public Task DisposeAsync() => _database.DeleteAsync();

    [Fact]
    public async Task Enrolled_admin_can_login_without_a_shared_default_password()
    {
        var result = await _auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        result.Succeeded.Should().BeTrue();
        result.MustChangePassword.Should().BeFalse();
        _currentUser.IsAuthenticated.Should().BeTrue();
        _currentUser.HasPermission(Permission.ManageUsers).Should().BeTrue();
    }

    [Fact]
    public async Task Password_change_survives_restart_and_old_password_is_rejected()
    {
        var login = await _auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        await _auth.ChangePasswordAsync(new ChangePasswordRequest(login.User!.Id, AdminPassword, "NewPass#2026"));

        // Simulate app restart: initializer runs again.
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        (await _auth.LoginAsync(new LoginRequest("admin", AdminPassword))).Succeeded.Should().BeFalse("old password must not work");
        var relogin = await _auth.LoginAsync(new LoginRequest("admin", "NewPass#2026"));
        relogin.Succeeded.Should().BeTrue();
        relogin.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public async Task Weak_password_is_rejected_by_policy()
    {
        var login = await _auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        var act = () => _auth.ChangePasswordAsync(new ChangePasswordRequest(login.User!.Id, AdminPassword, "short"));
        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task Account_locks_after_too_many_failures()
    {
        for (var i = 0; i < 3; i++)
            (await _auth.LoginAsync(new LoginRequest("admin", "wrong"))).Succeeded.Should().BeFalse();

        var locked = await _auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        locked.Succeeded.Should().BeFalse();
        locked.Error.Should().Contain("locked");
    }

    [Fact]
    public async Task Username_is_case_insensitive_and_unknown_user_gets_generic_error()
    {
        (await _auth.LoginAsync(new LoginRequest("ADMIN", AdminPassword))).Succeeded.Should().BeTrue();
        var unknown = await _auth.LoginAsync(new LoginRequest("nobody", "x"));
        unknown.Error.Should().Be("Invalid username or password.");
    }

    [Fact]
    public async Task Viewer_cannot_manage_users_and_last_admin_is_protected()
    {
        await _auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        var viewer = await _users.CreateAsync(new CreateUserRequest("viewer1", "Plant Viewer", "Viewer#123", UserRole.Viewer));
        var dto = (await _users.GetAllAsync()).Should().ContainSingle(u => u.Username == "viewer1").Subject;
        dto.MustChangePassword.Should().BeTrue();

        var admin = _currentUser.User!;
        var demote = () => _users.UpdateAsync(new UpdateUserRequest(admin.Id, admin.FullName, UserRole.Operator, true));
        await demote.Should().ThrowAsync<BusinessRuleException>().WithMessage("*administrator must remain*");

        await _auth.LoginAsync(new LoginRequest("viewer1", "Viewer#123"));
        var act = () => _users.GetAllAsync();
        await act.Should().ThrowAsync<AuthorizationException>();
        _ = viewer;
    }

    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
