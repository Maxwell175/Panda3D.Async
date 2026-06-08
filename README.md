# Panda3D.Async

Zero-allocation async/await for the Panda3D C# bindings.

Heavily inspired by [UniTask](https://github.com/Cysharp/UniTask).

Provides:
- `PandaTask` / `PandaTask<T>` — a struct task type analogous to
  UniTask, with a pooled state-machine runner (no allocation for
  sync-completed awaits or per-frame yields).
- `PandaTaskCompletionSource` — safe to complete from any thread;
  backed by a native `AsyncFuture` so cross-thread signalling is
  handled by the existing atomic state machine.
- Chain-aware continuations — after `await`, control resumes on the
  same `AsyncTaskChain` it started on. `ConfigureAwait(false)` opts
  out.
- `SynchronizationContext` bridge — `await httpClient.GetAsync(…)`
  inside a coroutine automatically resumes on the originating chain,
  so scene-graph mutation after a network call is thread-safe.
- Interop with `System.Threading.Tasks.Task` via `AsTask()` /
  `PandaTask.FromTask()`.

## Depends on

- `Panda3D.Interop` 1.11.*

Nothing else. All native support (the `ManagedAsyncTask` class and
the `AsyncFuture` C# extension methods) ships inside the existing
`Panda3D.Runtime.{rid}` packages — `Panda3D.Async` is pure managed
code.

## Samples

- **`samples/asteroids-async`** — Asteroids ported to coroutines.
  Side-by-side with the imperative `raw-asteroids` sample, this is the
  minimal "look, the same game, but with `await NextFrame()` and
  `await Delay()`" comparison.
- **`samples/mission-control`** — interactive solar-system tech demo.
  Textured planets orbit a sun on per-planet coroutines while a HUD
  reflects what the engine is doing.  Keys exercise the async
  features the asteroids sample doesn't:

  | Key | Demonstrates |
  |-----|--------------|
  | `F` | Periodic + on-demand HTTP fetch — `await httpClient.GetStringAsync(...)` resumes on the scene chain via the `SynchronizationContext` bridge. |
  | `P` | One-shot HTTP ping with measured roundtrip. |
  | `R` | CPU offload — `await PandaTask.SwitchToChain("workers")` runs heavy compute on worker threads, hops back to "default" before mutating planet state. |
  | `G` | Native async asset load — `loader.MakeAsyncRequest` + `await IAsyncFuture` to splice a freshly-loaded model into the running scene. |
  | `S` / `L` | Save/load orbit state via `File.WriteAllTextAsync` / `ReadAllTextAsync` — `Task` awaited inside a `PandaTask` coroutine. |
  | `C` | Cooperative cancellation — replaces the in-flight `CancellationTokenSource`. |
  | `ESC` | Quit. |

  Always-on background coroutines: per-planet orbit + axial spin,
  slowly-orbiting camera, periodic HTTP heartbeat, and a real
  `Thread`-backed sensor producer feeding a `Channel` — its startup
  is gated on a cross-thread `PandaTaskCompletionSource` the consumer
  awaits.

  Set `MC_AUTOQUIT=N` to auto-quit after N seconds (handy for smoke
  tests).
