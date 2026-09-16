using WindowCast.Server.Hosting;

var host = await ServerHost.StartAsync();

Console.WriteLine($"WindowCast: HTTP server on port {host.Port}");
Console.WriteLine($"WindowCast: Quick connect: {host.LocalUrl}/#token={host.Tokens.Current}");
Console.WriteLine($"WindowCast: Access token: {host.Tokens.Current}");
Console.WriteLine($"WindowCast: Token file: {host.Tokens.FilePath}");

await host.WaitForShutdownAsync();
await host.DisposeAsync();
