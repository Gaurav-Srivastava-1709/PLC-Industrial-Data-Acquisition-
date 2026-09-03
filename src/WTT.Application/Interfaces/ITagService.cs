using System.Collections.Generic;
using System.Threading.Tasks;
using WTT.Domain.Entities;

namespace WTT.Application.Interfaces;

/// <summary>
/// ITagService: PLC Tags ke CRUD operations handle karta hai.
/// </summary>
public interface ITagService
{
    Task<List<DeviceTag>> GetAllTagsAsync();
    Task<List<DeviceTag>> GetTagsByDeviceIdAsync(int deviceId);
    Task<DeviceTag?> GetTagByIdAsync(int id);
    Task AddTagAsync(DeviceTag tag);
    Task UpdateTagAsync(DeviceTag tag);
    Task DeleteTagAsync(int id);
    Task<int> GetActiveTagCountAsync();
}
