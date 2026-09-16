using WindowCast.Server.Capture;
using WindowCast.Server.Hosting;

if (args.Length > 0 && args[0] == "spike")
    return CaptureSpike.Run(args);

var host = await ServerHost.StartAsync();

Console.WriteLine($"WindowCast: HTTP server on port {host.Port}");
Console.WriteLine($"WindowCast: Quick connect: {host.LocalUrl}/#token={host.Tokens.Current}");
Console.WriteLine($"WindowCast: Access token: {host.Tokens.Current}");
Console.WriteLine($"WindowCast: Token file: {host.Tokens.FilePath}");

await host.WaitForShutdownAsync();
await host.DisposeAsync();
return 0;
