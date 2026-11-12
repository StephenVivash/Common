# Common

## .NET 11 development setup

`global.json` selects the .NET 11.0.1xx SDK. Use MAUI workloads installed for
that SDK. On Windows, use the .NET 11 MAUI workloads installed by Visual
Studio. On macOS, restore workload set 11.0.100 from the repository directory:

```sh
dotnet --version
dotnet workload restore BtHack.slnx --version 11.0.100
```

On macOS, the Apple SDKs in this workload set require Xcode 27.2. The Mac
Catalyst apps target macOS 14.0 or later. Android builds also require the
Android SDK and JDK selected in VS Code's MAUI configuration.
After changing SDK installations or PATH, restart VS Code so its extensions
use the new environment. If using a per-user SDK installation, put
`$HOME/.dotnet` ahead of the system SDK on PATH and set `DOTNET_ROOT` to that
directory in the login-shell profile.

To check the Mac Catalyst app once Xcode is ready:

```sh
dotnet build WattCycleApp/WattCycleApp.csproj -c Release -f net11.0-maccatalyst \
  -p:TargetFrameworks=net11.0-maccatalyst -r maccatalyst-arm64
```
