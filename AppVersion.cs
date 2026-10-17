using System.Reflection;

namespace MakroWerkstatt;

public static class AppVersion
{
    private static readonly Version AssemblyVersion = typeof(AppVersion).Assembly.GetName().Version
        ?? throw new InvalidOperationException("Die Assembly-Version ist nicht verfügbar.");

    public static string Current => AssemblyVersion.ToString(3);
    public static string WindowTitle => $"MakroWerkstatt {Current}";
}
