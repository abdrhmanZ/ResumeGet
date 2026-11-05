# Verification status

Prepared on **2026-11-05**. This is a source-and-build delivery awaiting permission to execute on the development machine.

## Confirmed checks

- Windows build environment: .NET SDK **10.0.401**, with .NET runtime **10.0.12** installed.
- The Release solution build succeeds with **0 warnings and 0 errors**. This compiles the engine, CLI, and test project; it does not prove their runtime behavior.
- The test source contains **23 cases** (10 facts and 13 theory inputs). None is reported as passed in this delivery.
- C# whitespace formatting passes `dotnet format whitespace --folder --verify-no-changes`, excluding generated `bin` and `obj` directories.
- Framework-dependent Release publishing succeeds. The CLI package contains the application and engine DLLs, runtime configuration, dependency manifest, and debug symbols; no runtime is bundled.
- Source review covered range validation, file ownership, cancellation, and error handling. Regression cases cover preservation of a pre-existing lock file and case-insensitive HTTP header values.
- The PowerShell demo passes a static syntax check. The demo itself has not been executed.

## Pending checks

An earlier test attempt was blocked while loading `ResumeGet.Engine.dll`:

```text
An Application Control policy has blocked this file. (0x800711C7)
```

Consequently, **the functional test suite has not passed** and **the end-to-end demo has not been verified**. The user chose to prepare the project here pending authorized execution. No security-policy changes or workarounds were applied.

The workflow in `.github/workflows/ci.yml` is included for a future repository push. It has not run on GitHub. There is no successful CI result or coverage measurement to report.

## Checks to run after execution is authorized

From the source project root, with a .NET 10 SDK:

```powershell
dotnet restore ResumeGet.slnx
dotnet build ResumeGet.slnx --configuration Release --no-restore
dotnet test ResumeGet.slnx --configuration Release --no-build
```

Then use PowerShell 7 for the local demo:

```powershell
pwsh -File scripts/demo.ps1
```

The demo creates a local HTTP server and a new temporary directory. It expects the first transfer to fail after a saved prefix, then runs the same CLI command again and compares the completed file to the original with SHA-256. A successful demo prints `PASS / Interrupted, resumed, and SHA-256 matched.` Keep the test output and demo result as the runtime evidence before presenting the project as tested.

The separately packaged CLI is framework-dependent and unsigned. It needs a .NET 10 runtime and remains subject to the device's application-control policy. Publishing or extracting it does not grant execution approval.
