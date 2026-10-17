using System.IO;

namespace MakroWerkstatt;

public sealed class MacroExportFileWriter
{
    private readonly IExportFileOperations _fileOperations;

    public MacroExportFileWriter() : this(PhysicalExportFileOperations.Instance) { }

    internal MacroExportFileWriter(IExportFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(fileOperations);
        _fileOperations = fileOperations;
    }

    public void WriteAtomically(string destination, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(content);
        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination)
            ?? throw new IOException("Der Zielordner ist ungültig.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            _fileOperations.WriteAllBytes(temporary, content);
            if (File.Exists(fullDestination)) _fileOperations.Replace(temporary, fullDestination);
            else _fileOperations.Move(temporary, fullDestination);
        }
        finally
        {
            _fileOperations.TryDelete(temporary);
        }
    }
}

internal interface IExportFileOperations
{
    void WriteAllBytes(string path, byte[] content);
    void Replace(string temporaryPath, string destinationPath);
    void Move(string temporaryPath, string destinationPath);
    void TryDelete(string path);
}

internal sealed class PhysicalExportFileOperations : IExportFileOperations
{
    public static PhysicalExportFileOperations Instance { get; } = new();
    private PhysicalExportFileOperations() { }

    public void WriteAllBytes(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    public void Replace(string temporaryPath, string destinationPath) => File.Replace(temporaryPath, destinationPath, null);
    public void Move(string temporaryPath, string destinationPath) => File.Move(temporaryPath, destinationPath);

    public void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
