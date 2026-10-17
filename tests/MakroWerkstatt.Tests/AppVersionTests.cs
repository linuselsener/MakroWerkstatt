using System.Reflection;
using Xunit;

namespace MakroWerkstatt.Tests;

public sealed class AppVersionTests
{
    [Fact]
    public void Version_is_readable_and_uses_three_numeric_components()
    {
        Assert.Matches("^\\d+\\.\\d+\\.\\d+$", AppVersion.Current);
        Assert.Equal(AppVersion.Current, typeof(AppVersion).Assembly.GetName().Version?.ToString(3));
        var informationalVersion = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.StartsWith(AppVersion.Current, informationalVersion ?? string.Empty);
    }
}
