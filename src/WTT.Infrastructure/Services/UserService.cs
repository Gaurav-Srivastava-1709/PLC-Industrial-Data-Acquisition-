using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Infrastructure.Data;
using BCrypt.Net;

namespace WTT.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<UserService> _logger;
    private readonly IAuthService _authService;

    public UserService(AppDbContext dbContext, ILogger<UserService> logger, IAuthService authService)
    {
        _dbContext = dbContext;
        _logger = logger;
        _authService = authService;
    }

    private void EnsureAdminRole()
    {
        if (_authService.CurrentUser?.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only administrators can perform user management operations.");
        }
    }

    public async Task<List<User>> GetAllUsersAsync()
    {
        EnsureAdminRole();
        return await _dbContext.Users.AsNoTracking().ToListAsync();
    }

    public async Task<User?> GetUserByIdAsync(int id)
    {
        EnsureAdminRole();
        return await _dbContext.Users.FindAsync(id);
    }

    public async Task<User> CreateUserAsync(User user, string plainTextPassword)
    {
        EnsureAdminRole();

        if (await _dbContext.Users.AnyAsync(u => u.Username.ToLower() == user.Username.ToLower()))
        {
            throw new InvalidOperationException($"Username '{user.Username}' already exists.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(plainTextPassword);
        user.CreatedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        user.MustChangePassword = true;

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        
        _logger.LogInformation("Admin {AdminUser} created new user {NewUser} with role {Role}", 
            _authService.CurrentUser?.Username, user.Username, user.Role);

        return user;
    }

    public async Task<User> UpdateUserAsync(User user)
    {
        EnsureAdminRole();

        var existingUser = await _dbContext.Users.FindAsync(user.Id) 
            ?? throw new KeyNotFoundException($"User with ID {user.Id} not found.");

        // Check if username is being changed to one that already exists
        if (existingUser.Username.ToLower() != user.Username.ToLower() && 
            await _dbContext.Users.AnyAsync(u => u.Username.ToLower() == user.Username.ToLower()))
        {
            throw new InvalidOperationException($"Username '{user.Username}' already exists.");
        }

        existingUser.Username = user.Username;
        existingUser.FullName = user.FullName;
        existingUser.Role = user.Role;
        existingUser.IsActive = user.IsActive;
        existingUser.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin {AdminUser} updated user {UpdatedUser}", 
            _authService.CurrentUser?.Username, existingUser.Username);

        return existingUser;
    }

    public async Task DeleteUserAsync(int id)
    {
        EnsureAdminRole();

        var user = await _dbContext.Users.FindAsync(id)
            ?? throw new KeyNotFoundException($"User with ID {id} not found.");

        if (user.Id == _authService.CurrentUser?.Id)
        {
            throw new InvalidOperationException("You cannot delete your own admin account.");
        }

        _dbContext.Users.Remove(user);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin {AdminUser} deleted user {DeletedUser}", 
            _authService.CurrentUser?.Username, user.Username);
    }

    public async Task<bool> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        // Users can change their own password, or Admin can do it
        if (_authService.CurrentUser?.Id != userId && _authService.CurrentUser?.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("You can only change your own password.");
        }

        var user = await _dbContext.Users.FindAsync(userId)
            ?? throw new KeyNotFoundException($"User with ID {userId} not found.");

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            throw new InvalidOperationException("Current password is incorrect.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.MustChangePassword = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Password changed successfully for user {User}", user.Username);
        return true;
    }

    public async Task<bool> AdminResetPasswordAsync(int userId, string newPassword)
    {
        EnsureAdminRole();

        var user = await _dbContext.Users.FindAsync(userId)
            ?? throw new KeyNotFoundException($"User with ID {userId} not found.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.MustChangePassword = true; // Force them to change it on next login
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin {AdminUser} reset password for user {TargetUser}", 
            _authService.CurrentUser?.Username, user.Username);
            
        return true;
    }
}
