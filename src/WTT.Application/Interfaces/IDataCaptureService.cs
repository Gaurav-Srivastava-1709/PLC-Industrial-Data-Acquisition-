using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WTT.Domain.Entities;

namespace WTT.Application.Interfaces;

/// <summary>
/// TagValueChangedEventArgs: Jab koi tag read hota hai, to live event ke through UI ko latest value milti hai.
/// </summary>
public class TagValueChangedEventArgs : EventArgs
{
    public int TagId { get; set; }
    public string TagName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Quality { get; set; } = "Good";
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// IDataCaptureService: Background polling service ka public contract.
/// </summary>
public interface IDataCaptureService
{
    bool IsRunning { get; }
    int PollingIntervalSeconds { get; set; }
    int RetentionDays { get; set; }
    
    event EventHandler<TagValueChangedEventArgs>? TagValueChanged;
    event EventHandler<bool>? ServiceStatusChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<int> GetTodayReadingsCountAsync();
    Task<Dictionary<int, string>> GetLatestValuesAsync();
}
