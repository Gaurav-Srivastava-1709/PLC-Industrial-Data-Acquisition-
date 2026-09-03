using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Infrastructure.Data;

namespace WTT.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<AuthService> _logger;

    public User? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser != null;

    public AuthService(AppDbContext dbContext, ILogger<AuthService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<User?> AuthenticateAsync(string username, string password)
    {
        try
        {
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
            
            if (user == null)
            {
                _logger.LogWarning("Failed login attempt for unknown or inactive user: {Username}", username);
                return null;
            }

            // Verify BCrypt Hash
            if (BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                user.LastLoginAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();

                CurrentUser = user;
                _logger.LogInformation("User {Username} logged in successfully.", username);
                return user;
            }

            _logger.LogWarning("Failed login attempt due to invalid password for user: {Username}", username);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred during authentication.");
            throw;
        }
    }

    public Task LogoutAsync()
    {
        _logger.LogInformation("User {Username} logged out.", CurrentUser?.Username);
        CurrentUser = null;
        return Task.CompletedTask;
    }
}
