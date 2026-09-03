using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Infrastructure.Data;

namespace WTT.Infrastructure.Services;

public class DeviceService : IDeviceService
{
    private readonly AppDbContext _dbContext;

    public DeviceService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<PlcDevice>> GetAllDevicesAsync()
    {
        return await _dbContext.PlcDevices.ToListAsync();
    }

    public async Task<PlcDevice?> GetDeviceByIdAsync(int id)
    {
        return await _dbContext.PlcDevices.FindAsync(id);
    }

    public async Task<PlcDevice> AddDeviceAsync(PlcDevice device)
    {
        _dbContext.PlcDevices.Add(device);
        await _dbContext.SaveChangesAsync();
        return device;
    }

    public async Task<PlcDevice> UpdateDeviceAsync(PlcDevice device)
    {
        _dbContext.PlcDevices.Update(device);
        await _dbContext.SaveChangesAsync();
        return device;
    }

    public async Task DeleteDeviceAsync(int id)
    {
        var device = await _dbContext.PlcDevices.FindAsync(id);
        if (device != null)
        {
            _dbContext.PlcDevices.Remove(device);
            await _dbContext.SaveChangesAsync();
        }
    }
}
