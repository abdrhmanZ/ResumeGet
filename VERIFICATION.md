# Verification record

Last checked on **2026-11-06**. The [GitHub Actions run](https://github.com/abdrhmanZ/ResumeGet/actions/runs/42012076126) completed successfully on **2026-11-05** for code commit [`98ea6e7`](https://github.com/abdrhmanZ/ResumeGet/commit/98ea6e76c0094602514f2c8952d0c59becae6fce).

## Verified on GitHub Actions

The `windows-latest` job used the repository's [.NET 10 workflow](.github/workflows/ci.yml) and completed:

- Package restore and the Release solution build.
- All **23 test cases** in the xUnit suite.
- The CLI interruption/resume demo, including the final SHA-256 comparison against its known sample.

The demo exercises two separate CLI invocations against a loopback HTTP server: the first response ends after a saved prefix, and the second must request the remaining range with the expected validator. It prints `PASS / Interrupted, resumed, and SHA-256 matched.` only after its checks succeed.

This evidence covers the checked-in scenarios and the local demo. No coverage percentage, performance benchmark, power-loss recovery guarantee, or general remote-file checksum verification is claimed.

## Local preparation checks

On the development machine, .NET SDK **10.0.401** produced a Release build with **0 warnings and 0 errors**. C# whitespace formatting, PowerShell syntax checking, and framework-dependent publishing also succeeded.

Local runtime testing remains blocked while loading `ResumeGet.Engine.dll`:

```text
An Application Control policy has blocked this file. (0x800711C7)
```

The successful runtime evidence above comes from GitHub's Windows runner, not this restricted device. No local security-policy changes or workarounds were applied.

## Reproduce the checks

From the repository root, with a .NET 10 SDK and permission to execute the project:

```powershell
dotnet restore ResumeGet.slnx
dotnet build ResumeGet.slnx --configuration Release --no-restore
dotnet test ResumeGet.slnx --configuration Release --no-build
```

Then use PowerShell 7 for the local demo:

```powershell
pwsh -File scripts/demo.ps1 -CliPath ./src/ResumeGet.Cli/bin/Release/net10.0/resumeget.dll
```

The demo creates a new temporary directory and keeps its files for inspection. It prints that directory's path on success.

The separately packaged CLI is framework-dependent and unsigned. It needs a .NET 10 runtime and remains subject to the device's application-control policy. Publishing or extracting it does not grant execution approval.
