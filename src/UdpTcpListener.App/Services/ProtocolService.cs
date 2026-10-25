using System.Diagnostics;
using UdpTcpListener.Core.Decoding;

namespace UdpTcpListener.App.Services;

/// <summary>
/// Owns the decoder registry: built-in decoders plus the user's custom protocol definitions
/// from <see cref="CustomProtocolLoader.DefaultDirectory"/>. Writes a commented example there on first run.
/// </summary>
public sealed class ProtocolService
{
    private const string ExampleFileName = "example-sensor.json";

    public ProtocolService(string? directory = null)
    {
        Directory = directory ?? CustomProtocolLoader.DefaultDirectory;
        EnsureExample();
        Reload();
    }

    public string Directory { get; }

    public DecoderRegistry Registry { get; private set; } = DecoderRegistry.Default;

    public IReadOnlyList<string> CustomNames { get; private set; } = [];

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public event EventHandler? Reloaded;

    public void Reload()
    {
        var result = CustomProtocolLoader.LoadDirectory(Directory);
        Registry = new DecoderRegistry(result.Decoders);
        CustomNames = result.Decoders.Select(d => d.Name).ToList();
        Errors = result.Errors;
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureExample()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
                return; // the user may have deleted the example on purpose
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, ExampleFileName), CustomProtocolLoader.ExampleDefinition + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not create the protocols folder: {ex.Message}");
        }
    }
}
