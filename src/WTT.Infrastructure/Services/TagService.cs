using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Infrastructure.Data;

namespace WTT.Infrastructure.Services;

/// <summary>
/// TagService: EF Core implementation for managing PLC tags in SQLite.
/// </summary>
public class TagService : ITagService
{
    private readonly AppDbContext _context;

    public TagService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<DeviceTag>> GetAllTagsAsync()
    {
        return await _context.DeviceTags
            .Include(t => t.Device)
            .OrderBy(t => t.TagName)
            .ToListAsync();
    }

    public async Task<List<DeviceTag>> GetTagsByDeviceIdAsync(int deviceId)
    {
        return await _context.DeviceTags
            .Include(t => t.Device)
            .Where(t => t.DeviceId == deviceId)
            .OrderBy(t => t.TagName)
            .ToListAsync();
    }

    public async Task<DeviceTag?> GetTagByIdAsync(int id)
    {
        return await _context.DeviceTags
            .Include(t => t.Device)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task AddTagAsync(DeviceTag tag)
    {
        tag.CreatedAt = DateTime.UtcNow;
        _context.DeviceTags.Add(tag);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateTagAsync(DeviceTag tag)
    {
        var existing = await _context.DeviceTags.FindAsync(tag.Id);
        if (existing == null) return;

        existing.TagName = tag.TagName;
        existing.Address = tag.Address;
        existing.DataType = tag.DataType;
        existing.Description = tag.Description;
        existing.IsActive = tag.IsActive;
        existing.DeviceId = tag.DeviceId;

        await _context.SaveChangesAsync();
    }

    public async Task DeleteTagAsync(int id)
    {
        var tag = await _context.DeviceTags.FindAsync(id);
        if (tag != null)
        {
            _context.DeviceTags.Remove(tag);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<int> GetActiveTagCountAsync()
    {
        return await _context.DeviceTags.CountAsync(t => t.IsActive);
    }
}
