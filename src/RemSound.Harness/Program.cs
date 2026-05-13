using System.Net;
using NAudio.CoreAudioApi;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

return args[0].ToLowerInvariant() switch
{
    "send" => RunSend(args),
    "recv" or "receive" => RunReceive(args),
    "devices" => ListDevices(),
    "loopback" => RunLoopback(args),
    _ => Unknown(args[0]),
};

static int Unknown(string verb)
{
    Console.Error.WriteLine($"Unknown verb '{verb}'.");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("RemSound.Harness — minimal command-line test for the new audio engine.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  RemSound.Harness devices");
    Console.WriteLine("      List Windows render devices and their IDs.");
    Console.WriteLine();
    Console.WriteLine("  RemSound.Harness send <ip:port> [--opus] [--device <id>]");
    Console.WriteLine("      Capture the default (or selected) render device and send to the receiver.");
    Console.WriteLine();
    Console.WriteLine("  RemSound.Harness recv [--port N] [--device <id>] [--max-latency N]");
    Console.WriteLine("      Listen for RemSound packets and play them through the default (or selected) device.");
    Console.WriteLine();
    Console.WriteLine("  RemSound.Harness loopback [--opus] [--max-latency N]");
    Console.WriteLine("      Run sender + receiver on localhost. Useful for sanity checks; will feed the");
    Console.WriteLine("      default output back into the system, so use headphones and a different render device for the receiver.");
    Console.WriteLine();
    Console.WriteLine("Press Ctrl+C to stop in any mode.");
}

static int ListDevices()
{
    var enumerator = new MMDeviceEnumerator();
    var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
    Console.WriteLine($"{"State",-8}{"Default",-9}{"Name"}");
    foreach (var device in devices)
    {
        var marker = device.ID == defaultRender.ID ? "yes" : "";
        Console.WriteLine($"{device.State,-8}{marker,-9}{device.FriendlyName}");
        Console.WriteLine($"        ID: {device.ID}");
        device.Dispose();
    }
    defaultRender.Dispose();
    return 0;
}

static int RunSend(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Missing destination. Example:  RemSound.Harness send 192.168.1.42:47830");
        return 1;
    }
    if (!TryParseEndpoint(args[1], out var target))
    {
        Console.Error.WriteLine($"Could not parse '{args[1]}' as ip:port.");
        return 1;
    }

    var codec = args.Contains("--opus") ? AudioTransportCodec.Opus : AudioTransportCodec.Pcm;
    var deviceId = ParseOption(args, "--device");

    using var sender = new AudioSender();
    if (deviceId is not null)
    {
        sender.Configure(new[] { new CaptureSourceSpec(deviceId, CaptureKind.Loopback, deviceId) });
    }
    sender.ConfigureCodec(codec);
    sender.SetReceivers(new[] { target });
    sender.Start();

    Console.WriteLine($"Sending {codec} from \"{sender.CaptureDeviceName}\" → {target}. Press Ctrl+C to stop.");
    using var quit = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
    var lastPackets = 0L;
    while (!quit.Wait(1000))
    {
        var p = sender.PacketsSent;
        var rate = p - lastPackets;
        lastPackets = p;
        Console.WriteLine($"[send] packets={p}  /sec={rate}  bytes={sender.BytesSent}  uptime={sender.Uptime:hh\\:mm\\:ss}");
    }
    sender.Stop();
    return 0;
}

static int RunReceive(string[] args)
{
    var port = int.TryParse(ParseOption(args, "--port"), out var p) ? p : RemPacket.DefaultPort;
    var deviceId = ParseOption(args, "--device");
    var maxLatency = int.TryParse(ParseOption(args, "--max-latency"), out var ml) ? ml : 80;

    using var receiver = new AudioReceiver();
    if (deviceId is not null) receiver.SetOutputDevices(new[] { deviceId });
    receiver.MaxLatencyMs = maxLatency;
    receiver.Start(port);

    Console.WriteLine($"Listening on UDP :{port}, output \"{receiver.OutputDeviceName}\", max latency {maxLatency} ms. Press Ctrl+C to stop.");
    using var quit = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
    var lastPackets = 0L;
    while (!quit.Wait(1000))
    {
        var pk = receiver.PacketsReceived;
        var rate = pk - lastPackets;
        lastPackets = pk;
        Console.WriteLine($"[recv] packets={pk}  /sec={rate}  buffer={receiver.CurrentBufferMs}ms  underruns={receiver.Underruns}  drops={receiver.Drops}");
    }
    receiver.Stop();
    return 0;
}

static int RunLoopback(string[] args)
{
    var codec = args.Contains("--opus") ? AudioTransportCodec.Opus : AudioTransportCodec.Pcm;
    var maxLatency = int.TryParse(ParseOption(args, "--max-latency"), out var ml) ? ml : 80;

    using var receiver = new AudioReceiver();
    receiver.MaxLatencyMs = maxLatency;
    receiver.Start();

    using var sender = new AudioSender();
    sender.ConfigureCodec(codec);
    sender.SetReceivers(new[] { new IPEndPoint(IPAddress.Loopback, RemPacket.DefaultPort) });
    sender.Start();

    Console.WriteLine($"Loopback running, codec={codec}, max latency={maxLatency} ms. Ctrl+C to stop.");
    using var quit = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
    while (!quit.Wait(1000))
    {
        Console.WriteLine($"send pkt={sender.PacketsSent}  recv pkt={receiver.PacketsReceived}  buf={receiver.CurrentBufferMs}ms  under={receiver.Underruns}  drop={receiver.Drops}");
    }
    sender.Stop();
    receiver.Stop();
    return 0;
}

static bool TryParseEndpoint(string text, out IPEndPoint endpoint)
{
    endpoint = new IPEndPoint(IPAddress.Loopback, 0);
    var split = text.Split(':');
    if (split.Length != 2) return false;
    if (!IPAddress.TryParse(split[0], out var ip)) return false;
    if (!int.TryParse(split[1], out var port) || port is <= 0 or > 65535) return false;
    endpoint = new IPEndPoint(ip, port);
    return true;
}

static string? ParseOption(string[] args, string optionName)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}
