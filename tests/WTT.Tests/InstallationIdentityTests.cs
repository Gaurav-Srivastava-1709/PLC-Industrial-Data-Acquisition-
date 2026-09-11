using FluentAssertions;
using WTT.Infrastructure.Services;
using Xunit;

namespace WTT.Tests;

public sealed class InstallationIdentityTests
{
    [Fact]
    public void Identity_is_stable_and_unique_per_path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wtt-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var firstPath = Path.Combine(root, "one");
            var secondPath = Path.Combine(root, "two");
            var first = FileInstallationIdentity.ReadOrCreate(firstPath);
            FileInstallationIdentity.ReadOrCreate(firstPath).Should().Be(first);
            FileInstallationIdentity.ReadOrCreate(secondPath).Should().NotBe(first);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
