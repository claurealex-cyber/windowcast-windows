using System.Diagnostics;
using System.Globalization;
using WindowCast.Server.Capture.Encoding;
using WindowCast.Server.Capture.Native;

namespace WindowCast.Server.Capture;

/// <summary>
/// M1 harness: capture one or more windows (or a monitor) for N seconds, optionally encode, and log
/// per-second metrics (fps, encode time, output bytes, CPU, memory, handles) to the console and a CSV.
///
///   WindowCast.Server spike --window "Notepad" --seconds 30 --encoder h264 --csv out.csv --out stream.h264
///   WindowCast.Server spike --window "Test Pattern" --all            (every matching window at once)
///   WindowCast.Server spike --monitor 0 --encoder jpeg
///   WindowCast.Server spike --list
/// </summary>
public static class CaptureSpike
{
    private sealed class Target
    {
        public required string Label;
        public required WgcCapture Capture;
        public IFrameEncoder? Encoder;
        public readonly object Lock = new();
        public long Captured, Encoded, Bytes, KeyFrames, EncodeCalls, Errors, SizeChanges;
        public double EncodeMsTotal, EncodeMsMax;
        public long LastDropped;
        public bool Closed;
        public double ClosedAt;
    }

    public static int Run(string[] args)
    {
        string? windowTitle = null;
        int? monitorIndex = null;
        var seconds = 30;
        var encoderName = "h264";
        string? csvPath = null;
        string? outPath = null;
        var hardware = true;
        var fps = 30;
        var bitrate = 6000;
        var list = false;
        var cursor = true;
        var all = false;
        var exitOnClose = true;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--window": windowTitle = args[++i]; break;
                case "--monitor": monitorIndex = int.Parse(args[++i]); break;
                case "--seconds": seconds = int.Parse(args[++i]); break;
                case "--encoder": encoderName = args[++i]; break;
                case "--csv": csvPath = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--software": hardware = false; break;
                case "--fps": fps = int.Parse(args[++i]); break;
                case "--bitrate": bitrate = int.Parse(args[++i]); break;
                case "--no-cursor": cursor = false; break;
                case "--list": list = true; break;
                case "--all": all = true; break;
                case "--keep-going": exitOnClose = false; break;
            }
        }

        if (list) return ListTargets();

        Console.WriteLine($"WGC supported: {WgcCapture.IsSupported}");
        using var device = GraphicsDevice.Create();
        Console.WriteLine($"GPU: {device.AdapterName}");

        var targets = new List<Target>();
        if (monitorIndex is int mi)
        {
            var mons = Win32.EnumerateMonitors();
            targets.Add(new Target { Label = $"monitor{mi}", Capture = new WgcCapture(device, mons[mi].Handle, CaptureTargetKind.Monitor) });
            Console.WriteLine($"Target: monitor {mi} {mons[mi].Device} {mons[mi].Bounds}");
        }
        else
        {
            var matches = Win32.EnumerateAltTabWindows()
                .Where(h => Win32.GetWindowText(h).Contains(windowTitle ?? "", StringComparison.OrdinalIgnoreCase))
                .Where(h => !Win32.IsIconic(h))
                .ToList();
            if (matches.Count == 0)
            {
                Console.Error.WriteLine($"No window matching '{windowTitle}'. Use --list.");
                return 2;
            }
            if (!all) matches = matches.Take(1).ToList();
            var n = 0;
            foreach (var hwnd in matches)
            {
                Console.WriteLine($"Target: window 0x{hwnd.ToInt64():X} '{Win32.GetWindowText(hwnd)}' {Win32.GetFrameBounds(hwnd)}");
                targets.Add(new Target { Label = $"w{n++}", Capture = new WgcCapture(device, hwnd, CaptureTargetKind.Window) });
            }
        }

        FileStream? outFile = outPath is null ? null : File.Create(outPath);
        var startTicks = Stopwatch.GetTimestamp();

        foreach (var t in targets)
        {
            var target = t;
            target.Capture.SizeChanged += size =>
            {
                Console.WriteLine($"  [{target.Label} size] {size.Width}x{size.Height} at t={Elapsed(startTicks):F2}s");
                lock (target.Lock)
                {
                    target.SizeChanges++;
                    if (target.Encoder is MfH264Encoder) { target.Encoder.Dispose(); target.Encoder = null; }
                }
            };
            target.Capture.Closed += () =>
            {
                target.Closed = true;
                target.ClosedAt = Elapsed(startTicks);
                Console.WriteLine($"  [{target.Label} closed] target closed at t={target.ClosedAt:F2}s");
            };
            target.Capture.FrameArrived += frame =>
            {
                Interlocked.Increment(ref target.Captured);
                if (encoderName == "none") return;
                lock (target.Lock)
                {
                    if (target.Encoder is null)
                    {
                        target.Encoder = encoderName switch
                        {
                            "jpeg" => new JpegEncoder(15, 50),
                            _ => new MfH264Encoder(frame.Width, frame.Height, fps, bitrate, hardware),
                        };
                        var e = target.Encoder;
                        Console.WriteLine($"  [{target.Label} encoder] {e.Name}{(e is MfH264Encoder m ? $" async={m.IsAsync} {m.Width}x{m.Height} {m.Fps}fps {m.BitrateKbps}kbps" : "")}");
                    }
                    var t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        target.Encoder.Encode(frame, c =>
                        {
                            target.Encoded++;
                            target.Bytes += c.Length;
                            if (c.IsKeyFrame) target.KeyFrames++;
                            if (targets.Count == 1) outFile?.Write(c.Span);
                        });
                    }
                    catch (Exception ex)
                    {
                        target.Errors++;
                        if (target.Errors <= 3) Console.Error.WriteLine($"  [{target.Label} encode error] {ex.Message}");
                        return;
                    }
                    var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    target.EncodeMsTotal += ms;
                    target.EncodeMsMax = Math.Max(target.EncodeMsMax, ms);
                    target.EncodeCalls++;
                }
            };
        }

        foreach (var t in targets)
        {
            t.Capture.Start(captureCursor: cursor, showBorder: false);
            Console.WriteLine($"[{t.Label}] capture started: {t.Capture.CurrentSize.Width}x{t.Capture.CurrentSize.Height}, border disabled: {t.Capture.BorderDisabled}, encoder: {encoderName}");
        }

        StreamWriter? csv = csvPath is null ? null : new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
        csv?.WriteLine("second,target,captured_fps,encoded_fps,dropped,encode_ms_avg,encode_ms_max,kbytes,keyframes,cpu_pct,working_set_mb,private_mb,handles,threads,size_w,size_h,encoder");

        var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastWall = Stopwatch.GetTimestamp();
        var prev = targets.Select(Snapshot).ToArray();

        for (var s = 1; s <= seconds; s++)
        {
            Thread.Sleep(1000);
            if (exitOnClose && targets.All(t => t.Closed)) break;

            proc.Refresh();
            var nowWall = Stopwatch.GetTimestamp();
            var cpuDelta = (proc.TotalProcessorTime - lastCpu).TotalSeconds;
            var wallDelta = (nowWall - lastWall) / (double)Stopwatch.Frequency;
            var cpuPct = 100.0 * cpuDelta / wallDelta / Environment.ProcessorCount;
            lastCpu = proc.TotalProcessorTime;
            lastWall = nowWall;

            var totalCap = 0L; var totalEnc = 0L; var totalKb = 0.0;
            var perTarget = new List<string>();
            for (var i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                var cur = Snapshot(t);
                var d = cur - prev[i];
                prev[i] = cur;
                var dropped = t.Capture.FramesDropped - t.LastDropped;
                t.LastDropped = t.Capture.FramesDropped;
                var encAvg = d.EncodeCalls > 0 ? d.EncodeMsTotal / d.EncodeCalls : 0;
                var size = t.Capture.CurrentSize;
                string encName;
                lock (t.Lock) encName = t.Encoder?.Name ?? encoderName;
                totalCap += d.Captured; totalEnc += d.Encoded; totalKb += d.Bytes / 1024.0;
                perTarget.Add($"{t.Label}={d.Captured}/{d.Encoded}");

                csv?.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5:F2},{6:F2},{7:F0},{8},{9:F1},{10:F0},{11:F0},{12},{13},{14},{15},{16}",
                    s, t.Label, d.Captured, d.Encoded, dropped, encAvg, d.EncodeMsMax, d.Bytes / 1024.0, d.KeyFrames,
                    cpuPct, proc.WorkingSet64 / 1048576.0, proc.PrivateMemorySize64 / 1048576.0, proc.HandleCount, proc.Threads.Count, size.Width, size.Height, encName));

                if (targets.Count == 1)
                    Console.WriteLine($"t={s,3}s cap={d.Captured,3} fps enc={d.Encoded,3} fps drop={dropped,2} enc={encAvg,6:F2}ms max={d.EncodeMsMax,6:F2}ms {d.Bytes / 1024.0,7:F0} KB key={d.KeyFrames} cpu={cpuPct,5:F1}% ws={proc.WorkingSet64 / 1048576.0,5:F0}MB h={proc.HandleCount} {size.Width}x{size.Height}");
                lock (t.Lock) t.EncodeMsMax = 0;
            }
            if (targets.Count > 1)
                Console.WriteLine($"t={s,3}s n={targets.Count} cap={totalCap,4} enc={totalEnc,4} {totalKb,7:F0} KB cpu={cpuPct,5:F1}% ws={proc.WorkingSet64 / 1048576.0,5:F0}MB h={proc.HandleCount}  {string.Join(" ", perTarget)}");
            csv?.Flush();
        }

        foreach (var t in targets)
        {
            t.Capture.Stop();
            lock (t.Lock) { t.Encoder?.Dispose(); t.Encoder = null; }
        }
        outFile?.Dispose();
        csv?.Dispose();

        var elapsed = Elapsed(startTicks);
        Console.WriteLine();
        var errors = 0L;
        foreach (var t in targets)
        {
            var total = Snapshot(t);
            errors += total.Errors;
            Console.WriteLine($"Summary [{t.Label}]: {total.Captured} frames captured in {elapsed:F1}s ({total.Captured / elapsed:F1} fps), {total.Encoded} encoded, {t.Capture.FramesDropped} dropped, {total.Bytes / 1024.0 / 1024.0:F1} MB out, {total.KeyFrames} keyframes, {total.SizeChanges} size changes, {total.Errors} errors{(t.Closed ? $", closed at {t.ClosedAt:F2}s" : "")}");
            t.Capture.Dispose();
        }
        if (outPath is not null) Console.WriteLine($"Wrote {outPath}");
        return errors == 0 ? 0 : 1;
    }

    private static int ListTargets()
    {
        Console.WriteLine("Monitors:");
        var mons = Win32.EnumerateMonitors();
        for (var i = 0; i < mons.Count; i++)
            Console.WriteLine($"  [{i}] {mons[i].Device} {mons[i].Bounds}{(mons[i].Primary ? " primary" : "")}");
        Console.WriteLine("Windows:");
        foreach (var h in Win32.EnumerateAltTabWindows())
        {
            Win32.GetWindowThreadProcessId(h, out var pid);
            Console.WriteLine($"  0x{h.ToInt64():X} pid={pid} {Win32.GetFrameBounds(h)} {(Win32.IsIconic(h) ? "[min] " : "")}{Win32.GetWindowText(h)}");
        }
        return 0;
    }

    private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;

    private static Snap Snapshot(Target t)
    {
        lock (t.Lock)
            return new Snap(Interlocked.Read(ref t.Captured), t.Encoded, t.Bytes, t.KeyFrames, t.EncodeCalls, t.EncodeMsTotal, t.EncodeMsMax, t.Errors, t.SizeChanges);
    }

    private readonly record struct Snap(long Captured, long Encoded, long Bytes, long KeyFrames, long EncodeCalls, double EncodeMsTotal, double EncodeMsMax, long Errors, long SizeChanges)
    {
        public static Snap operator -(Snap a, Snap b) => new(a.Captured - b.Captured, a.Encoded - b.Encoded, a.Bytes - b.Bytes, a.KeyFrames - b.KeyFrames,
            a.EncodeCalls - b.EncodeCalls, a.EncodeMsTotal - b.EncodeMsTotal, a.EncodeMsMax, a.Errors - b.Errors, a.SizeChanges - b.SizeChanges);
    }
}
