using System.Security.Cryptography;
using WTT.Application.Interfaces;

namespace WTT.Infrastructure.Services;

public sealed class FileInstallationIdentity : IInstallationIdentity
{
    public Guid SourceId { get; }

    public FileInstallationIdentity()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WTT");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "installation-id");
        SourceId = ReadOrCreate(path);
    }

    public static Guid ReadOrCreate(string path)
    {
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing)) return existing;

        var value = Guid.NewGuid();
        var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}.tmp";
        File.WriteAllText(temp, value.ToString("D"));
        try
        {
            File.Move(temp, path, overwrite: false);
            return value;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            if (Guid.TryParse(File.ReadAllText(path).Trim(), out existing)) return existing;
            throw new InvalidDataException($"Installation identity is invalid: {path}");
        }
    }
}
