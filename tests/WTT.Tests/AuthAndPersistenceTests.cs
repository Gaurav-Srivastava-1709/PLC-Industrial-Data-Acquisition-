using FluentAssertions;
using Microsoft.Data.Sqlite;
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

/// <summary>Real SQLite (in-memory, shared connection) + real BCrypt; only the audit sink is faked.</summary>
public sealed class AuthAndPersistenceTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private IDbContextFactory<AppDbContext> _factory = null!;
    private readonly SecurityOptions _security = new() { BcryptWorkFactor = 10, MaxFailedLogins = 3, LockoutMinutes = 15, MinPasswordLength = 8 };
    private BcryptPasswordHasher _hasher = null!;
    private CurrentUserService _currentUser = null!;
    private AuthService _auth = null!;
    private UserService _users = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        _factory = new TestFactory(options);
        await using (var db = await _factory.CreateDbContextAsync()) await db.Database.MigrateAsync();

        _hasher = new BcryptPasswordHasher(Options.Create(_security));
        _currentUser = new CurrentUserService();
        var audit = Substitute.For<IAuditService>();
        _auth = new AuthService(_factory, _hasher, _currentUser, audit, Options.Create(_security), NullLogger<AuthService>.Instance);
        _users = new UserService(_factory, _hasher, _currentUser, audit, Options.Create(_security), NullLogger<UserService>.Instance);

        var init = new DatabaseInitializer(_factory, _hasher, NullLogger<DatabaseInitializer>.Instance);
        (await init.InitializeAsync()).Should().BeTrue("first run seeds the admin");
        (await init.InitializeAsync()).Should().BeFalse("second run must NOT reseed or reset anything");
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    [Fact]
    public async Task Default_admin_can_login_and_must_change_password()
    {
        var result = await _auth.LoginAsync(new LoginRequest(DatabaseInitializer.DefaultAdminUsername, DatabaseInitializer.DefaultAdminPassword));
        result.Succeeded.Should().BeTrue();
        result.MustChangePassword.Should().BeTrue();
        _currentUser.IsAuthenticated.Should().BeTrue();
        _currentUser.HasPermission(Permission.ManageUsers).Should().BeTrue();
    }

    [Fact]
    public async Task Password_change_survives_restart_and_old_password_is_rejected()
    {
        var login = await _auth.LoginAsync(new LoginRequest("admin", DatabaseInitializer.DefaultAdminPassword));
        await _auth.ChangePasswordAsync(new ChangePasswordRequest(login.User!.Id, DatabaseInitializer.DefaultAdminPassword, "NewPass#2026"));

        // Simulate app restart: initializer runs again.
        await new DatabaseInitializer(_factory, _hasher, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        (await _auth.LoginAsync(new LoginRequest("admin", DatabaseInitializer.DefaultAdminPassword))).Succeeded.Should().BeFalse("old password must not work");
        var relogin = await _auth.LoginAsync(new LoginRequest("admin", "NewPass#2026"));
        relogin.Succeeded.Should().BeTrue();
        relogin.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public async Task Weak_password_is_rejected_by_policy()
    {
        var login = await _auth.LoginAsync(new LoginRequest("admin", DatabaseInitializer.DefaultAdminPassword));
        var act = () => _auth.ChangePasswordAsync(new ChangePasswordRequest(login.User!.Id, DatabaseInitializer.DefaultAdminPassword, "short"));
        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task Account_locks_after_too_many_failures()
    {
        for (var i = 0; i < 3; i++)
            (await _auth.LoginAsync(new LoginRequest("admin", "wrong"))).Succeeded.Should().BeFalse();

        var locked = await _auth.LoginAsync(new LoginRequest("admin", DatabaseInitializer.DefaultAdminPassword));
        locked.Succeeded.Should().BeFalse();
        locked.Error.Should().Contain("locked");
    }

    [Fact]
    public async Task Username_is_case_insensitive_and_unknown_user_gets_generic_error()
    {
        (await _auth.LoginAsync(new LoginRequest("ADMIN", DatabaseInitializer.DefaultAdminPassword))).Succeeded.Should().BeTrue();
        var unknown = await _auth.LoginAsync(new LoginRequest("nobody", "x"));
        unknown.Error.Should().Be("Invalid username or password.");
    }

    [Fact]
    public async Task Viewer_cannot_manage_users_and_last_admin_is_protected()
    {
        await _auth.LoginAsync(new LoginRequest("admin", DatabaseInitializer.DefaultAdminPassword));
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
