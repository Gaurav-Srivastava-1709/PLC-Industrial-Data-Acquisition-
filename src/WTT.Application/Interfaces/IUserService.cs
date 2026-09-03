using System.Collections.Generic;
using System.Threading.Tasks;
using WTT.Domain.Entities;

namespace WTT.Application.Interfaces;

/// <summary>
/// IUserService: Admin ko users CRUD manage karne dega.
/// </summary>
public interface IUserService
{
    Task<List<User>> GetAllUsersAsync();
    Task<User?> GetUserByIdAsync(int id);
    Task<User> CreateUserAsync(User user, string plainTextPassword);
    Task<User> UpdateUserAsync(User user);
    Task DeleteUserAsync(int id);
    
    /// <summary>User ka password change karne ke liye</summary>
    Task<bool> ChangePasswordAsync(int userId, string currentPassword, string newPassword);
    
    /// <summary>Admin dwara kisi bhi user ka password reset karne ke liye</summary>
    Task<bool> AdminResetPasswordAsync(int userId, string newPassword);
}
