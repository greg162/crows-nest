using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Crowsnest.Device.Protocol;

/// <summary>
/// Splits a byte stream into newline-delimited frames (spec §6.1).
///
/// Robustness is the point: a frame longer than <see cref="MaxFrameBytes"/> is discarded
/// up to the next newline rather than growing the buffer without bound, and a partial
/// frame at end-of-stream is dropped. Garbage bytes cost one frame, never the link.
/// </summary>
public sealed class NdjsonFrameReader(PipeReader input, int maxFrameBytes = 16 * 1024)
{
    private const byte Newline = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

    private readonly PipeReader _input = input;

    public int MaxFrameBytes { get; } = maxFrameBytes;

    /// <summary>Frames discarded for exceeding <see cref="MaxFrameBytes"/>. Diagnostics only.</summary>
    public long OversizedFrames { get; private set; }

    public async IAsyncEnumerable<byte[]> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        bool discarding = false;

        while (true)
        {
            ReadResult result = await _input.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (TrySplit(ref buffer, out ReadOnlySequence<byte> line))
            {
                if (discarding)
                {
                    // This "line" is the tail of a frame we already gave up on.
                    discarding = false;
                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length > MaxFrameBytes)
                {
                    // A complete but absurd frame. Decoding it would cost more than dropping it.
                    OversizedFrames++;
                    continue;
                }

                yield return Trim(line);
            }

            // No newline in an over-long buffer: abandon the frame and resynchronise.
            if (!discarding && buffer.Length > MaxFrameBytes)
            {
                OversizedFrames++;
                discarding = true;
                _input.AdvanceTo(buffer.End);
            }
            else
            {
                _input.AdvanceTo(buffer.Start, buffer.End);
            }

            if (result.IsCompleted)
            {
                yield break;
            }
        }
    }

    private static bool TrySplit(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        SequencePosition? position = buffer.PositionOf(Newline);
        if (position is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    private static byte[] Trim(ReadOnlySequence<byte> line)
    {
        // Tolerate CRLF: a terminal or a serial monitor on the other end may add the CR.
        byte[] bytes = line.ToArray();
        return bytes.Length > 0 && bytes[^1] == CarriageReturn ? bytes[..^1] : bytes;
    }
}
