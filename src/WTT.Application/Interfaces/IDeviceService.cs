using System.Collections.Generic;
using System.Threading.Tasks;
using WTT.Domain.Entities;

namespace WTT.Application.Interfaces;

public interface IDeviceService
{
    Task<List<PlcDevice>> GetAllDevicesAsync();
    Task<PlcDevice?> GetDeviceByIdAsync(int id);
    Task<PlcDevice> AddDeviceAsync(PlcDevice device);
    Task<PlcDevice> UpdateDeviceAsync(PlcDevice device);
    Task DeleteDeviceAsync(int id);
}
