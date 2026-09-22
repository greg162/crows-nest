using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;

namespace Crowsnest.Device.Protocol;

/// <summary>Serialises one message per line, UTF-8, newline-terminated (spec §6.1).</summary>
public sealed class NdjsonFrameWriter(PipeWriter output)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = true };

    private readonly PipeWriter _output = output;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Writes and flushes one frame. Safe to call from several tasks.</summary>
    public async ValueTask WriteAsync(HostMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await _gate.WaitAsync(ct);
        try
        {
            Write(message, ProtocolJsonContext.Default.HostMessage);
            await _output.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The device half of the link, used by Crowsnest.DeviceSimulator.</summary>
    public async ValueTask WriteAsync(DeviceMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await _gate.WaitAsync(ct);
        try
        {
            Write(message, ProtocolJsonContext.Default.DeviceMessage);
            await _output.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Write<T>(T message, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        using var writer = new Utf8JsonWriter(_output, WriterOptions);
        JsonSerializer.Serialize(writer, message, typeInfo);
        writer.Flush();
        _output.Write("\n"u8);
    }
}
