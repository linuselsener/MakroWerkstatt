using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class MacroExportFileWriterTests
{
    [Fact]
    public void Write_atomically_leaves_no_temporary_file_after_success()
    {
        using var context = new TestContext();
        var destination = Path.Combine(context.RootPath, "exports", "backup.mwexport");
        var content = new byte[] { 0x31, 0x42, 0x53 };

        new MacroExportFileWriter().WriteAtomically(destination, content);

        Assert.Equal(content, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
    }

    [Fact]
    public void Write_atomically_preserves_existing_destination_and_cleans_temp_after_replace_failure()
    {
        using var context = new TestContext();
        var destination = Path.Combine(context.RootPath, "existing.mwexport");
        var original = new byte[] { 0x19, 0x28, 0x37 };
        File.WriteAllBytes(destination, original);
        var writer = new MacroExportFileWriter(new ThrowOnReplaceOperations());

        Assert.Throws<IOException>(() => writer.WriteAtomically(destination, new byte[] { 0x41, 0x52 }));

        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(context.RootPath, "*.tmp"));
    }

    private sealed class ThrowOnReplaceOperations : IExportFileOperations
    {
        private readonly IExportFileOperations _inner = PhysicalExportFileOperations.Instance;

        public void WriteAllBytes(string path, byte[] content) => _inner.WriteAllBytes(path, content);
        public void Replace(string temporaryPath, string destinationPath) => throw new IOException("synthetic replace failure");
        public void Move(string temporaryPath, string destinationPath) => _inner.Move(temporaryPath, destinationPath);
        public void TryDelete(string path) => _inner.TryDelete(path);
    }
}
