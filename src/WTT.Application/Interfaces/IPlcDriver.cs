using System.Threading.Tasks;
using WTT.Domain.Entities;

namespace WTT.Application.Interfaces;

public interface IPlcDriver
{
    Task ConnectAsync(PlcDevice device);
    Task DisconnectAsync();
    bool IsConnected { get; }
    
    // Very simple read for now
    Task<object> ReadTagAsync(string address);
    Task WriteTagAsync(string address, object value);
}
