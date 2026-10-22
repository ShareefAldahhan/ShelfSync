> Historical planning/learning document. Some proposals differ from the completed local MVP. See [current architecture](architecture.md), [API contract](api-contract.md), and [release guide](local-release.md) for implemented behavior and verification.

# Lesson 1: start the API

Goal: build a minimal .NET 10 backend and verify process liveness. This lesson does not implement inventory, database access, security, or deployment.

The starter files have been created, but not compiled or run: Codex's command runner is failing during sandbox setup. Run these steps in your own PowerShell terminal and report the result.

## 1. Open the project

Open inventory-reservation in your editor. Use a terminal in this project directory, rather than the parent resume workspace.

```powershell
Set-Location -LiteralPath 'C:\Users\share\OneDrive\Documents\ChatGPT\Resume Improvement 2\inventory-reservation'
dotnet --version
```

Expected: a 10.x SDK. If another SDK is selected, run dotnet --list-sdks and report the output rather than changing machine settings blindly.

## 2. Inspect the project file

backend/ShelfSync.Api/ShelfSync.Api.csproj selects the ASP.NET Web SDK and net10.0. Nullable checking and warnings-as-errors catch mistakes early. Implicit usings make common framework namespaces available without repetitive imports. No external package dependency is required yet.

## 3. Read Program.cs

CreateBuilder loads configuration, service registration and logging defaults. Build constructs the application. MapGet registers one HTTP GET endpoint. Results.Ok produces HTTP 200 and JSON. Run starts the server. UtcNow provides an unambiguous timestamp; this is not the database decision time used later for reservation expiry.

The endpoint is liveness only. Database readiness will be a separate endpoint after PostgreSQL is connected. Do not claim healthy inventory because this endpoint works.

## 4. Start the backend

```powershell
dotnet run --project .\backend\ShelfSync.Api\ShelfSync.Api.csproj --launch-profile local
```

Keep this terminal running. The local launch profile binds to 127.0.0.1:5080 and does not expose the demo on the network. HTTP is acceptable for this loopback lesson; HTTPS and authenticated public deployment remain later requirements. Initial restore may need internet access even though there are no custom packages.

## 5. Check it from a second terminal

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health/live'
```

Expected: service ShelfSync.Api, status alive, and a UTC timestamp. You can also open the URL in a browser. The root URL returning 404 is expected because no root route is defined.

## 6. Understand and finish

Explain why liveness differs from database readiness. Stop the API with Ctrl+C. Repeat the request and observe that it fails to connect. Restart and verify again. This is a manual learning checkpoint, not an automated production test.

If port 5080 is occupied, report the error; do not kill an unrelated process. If compilation/restoration fails, copy the first relevant error and do not paste secrets. Checkpoint status remains unverified until the commands run successfully.

Next lesson: verify/install PostgreSQL, create separate application/test databases, apply schema and seed, then add a parameterized product query and database readiness endpoint.

Reference: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis
