# 300 — The viewer's Ctrl+C

## Goal

The viewer is the one host that runs until it is stopped, so Ctrl+C has to stop it — not kill it
mid-flight. What that means concretely: the server closes, the engine is disposed (the six ONNX
sessions with it), and the process leaves with exit code 0 and nothing extra on either stream.

## What was already there

`WebApplication.CreateSlimBuilder()` builds the *empty* host
(`Host.CreateEmptyApplicationBuilder`), which is why a slim app has no console logging and no
`IHostLifetime` of its own — or so the shape of the API suggests. It does have one: the host's
service collection is populated by `HostBuilder.PopulateServiceCollection`, which registers
`IHostLifetime → ConsoleLifetime` for the empty builder too. The new test prints what it resolves
and it is `Microsoft.Extensions.Hosting.Internal.ConsoleLifetime`, with or without the line
below.

So Ctrl+C already stopped the viewer. This step makes it the viewer's own behaviour rather than a
framework default it happens to sit on:

```csharp
builder.Host.UseConsoleLifetime();
```

`ConfigureHostBuilder.ConfigureServices` runs its delegate immediately, so this is an ordinary
`AddSingleton<IHostLifetime, ConsoleLifetime>` on the builder's services.

## What Ctrl+C does

`ConsoleLifetime` registers a console control handler for SIGINT (Ctrl+C), SIGQUIT (Ctrl+Break)
and SIGTERM (console close/logoff) and turns each into
`IHostApplicationLifetime.StopApplication()`:

1. Kestrel stops accepting, and the application's stopping token fires;
2. `app.Run()` returns, and `Serve`'s `using var engine` disposes the engine — the ORT sessions
   are released rather than abandoned to the process's death;
3. `Serve` returns 0, and the viewer's `Run` returns it: **exit 0, nothing printed**.

A page that is being read is *waited for*, bounded by `HostOptions.ShutdownTimeout` (30 seconds
by default): the shutdown is graceful, so Ctrl+C during a long document takes effect when the
page finishes or when the timeout expires, whichever comes first.

The reference does something else on Ctrl+C: `srv.serve_forever()` lets the `KeyboardInterrupt`
out of `main()`, so Python prints a traceback and exits 1. That is a deliberate difference — a
traceback is not a surface worth reproducing, and a local viewer is expected to stop cleanly.

## What could not be verified here

The key press itself. This shell has **no console window** — `GetConsoleWindow()` returns 0 in
both the shell and its children — and console control events are not delivered in it:

- `GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)` reports success and reaches nobody, not even the
  process that generated it (checked with a target that prints on `KeyboardInterrupt`, and with
  the generator deliberately unprotected);
- writing a Ctrl+C key event into the target's own `CONIN$` is queued and never processed;
- closing a viewer's console window kills it at once with `0xC000013A`
  (`STATUS_CONTROL_C_EXIT`) rather than through the handler.

So the interactive check is the user's: run `indra-ocr-viewer`, press Ctrl+C, and expect exit 0
with no output. What *is* verified here is the half that can be:

- the host resolves a `ConsoleLifetime` (so the signal has something to call);
- stopping the application ends the run, shuts the server down and gives the port back
  (`ViewerHostTests`);
- nothing else changed: the four harnesses are still green.

## Verification

- `dotnet build src/Indra.Ocr.slnx` — 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` — **207 passed** (206 before; `ViewerHostTests` is the new
  one). It starts the viewer's host on a free port through the same `BuildHost` that `Serve`
  uses, asks `/info`, then stops the application and checks the port is free.
- `cli_parity.py` 83/83, `parity_sweep.py` 24/24, `viewer_cli_parity.py` 20/20,
  `viewer_parity.py` 12 cases + `stderr`.

## Notes

- `Serve` now builds the host in `BuildHost(ViewerArgs, Engine)` and runs it, so a test can look
  at the host without a console; the banner and `app.Run()` stay in `Serve`, in the same order.
- The viewer's `terminate()` path in the harnesses is unchanged on Windows (that is a hard kill,
  no signal); on Linux it is SIGTERM, which the console lifetime now turns into the same graceful
  stop — the harness only requires the process to exit.
- The CLI is deliberately left as it is: it is not a long-running server, and the reference dies
  on Ctrl+C there too.

## Files

- `src/Indra.Ocr.Viewer/Viewer.cs` — `UseConsoleLifetime`, and `BuildHost` as the seam the test
  uses.
- `src/Indra.Ocr.Tests/ViewerHostTests.cs` — the lifetime and the shutdown.
