# WindowCast for Windows

Stream any window or the whole desktop from a Windows 11 PC to a phone or another computer over your
Tailscale network, with full mouse and keyboard control. Windows twin of the macOS
[WindowCast](https://github.com/claurealex-cyber/WindowCast).

Plan and milestone stress gates: https://claude.ai/artifact/TEq92qa8gPKCSMmg9QXoQg

## Layout

```
src/WindowCast.Server/   Kestrel host, auth, capture, input, sessions
src/WindowCast.Tray/     system-tray launcher, Tailscale serve, QR
src/WindowCast.Web/      browser client (shared with the macOS version)
tests/WindowCast.Tests/  unit + stress tests (real ports 18090-18095)
```

## Build

```
dotnet build
dotnet test
dotnet run --project src/WindowCast.Server
```

The server listens on 127.0.0.1 only, ports 8090 to 8095. Token lives at `%LOCALAPPDATA%\WindowCast\token`.
