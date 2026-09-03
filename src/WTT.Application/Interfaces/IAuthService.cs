using System.Threading.Tasks;
using WTT.Domain.Entities;

namespace WTT.Application.Interfaces;

public interface IAuthService
{
    Task<User?> AuthenticateAsync(string username, string password);
    Task LogoutAsync();
    User? CurrentUser { get; }
    bool IsAuthenticated { get; }
}
