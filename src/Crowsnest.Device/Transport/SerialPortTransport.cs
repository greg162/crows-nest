using System.IO.Pipelines;
using System.IO.Ports;

namespace Crowsnest.Device.Transport;

/// <summary>
/// USB CDC transport (spec §6). The CrowPanel exposes the ESP32-S3's native
/// USB-Serial/JTAG peripheral rather than a UART bridge (spec §9.6 F4), so the baud rate
/// is meaningless — the value below is what Windows wants to hear, not a line rate.
/// </summary>
public sealed class SerialPortTransport(string portName, int baudRate = 921_600) : IDeviceTransport
{
    private SerialPort? _port;
    private PipeReader? _input;
    private PipeWriter? _output;

    public string PortName { get; } = portName;

    public PipeReader Input => _input ?? throw new InvalidOperationException("Connect first.");

    public PipeWriter Output => _output ?? throw new InvalidOperationException("Connect first.");

    public Task ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var port = new SerialPort(PortName, baudRate, Parity.None, 8, StopBits.One)
        {
            // The link is framed by newlines and has its own heartbeat; never let the
            // stream time out underneath the pipe.
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
            // NEVER assert these on this board. The ESP32-S3's USB-Serial/JTAG peripheral
            // maps the CDC control lines onto the reset circuit — RTS drives EN and DTR
            // drives GPIO 0, which is exactly how esptool reboots the chip into download
            // mode. Setting RtsEnable holds the panel in reset, and the symptom is a board
            // that enumerates perfectly and never says a word.
            DtrEnable = false,
            RtsEnable = false,
        };

        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();

        _port = port;
        _input = PipeReader.Create(port.BaseStream, new StreamPipeReaderOptions(leaveOpen: true));
        _output = PipeWriter.Create(port.BaseStream, new StreamPipeWriterOptions(leaveOpen: true));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {

        // Teardown runs after the cable is pulled, when every one of these can throw
        // ("A device which does not exist was specified"): completing the writer flushes
        // whatever is still buffered into a port that has gone. Each step is best effort,
        // or one failure leaks the rest and takes the host down with it.
        Try(() => _input?.Complete());
        Try(() => _output?.Complete());
        Try(() => _port?.Dispose());
        _port = null;
        return ValueTask.CompletedTask;
    }

    private static void Try(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // The port is already gone.
        }
    }
}
