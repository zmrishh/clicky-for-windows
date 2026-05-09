using System;
using System.Threading;
using NAudio.Wave;

var deviceCount = WaveIn.DeviceCount;
Console.WriteLine($"WaveIn.DeviceCount = {deviceCount}");
for (int i = 0; i < deviceCount; i++)
{
    var caps = WaveIn.GetCapabilities(i);
    Console.WriteLine($"  Device {i}: \"{caps.ProductName}\" channels={caps.Channels}");
}

if (deviceCount == 0)
{
    Console.WriteLine("NO DEVICES — mic not visible to WinMM at all.");
    return;
}

Console.WriteLine("\nTesting device 0 with WaveInEvent 44100/16/2 for 4 seconds...");
var wi = new WaveInEvent
{
    DeviceNumber       = 0,
    WaveFormat         = new WaveFormat(44100, 16, 2),
    BufferMilliseconds = 100
};

int chunks = 0;
wi.DataAvailable += (_, e) =>
{
    var n = Interlocked.Increment(ref chunks);
    if (n <= 5) Console.WriteLine($"  DataAvailable chunk #{n}  bytes={e.BytesRecorded}");
};
wi.RecordingStopped += (_, e) =>
{
    Console.WriteLine($"  RecordingStopped exception={e.Exception?.Message ?? "none"}");
};

wi.StartRecording();
Thread.Sleep(4000);
wi.StopRecording();
wi.Dispose();

Console.WriteLine($"\nTotal chunks received: {chunks}");
Console.WriteLine(chunks > 0 ? "✓ MIC OK — WinMM callbacks work" : "✗ MIC DEAD — WinMM DataAvailable never fired");
