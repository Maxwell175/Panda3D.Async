using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Interrogate;
using Panda3D.Async;
using Panda3D.Core;
using Thread = System.Threading.Thread;

namespace Panda3D.AsyncShowcase;

/// <summary>
/// Mission Control — interactive tech demo for Panda3D.Async.  Planets orbit a sun;
/// a HUD reflects what the engine's coroutines are doing.  Each key exercises one
/// async-library feature:
///   F    periodic loopback HTTP fetch (SynchronizationContext bridge)
///   P    one-shot ping with measured roundtrip
///   R    recompute orbits on "workers" chain, apply after SwitchToChain("default")
///   G    spawn a planet via loader.MakeAsyncRequest + await IAsyncFuture
///   S/L  save/load orbit state via File.WriteAllTextAsync / ReadAllTextAsync
///   C    cancel in-flight user op
///   ESC  quit
/// Background: per-planet orbit coroutines, a Thread-producer Channel feeding a
/// chain-side consumer, and a PandaTaskCompletionSource startup signal.
/// All scene-graph mutation runs on the default chain.
/// </summary>
internal sealed class MissionControl {
    // ─── tunables ───────────────────────────────────────────────────
    const float Pi2          = MathF.PI * 2f;
    const float OrbitScale   = 7f;          // multiplier on planet.orbit
    const float SizeScale    = 1.0f;        // multiplier on planet.size
    const float SunScale     = 1.6f;
    const float SkyScale     = 200f;
    const float CamRadius    = 14f;         // camera orbit radius
    const float CamHeight    = 4.5f;
    const float CamSpinSec   = 90f;         // one camera revolution

    static readonly string AppRoot      = Path.GetFullPath(AppContext.BaseDirectory).Replace('\\', '/');
    static readonly string ModelsRoot   = AppRoot + "/assets/models";
    static readonly string TexturesRoot = AppRoot + "/assets/textures";
    static readonly string DataRoot     = AppRoot + "/data";
    static readonly string SaveFile     = AppRoot + "/data/save.json";

    // ─── engine ─────────────────────────────────────────────────────
    IGraphicsEngine engine = null!;
    IGraphicsWindow window = null!;
    ILoader loader = null!;
    IClockObject clock = null!;
    IAsyncTaskManager taskManager = null!;
    DataGraphTraverser dgTraverser = null!;

    // ─── scene ──────────────────────────────────────────────────────
    INodePath render = null!;
    INodePath cameraRoot = null!;
    INodePath aspect2d = null!;
    INodePath dataRoot = null!;
    INodePath skybox = null!;
    INodePath sun = null!;
    INodePath systemRoot = null!;       // parent of all planets
    INodePath progressBarBg = null!;
    INodePath progressBarFill = null!;

    // ─── loaded resources ───────────────────────────────────────────
    INodePath sphereModel = null!;
    INodePath skyModel = null!;
    readonly Dictionary<string, ITexture> textures = new(StringComparer.OrdinalIgnoreCase);

    // ─── HUD ────────────────────────────────────────────────────────
    TextNode statusText = null!;
    TextNode httpText = null!;
    TextNode telemetryText = null!;
    TextNode progressText = null!;

    // ─── input ──────────────────────────────────────────────────────
    IMouseWatcher mouseWatcher = null!;
    int kEsc, kR, kS, kL, kP, kF, kG, kC;
    bool prevR, prevS, prevL, prevP, prevF, prevG, prevC;

    // ─── async/runtime state ────────────────────────────────────────
    bool quit;
    readonly CancellationTokenSource shutdownCts = new();
    CancellationTokenSource opCts = new();
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4) };
    HttpListener? localServer;
    Thread? serverThread;
    string localBaseUrl = "";

    // Sensor stream: one OS thread → channel → coroutine.
    readonly Channel<string> alertChannel =
        Channel.CreateBounded<string>(new BoundedChannelOptions(32) {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    Thread? sensorThread;
    readonly PandaTaskCompletionSource sensorReady = new();

    // ─── model state ────────────────────────────────────────────────
    sealed class Planet {
        public required string Name;
        public required INodePath OrbitRoot;   // rotates around sun
        public required INodePath Body;        // textured sphere child of OrbitRoot
        public required ITexture Tex;
        public float OrbitRadius;
        public float OrbitOmega;               // rad/sec
        public float DayOmega;                 // rad/sec for axial spin
        public float Phase;                    // current orbital angle
        public float Tilt;                     // axial tilt degrees
        public float Size;
    }

    sealed record PlanetSpec(string name, string tex, float orbit, float size,
                              float yearMul, float dayMul, float tilt);
    sealed record PlanetsDoc(float yearSeconds, List<PlanetSpec> planets);

    readonly List<Planet> planets = new();
    readonly List<string> log = new();   // newest first
    string statusLine = "boot";
    string httpLine = "(no fetch yet)";
    int loadProgressDone, loadProgressTotal = 1;
    int recomputeRunning;
    int spawnRunning;
    float yearSeconds = 30f;
    float cameraPhase = 0f;

    // ════════════════════════════════════════════════════════════════
    //  Entry
    // ════════════════════════════════════════════════════════════════

    public static void Main(string[] _) {
        var app = new MissionControl();
        try {
            app.Initialize();
            app.RunLoop();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally { app.Shutdown(); }
    }

    void Shutdown() {
        try { shutdownCts.Cancel(); } catch { }
        try { localServer?.Stop(); } catch { }
        try { sensorThread?.Join(500); } catch { }
        try { serverThread?.Join(500); } catch { }
        try { http.Dispose(); } catch { }
        try { localServer?.Close(); } catch { }
        try { engine?.RemoveAllWindows(); } catch { }
    }

    // ════════════════════════════════════════════════════════════════
    //  Bootstrap (synchronous)
    // ════════════════════════════════════════════════════════════════

    void Initialize() {
        engine = GraphicsEngine.GetGlobalPtr();
        var pipe = GraphicsPipeSelection.GetGlobalPtr().MakeDefaultPipe()
            ?? throw new InvalidOperationException("No graphics pipe");

        var fb = new FrameBufferProperties();
        fb.SetRgbColor(true);
        fb.SetColorBits(24);
        fb.SetDepthBits(24);
        fb.SetBackBuffers(1);

        var wp = WindowProperties.GetDefault();
        wp.SetSize(1280, 720);
        wp.SetTitle("Panda3D.Async — Mission Control");

        var output = engine.MakeOutput(pipe, "window", 0, fb, wp,
            (int)GraphicsPipe_BufferCreationFlags.BF_require_window)
            ?? throw new InvalidOperationException("make_output returned null");
        window = output.CastTo<GraphicsWindow>()
            ?? throw new InvalidOperationException("Output is not a GraphicsWindow");

        output.SetClearColorActive(true);
        output.SetClearColor(new LVecBase4f(0.01f, 0.01f, 0.03f, 1));

        render = new NodePath("render");

        var camNode = new Camera("camera");
        var lens = new PerspectiveLens();
        lens.SetAspectRatio(1280f / 720f);
        lens.SetFov(55f);
        lens.SetNearFar(0.1f, 1000f);
        camNode.SetLens(lens);
        camNode.SetScene(render);
        cameraRoot = render.AttachNewNode(camNode);
        output.MakeDisplayRegion().SetCamera(cameraRoot);

        new FrameRateMeter("fps").SetupWindow(window);
        SetupAspect2d(output);

        loader = Loader.GetGlobalPtr();
        clock = ClockObject.GetGlobalClock();
        taskManager = AsyncTaskManager.GetGlobalPtr();
        dgTraverser = new DataGraphTraverser();

        // Bootstrap before any worker threads start — model parsing is
        // re-entrancy-unsafe against an active multi-threaded chain.
        LoadBootstrapAssets();
        BuildScene();
        InitInput();
        StartLocalHttpServer();
        StartSensorThread();

        var workers = taskManager.FindTaskChain("workers")
                      ?? taskManager.MakeTaskChain("workers");
        workers.SetNumThreads(2);
        workers.StartThreads();
        loader.SetTaskChain("workers");

        clock.Reset();

        PandaTaskScheduler.UnobservedException += static ex =>
            Console.Error.WriteLine("[unobserved] " + ex);
    }

    void SetupAspect2d(IGraphicsOutput output) {
        const float ar = 1280f / 720f;
        aspect2d = new NodePath("aspect2d");
        aspect2d.SetDepthTest(false);
        aspect2d.SetDepthWrite(false);
        aspect2d.SetTransparency(TransparencyAttrib_Mode.M_alpha);
        aspect2d.SetBin("unsorted", 0);
        var cam2d = new Camera("camera2d");
        var lens2d = new OrthographicLens();
        lens2d.SetFilmSize(2f * ar, 2f);
        lens2d.SetNearFar(-1000, 1000);
        cam2d.SetLens(lens2d);
        cam2d.SetScene(aspect2d);
        var cam2dNp = aspect2d.AttachNewNode(cam2d);
        var dr = output.MakeDisplayRegion();
        dr.SetSort(10);
        dr.SetCamera(cam2dNp);
        dr.SetClearColorActive(false);
        dr.SetClearDepthActive(true);
    }

    void LoadBootstrapAssets() {
        // Just enough to render the loading screen.  The rest comes
        // through the parallel-async LoadingPhaseAsync below.
        sphereModel = new NodePath(loader.LoadSync(Filename.FromOsSpecific(ModelsRoot + "/sphere.egg.pz"))
            ?? throw new InvalidOperationException("sphere.egg.pz missing"));
        skyModel = new NodePath(loader.LoadSync(Filename.FromOsSpecific(ModelsRoot + "/skybox.egg.pz"))
            ?? throw new InvalidOperationException("skybox.egg.pz missing"));
        textures["stars"] = LoadTexture("stars");
    }

    void BuildScene() {
        // Skybox — a sphere with the star map on the inside.
        skybox = skyModel.CopyTo(render);
        skybox.SetScale(new LVecBase3f(SkyScale, SkyScale, SkyScale));
        skybox.SetTexture(textures["stars"], 1);
        skybox.SetBin("background", 0);
        skybox.SetDepthWrite(false);
        skybox.SetTwoSided(true);
        skybox.SetLightOff(1);   // fully lit by texture, ignore lights

        // Sun — placeholder texture until LoadingPhase loads sun.jpg.
        sun = sphereModel.CopyTo(render);
        sun.SetScale(new LVecBase3f(SunScale, SunScale, SunScale));
        sun.SetTexture(textures["stars"], 1);
        sun.SetLightOff(1);

        systemRoot = render.AttachNewNode("system");

        // Ambient floor + a point light at the sun for the day/night terminator.
        var amb = new AmbientLight("ambient");
        amb.SetColor(new LVecBase4f(0.18f, 0.18f, 0.20f, 1f));
        render.SetLight(render.AttachNewNode(amb));

        var sunLight = new PointLight("sunlight");
        sunLight.SetColor(new LVecBase4f(1.2f, 1.15f, 0.95f, 1f));
        var sunLightNp = render.AttachNewNode(sunLight);
        sunLightNp.SetPos(new LPoint3f(0, 0, 0));
        render.SetLight(sunLightNp);

        // HUD — static labels first.
        const float ar = 1280f / 720f;
        Label("Panda3D.Async  ::  Mission Control",
            -ar + 0.04f, 1f - 0.05f, 0.055f, TextProperties_Alignment.A_left);
        Label("[F] fetch telemetry   [P] ping", -ar + 0.04f, 1f - 0.13f, 0.040f, TextProperties_Alignment.A_left);
        Label("[R] recompute orbits  [G] add planet", -ar + 0.04f, 1f - 0.18f, 0.040f, TextProperties_Alignment.A_left);
        Label("[S] save  [L] load    [C] cancel", -ar + 0.04f, 1f - 0.23f, 0.040f, TextProperties_Alignment.A_left);
        Label("[ESC] quit", -ar + 0.04f, 1f - 0.28f, 0.040f, TextProperties_Alignment.A_left);

        // Dynamic HUD slots.
        statusText    = MakeDynamicText( 0,           -0.93f, 0.045f, TextProperties_Alignment.A_center);
        httpText      = MakeDynamicText( ar - 0.04f,   1f - 0.05f, 0.045f, TextProperties_Alignment.A_right);
        telemetryText = MakeDynamicText(-ar + 0.04f,  -0.40f, 0.035f, TextProperties_Alignment.A_left);
        progressText  = MakeDynamicText( 0,           -0.05f, 0.06f,  TextProperties_Alignment.A_center);

        // Loading bar — fill is anchored to its left edge and scaled.
        progressBarBg   = MakeQuad(-0.4f, 0.4f, -0.10f, -0.07f, new LVecBase4f(0.10f, 0.10f, 0.15f, 0.85f));
        progressBarFill = MakeQuad( 0f,    1f,  -0.10f, -0.07f, new LVecBase4f(0.40f, 0.85f, 1.00f, 0.95f));
        progressBarFill.SetX(-0.4f);
        progressBarFill.SetScale(new LVecBase3f(0.001f, 1f, 1.6f));
    }

    void InitInput() {
        dataRoot = new NodePath("data");
        var mouseNode = new MouseAndKeyboard(window, 0, "mouse");
        var mouse = dataRoot.AttachNewNode(mouseNode);
        mouseWatcher = new MouseWatcher("watcher");
        mouse.AttachNewNode(mouseWatcher);

        var reg = ButtonRegistry.Ptr();
        kEsc = reg.FindButton("escape");
        kR = reg.FindButton("r");
        kS = reg.FindButton("s");
        kL = reg.FindButton("l");
        kP = reg.FindButton("p");
        kF = reg.FindButton("f");
        kG = reg.FindButton("g");
        kC = reg.FindButton("c");
    }

    // ════════════════════════════════════════════════════════════════
    //  Local HTTP server (loopback).  Serves /telemetry and /ping.
    //  Loopback avoids libssl init, which clashes with Panda3D's own
    //  libssl/libcrypto when .NET pulls in CryptoNative_EnsureLibSslInitialized.
    // ════════════════════════════════════════════════════════════════

    void StartLocalHttpServer() {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        localServer = new HttpListener();
        localBaseUrl = $"http://127.0.0.1:{port}";
        localServer.Prefixes.Add(localBaseUrl + "/");
        localServer.Start();

        var token = shutdownCts.Token;
        serverThread = new Thread(() => {
            int seq = 0;
            while (!token.IsCancellationRequested) {
                HttpListenerContext ctx;
                try { ctx = localServer.GetContext(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }

                try {
                    string body;
                    string path = ctx.Request.Url?.AbsolutePath ?? "/";
                    if (path == "/telemetry") {
                        Interlocked.Increment(ref seq);
                        body = $"{{\"seq\":{seq},\"ts\":\"{DateTime.UtcNow:O}\",\"orbits\":{planets.Count}}}";
                    }
                    else if (path == "/ping") {
                        body = $"{{\"ts\":\"{DateTime.UtcNow:O}\"}}";
                    }
                    else {
                        ctx.Response.StatusCode = 404;
                        body = "{\"error\":\"not found\"}";
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                } catch { /* per-request swallow */ }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }) { IsBackground = true, Name = "mc-http" };
        serverThread.Start();
    }

    // ════════════════════════════════════════════════════════════════
    //  Sensor stream — OS thread feeding a Channel.  Demonstrates the
    //  cross-thread completion path: the producer runs entirely off
    //  any chain, but the consumer's awaits land on "default".
    // ════════════════════════════════════════════════════════════════

    void StartSensorThread() {
        sensorThread = new Thread(() => {
            try { Thread.Sleep(700); } catch { }
            sensorReady.TrySetResult();    // one-shot cross-thread signal

            string[] kinds = { "TELEMETRY", "ALERT", "STATUS", "ANOMALY" };
            var rng = new Random(0xC0FFEE);
            int n = 1;
            var token = shutdownCts.Token;
            while (!token.IsCancellationRequested) {
                try { Thread.Sleep(rng.Next(1400, 2600)); }
                catch { break; }
                if (token.IsCancellationRequested) break;
                var msg = $"[{DateTime.UtcNow:HH:mm:ss}Z] {kinds[rng.Next(kinds.Length)],-10} #{n++:D3}";
                alertChannel.Writer.TryWrite(msg);
            }
            try { alertChannel.Writer.TryComplete(); } catch { }
        }) { IsBackground = true, Name = "mc-sensor" };
        sensorThread.Start();
    }

    // ════════════════════════════════════════════════════════════════
    //  Render / poll loop — the only thread driving the engine.
    // ════════════════════════════════════════════════════════════════

    void RunLoop() {
        PandaTask.Spawn(MainAsync);
        while (!window.IsClosed() && !quit) {
            dgTraverser.Traverse(dataRoot.Node());
            engine.RenderFrame();
            taskManager.Poll();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Top-level orchestration
    // ════════════════════════════════════════════════════════════════

    async PandaTask MainAsync() {
        await LoadingPhaseAsync();
        progressBarBg.Hide();
        progressBarFill.Hide();
        SetText(progressText, "");

        var doc = await ReadPlanetsAsync(opCts.Token);
        yearSeconds = doc.yearSeconds;
        SpawnPlanets(doc.planets);

        // Fire-and-forget background coroutines.
        PandaTask.Spawn(() => SensorConsumerAsync(shutdownCts.Token));
        PandaTask.Spawn(() => HttpHeartbeatAsync(shutdownCts.Token));
        PandaTask.Spawn(() => CameraOrbitAsync(shutdownCts.Token));

        // MC_AUTOQUIT=N exits as if ESC had been pressed after N seconds (smoke testing).
        if (int.TryParse(Environment.GetEnvironmentVariable("MC_AUTOQUIT"), out var sec) && sec > 0) {
            PandaTask.Spawn(async () => {
                await PandaTask.Delay(sec);
                quit = true;
            });
        }

        await InputAndStatusLoopAsync();
        shutdownCts.Cancel();
    }

    // ─── Loading phase ──────────────────────────────────────────────

    /// <summary>
    /// Parallel async asset prep with a per-frame progress bar.  Texture loads run as
    /// <see cref="Task.Run"/> jobs on the thread pool; the SyncContext bridge resumes
    /// continuations on the scene chain so the counter updates race-free.
    /// </summary>
    async PandaTask LoadingPhaseAsync() {
        var names = new[] { "sun", "mercury", "venus", "earth", "mars", "moon", "deimos", "phobos" };
        loadProgressTotal = names.Length;
        loadProgressDone = 0;
        SetText(progressText, "Loading textures...");

        PandaTask.Spawn(ProgressBarRepaintAsync);

        var jobs = new PandaTask[names.Length];
        for (int i = 0; i < names.Length; i++) {
            string n = names[i];
            jobs[i] = LoadTextureAsync(n);
        }
        await PandaTask.WhenAll(jobs);

        // Real sun texture replaces the placeholder.
        sun.SetTexture(textures["sun"], 1);

        SetText(progressText, "Ready.");
        await PandaTask.Delay(0.3);
        SetText(progressText, "");
        loadProgressTotal = 0;
    }

    async PandaTask LoadTextureAsync(string name) {
        // Task.Run parallelizes synchronous I/O across the thread pool;
        // the await resumes on the chain, so the dictionary write is safe.
        var tex = await Task.Run(() => LoadTexture(name));
        textures[name] = tex;
        loadProgressDone++;
    }

    async PandaTask ProgressBarRepaintAsync() {
        while (loadProgressTotal > 0) {
            float pct = (float)loadProgressDone / Math.Max(1, loadProgressTotal);
            progressBarFill.SetScale(new LVecBase3f(MathF.Max(0.001f, pct), 1f, 1.6f));
            SetText(progressText, $"Loading {loadProgressDone}/{loadProgressTotal}");
            await PandaTask.NextFrame();
        }
    }

    // ─── Planet config ──────────────────────────────────────────────

    async PandaTask<PlanetsDoc> ReadPlanetsAsync(CancellationToken ct) {
        var json = await File.ReadAllTextAsync(DataRoot + "/planets.json", ct);
        return JsonSerializer.Deserialize<PlanetsDoc>(json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("planets.json malformed");
    }

    void SpawnPlanets(List<PlanetSpec> specs) {
        float phase0 = 0.13f;
        foreach (var s in specs) {
            var planet = MakePlanet(s, phase0);
            planets.Add(planet);
            PandaTask.Spawn(() => OrbitLoopAsync(planet, shutdownCts.Token));
            phase0 += 0.71f;
        }
        statusLine = $"system online — {planets.Count} planets";
        AppendLog($"system online — {planets.Count} planets");
    }

    Planet MakePlanet(PlanetSpec s, float startPhase) {
        var orbit = systemRoot.AttachNewNode($"orbit-{s.name}");
        var body = sphereModel.CopyTo(orbit);
        body.SetTexture(LookupOrFallback(s.tex, "moon"), 1);
        body.SetScale(new LVecBase3f(s.size * SizeScale, s.size * SizeScale, s.size * SizeScale));
        body.SetPos(new LPoint3f(s.orbit * OrbitScale, 0, 0));
        body.SetP(s.tilt);
        return new Planet {
            Name = s.name,
            OrbitRoot = orbit,
            Body = body,
            Tex = LookupOrFallback(s.tex, "moon"),
            OrbitRadius = s.orbit * OrbitScale,
            OrbitOmega = Pi2 / Math.Max(0.5f, s.yearMul * yearSeconds),
            DayOmega = Pi2 / Math.Max(0.2f, s.dayMul),
            Phase = startPhase * Pi2,
            Tilt = s.tilt,
            Size = s.size * SizeScale,
        };
    }

    ITexture LookupOrFallback(string name, string fallback)
        => textures.TryGetValue(name, out var t) ? t : textures[fallback];

    // ════════════════════════════════════════════════════════════════
    //  Per-planet orbit coroutine (fire-and-forget)
    // ════════════════════════════════════════════════════════════════

    async PandaTask OrbitLoopAsync(Planet p, CancellationToken ct) {
        float spin = 0f;
        while (!ct.IsCancellationRequested && !quit) {
            float dt = MathF.Min((float)clock.GetDt(), 0.05f);
            p.Phase += p.OrbitOmega * dt;
            if (p.Phase > Pi2) p.Phase -= Pi2;
            spin += p.DayOmega * dt;
            if (spin > Pi2) spin -= Pi2;

            float x = MathF.Cos(p.Phase) * p.OrbitRadius;
            float y = MathF.Sin(p.Phase) * p.OrbitRadius;
            p.Body.SetPos(new LPoint3f(x, y, 0));
            p.Body.SetH(spin * 180f / MathF.PI);

            await PandaTask.NextFrame();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Camera — slowly orbiting bird's-eye on the system.
    // ════════════════════════════════════════════════════════════════

    async PandaTask CameraOrbitAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested && !quit) {
            float dt = MathF.Min((float)clock.GetDt(), 0.05f);
            cameraPhase += dt * (Pi2 / CamSpinSec);
            if (cameraPhase > Pi2) cameraPhase -= Pi2;
            float cx = MathF.Cos(cameraPhase) * CamRadius;
            float cy = MathF.Sin(cameraPhase) * CamRadius;
            cameraRoot.SetPos(new LPoint3f(cx, cy, CamHeight));
            cameraRoot.LookAt(new LPoint3f(0, 0, 0));
            await PandaTask.NextFrame();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  HTTP heartbeat — periodic background poll.
    // ════════════════════════════════════════════════════════════════

    async PandaTask HttpHeartbeatAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                await PandaTask.Delay(6.0);
                if (ct.IsCancellationRequested) return;
                var t0 = clock.GetRealTime();
                var body = await http.GetStringAsync(localBaseUrl + "/telemetry", ct);
                var ms = (clock.GetRealTime() - t0) * 1000.0;
                httpLine = $"telemetry  {body.Length}B  {ms:0}ms";
            } catch (OperationCanceledException) { return; }
            catch (Exception ex) { httpLine = $"telemetry  fail: {ex.GetType().Name}"; }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Sensor consumer — drains the cross-thread channel after the
    //  one-shot startup signal lands.
    // ════════════════════════════════════════════════════════════════

    async PandaTask SensorConsumerAsync(CancellationToken ct) {
        AppendLog("waiting for sensor backend...");
        await sensorReady.Task;          // cross-thread one-shot
        AppendLog("sensor backend online.");

        try {
            await foreach (var msg in alertChannel.Reader.ReadAllAsync(ct)) {
                AppendLog(msg);
                await Task.Delay(20, ct); // SyncContext bridge → back on chain
            }
        } catch (OperationCanceledException) { /* expected */ }
    }

    // ════════════════════════════════════════════════════════════════
    //  Input + per-frame HUD repaint
    // ════════════════════════════════════════════════════════════════

    async PandaTask InputAndStatusLoopAsync() {
        while (!quit && !window.IsClosed()) {
            if (Down(kEsc)) { quit = true; break; }
            if (Edge(kF, ref prevF)) PandaTask.Spawn(() => FetchTelemetryAsync(opCts.Token));
            if (Edge(kP, ref prevP)) PandaTask.Spawn(() => PingAsync(opCts.Token));
            if (Edge(kR, ref prevR)) PandaTask.Spawn(() => RecomputeOrbitsAsync(opCts.Token));
            if (Edge(kG, ref prevG)) PandaTask.Spawn(() => SpawnNewPlanetAsync(opCts.Token));
            if (Edge(kS, ref prevS)) PandaTask.Spawn(() => SaveStateAsync(opCts.Token));
            if (Edge(kL, ref prevL)) PandaTask.Spawn(() => LoadStateAsync(opCts.Token));
            if (Edge(kC, ref prevC)) CancelPendingOps();
            UpdateHud();
            await PandaTask.NextFrame();
        }
    }

    void UpdateHud() {
        SetText(statusText, $"{planets.Count} planets   |   {statusLine}");
        SetText(httpText,   httpLine);
        if (log.Count > 0) {
            var sb = new StringBuilder();
            int n = Math.Min(8, log.Count);
            for (int i = 0; i < n; i++) sb.AppendLine(log[i]);
            SetText(telemetryText, sb.ToString());
        }
    }

    void AppendLog(string line) {
        log.Insert(0, line);
        if (log.Count > 16) log.RemoveRange(16, log.Count - 16);
    }

    void CancelPendingOps() {
        var old = opCts;
        opCts = new CancellationTokenSource();
        try { old.Cancel(); } catch { }
        statusLine = "cancelled in-flight ops";
        AppendLog("[user] cancel pressed");
    }

    // ════════════════════════════════════════════════════════════════
    //  Operations
    // ════════════════════════════════════════════════════════════════

    async PandaTask FetchTelemetryAsync(CancellationToken ct) {
        try {
            statusLine = "fetching telemetry...";
            var body = await http.GetStringAsync(localBaseUrl + "/telemetry", ct);
            httpLine = $"telemetry  {body.Length}B";
            AppendLog($"[op] telemetry: {body}");
            statusLine = "telemetry ok";
        } catch (OperationCanceledException) { statusLine = "telemetry cancelled"; }
        catch (Exception ex) {
            statusLine = $"telemetry fail: {ex.GetType().Name}";
            AppendLog($"[op] telemetry fail: {ex.Message}");
        }
    }

    async PandaTask PingAsync(CancellationToken ct) {
        try {
            statusLine = "pinging...";
            var t0 = clock.GetRealTime();
            using var resp = await http.GetAsync(localBaseUrl + "/ping",
                HttpCompletionOption.ResponseHeadersRead, ct);
            var ms = (clock.GetRealTime() - t0) * 1000.0;
            httpLine = $"ping  {ms:0}ms  {(int)resp.StatusCode}";
            statusLine = $"ping {ms:0} ms";
            AppendLog($"[op] ping {ms:0} ms");
        } catch (OperationCanceledException) { statusLine = "ping cancelled"; }
        catch (Exception ex) {
            statusLine = $"ping fail: {ex.GetType().Name}";
            AppendLog($"[op] ping fail: {ex.Message}");
        }
    }

    /// <summary>
    /// CPU-heavy noise pass on the workers chain, then SwitchToChain("default")
    /// before mutating planet state.
    /// </summary>
    async PandaTask RecomputeOrbitsAsync(CancellationToken ct) {
        if (Interlocked.Exchange(ref recomputeRunning, 1) != 0) return;
        try {
            statusLine = "recomputing orbits (workers chain)...";
            AppendLog("[op] recompute begin");

            await PandaTask.SwitchToChain("workers");
            var rng = new Random(Environment.TickCount);
            var newSpec = new (float omega, float dayOmega, float phase)[planets.Count];
            for (int i = 0; i < planets.Count; i++) {
                ct.ThrowIfCancellationRequested();
                long until = Environment.TickCount + 60;
                double sink = 0;
                while (Environment.TickCount < until) sink += Math.Sin(rng.NextDouble() * 6.28);
                if (double.IsNaN(sink)) throw new Exception();
                float year = 0.5f + (float)rng.NextDouble() * 4.0f;
                float day  = 0.6f + (float)rng.NextDouble() * 6.0f;
                float ph   = (float)rng.NextDouble() * Pi2;
                newSpec[i] = (Pi2 / (year * yearSeconds), Pi2 / day, ph);
            }

            await PandaTask.SwitchToChain("default");
            for (int i = 0; i < planets.Count; i++) {
                planets[i].OrbitOmega = newSpec[i].omega;
                planets[i].DayOmega = newSpec[i].dayOmega;
                planets[i].Phase = newSpec[i].phase;
            }
            statusLine = "recompute done";
            AppendLog($"[op] recompute ok ({planets.Count} planets)");
        }
        catch (OperationCanceledException) {
            await PandaTask.SwitchToChain("default");
            statusLine = "recompute cancelled";
            AppendLog("[op] recompute cancelled");
        }
        catch (Exception ex) {
            await PandaTask.SwitchToChain("default");
            statusLine = $"recompute fail: {ex.GetType().Name}";
            AppendLog($"[op] recompute fail: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref recomputeRunning, 0); }
    }

    /// <summary>
    /// Native async load via <see cref="ILoader.MakeAsyncRequest"/> + <c>await IAsyncFuture</c>;
    /// splices the loaded model into the scene once the future fires.
    /// </summary>
    async PandaTask SpawnNewPlanetAsync(CancellationToken ct) {
        if (Interlocked.Exchange(ref spawnRunning, 1) != 0) return;
        try {
            statusLine = "loading model async...";
            var opts = new LoaderOptions((int)LoaderOptions_LoaderFlags.LF_no_cache);
            var req = loader.MakeAsyncRequest(
                Filename.FromOsSpecific(ModelsRoot + "/sphere.egg.pz"), opts);
            loader.LoadAsync(req);
            ct.ThrowIfCancellationRequested();

            await req;     // IAsyncTask : IAsyncFuture
            ct.ThrowIfCancellationRequested();

            var mlr = req.CastTo<ModelLoadRequest>()
                      ?? throw new InvalidOperationException("not a ModelLoadRequest");
            var node = mlr.GetModel()
                       ?? throw new InvalidOperationException("model load returned null");

            var palette = new[] { "moon", "phobos", "deimos", "mercury", "mars", "venus", "earth" };
            string texName = palette[planets.Count % palette.Length];

            var rng = new Random(Environment.TickCount);
            var spec = new PlanetSpec(
                $"P-{planets.Count + 1:D2}",
                texName,
                orbit:    1.7f + (float)rng.NextDouble() * 1.0f,
                size:     0.30f + (float)rng.NextDouble() * 0.30f,
                yearMul:  1.0f + (float)rng.NextDouble() * 3.0f,
                dayMul:   0.5f + (float)rng.NextDouble() * 5.0f,
                tilt:     (float)rng.NextDouble() * 30f);
            var orbit = systemRoot.AttachNewNode($"orbit-{spec.name}");
            var body = new NodePath(node).CopyTo(orbit);
            body.SetTexture(LookupOrFallback(spec.tex, "moon"), 1);
            body.SetScale(new LVecBase3f(spec.size, spec.size, spec.size));
            body.SetPos(new LPoint3f(spec.orbit * OrbitScale, 0, 0));
            body.SetP(spec.tilt);
            var planet = new Planet {
                Name = spec.name,
                OrbitRoot = orbit,
                Body = body,
                Tex = LookupOrFallback(spec.tex, "moon"),
                OrbitRadius = spec.orbit * OrbitScale,
                OrbitOmega = Pi2 / Math.Max(0.5f, spec.yearMul * yearSeconds),
                DayOmega = Pi2 / Math.Max(0.2f, spec.dayMul),
                Phase = (float)rng.NextDouble() * Pi2,
                Tilt = spec.tilt,
                Size = spec.size,
            };
            planets.Add(planet);
            PandaTask.Spawn(() => OrbitLoopAsync(planet, shutdownCts.Token));

            statusLine = $"spawned {planet.Name}";
            AppendLog($"[op] spawned {planet.Name} (async load)");
        }
        catch (OperationCanceledException) { statusLine = "spawn cancelled"; }
        catch (Exception ex) {
            statusLine = $"spawn fail: {ex.GetType().Name}";
            AppendLog($"[op] spawn fail: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref spawnRunning, 0); }
    }

    sealed record SaveDoc(float yearSeconds, List<SavePlanet> planets);
    sealed record SavePlanet(string name, string tex, float orbit, float size,
                              float orbitOmega, float dayOmega, float phase, float tilt);

    async PandaTask SaveStateAsync(CancellationToken ct) {
        try {
            statusLine = "saving...";
            var doc = new SaveDoc(yearSeconds, new List<SavePlanet>(planets.Count));
            foreach (var p in planets) {
                doc.planets.Add(new SavePlanet(
                    p.Name,
                    GetTextureName(p.Tex),
                    p.OrbitRadius / OrbitScale,
                    p.Size / SizeScale,
                    p.OrbitOmega, p.DayOmega, p.Phase, p.Tilt));
            }
            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(SaveFile, json, ct);
            statusLine = $"saved {planets.Count} planets";
            AppendLog($"[op] save ok → {Path.GetFileName(SaveFile)}");
        }
        catch (OperationCanceledException) { statusLine = "save cancelled"; }
        catch (Exception ex) {
            statusLine = $"save fail: {ex.GetType().Name}";
            AppendLog($"[op] save fail: {ex.Message}");
        }
    }

    async PandaTask LoadStateAsync(CancellationToken ct) {
        try {
            if (!File.Exists(SaveFile)) {
                statusLine = "no save.json yet — press S first";
                return;
            }
            statusLine = "loading...";
            var json = await File.ReadAllTextAsync(SaveFile, ct);
            var doc = JsonSerializer.Deserialize<SaveDoc>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (doc is null) { statusLine = "load: bad json"; return; }

            int applied = 0;
            foreach (var sp in doc.planets) {
                var live = planets.Find(p => p.Name == sp.name);
                if (live is null) continue;
                live.OrbitOmega = sp.orbitOmega;
                live.DayOmega = sp.dayOmega;
                live.Phase = sp.phase;
                applied++;
            }
            statusLine = $"loaded {applied}/{doc.planets.Count} planets";
            AppendLog($"[op] load ok ({applied}/{doc.planets.Count})");
        }
        catch (OperationCanceledException) { statusLine = "load cancelled"; }
        catch (Exception ex) {
            statusLine = $"load fail: {ex.GetType().Name}";
            AppendLog($"[op] load fail: {ex.Message}");
        }
    }

    string GetTextureName(ITexture tex) {
        foreach (var kv in textures) if (ReferenceEquals(kv.Value, tex)) return kv.Key;
        return "moon";
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    ITexture LoadTexture(string name) {
        var path = TexturesRoot + "/" + name + ".jpg";
        var tex = TexturePool.LoadTexture(Filename.FromOsSpecific(path))
            ?? throw new InvalidOperationException("load failed: " + path);
        tex.SetWrapU(SamplerState_WrapMode.WM_repeat);
        tex.SetWrapV(SamplerState_WrapMode.WM_repeat);
        return tex;
    }

    void Label(string text, float x, float z, float scale, TextProperties_Alignment align) {
        var tn = new TextNode("label");
        tn.SetText(text);
        tn.ForceUpdate();
        tn.SetAlign(align);
        tn.SetTextColor(new LVecBase4f(1, 1, 1, 1));
        tn.SetShadowColor(new LVecBase4f(0, 0, 0, 0.55f));
        tn.SetShadow(new LVecBase2f(0.04f, 0.04f));
        tn.SetBin("fixed");
        tn.SetDrawOrder(0);
        var baked = tn.Generate();
        var np = aspect2d.AttachNewNode(baked);
        np.SetPos(new LPoint3f(x, 0, z));
        np.SetScale(new LVecBase3f(scale, scale, scale));
    }

    TextNode MakeDynamicText(float x, float z, float scale, TextProperties_Alignment align) {
        var tn = new TextNode($"dyn-{x:0.00}-{z:0.00}");
        tn.SetText("");
        tn.ForceUpdate();
        tn.SetAlign(align);
        tn.SetTextColor(new LVecBase4f(0.95f, 0.97f, 1f, 1f));
        tn.SetShadowColor(new LVecBase4f(0, 0, 0, 0.7f));
        tn.SetShadow(new LVecBase2f(0.04f, 0.04f));
        tn.SetBin("fixed");
        tn.SetDrawOrder(0);
        var np = aspect2d.AttachNewNode(tn);
        np.SetPos(new LPoint3f(x, 0, z));
        np.SetScale(new LVecBase3f(scale, scale, scale));
        return tn;
    }

    INodePath MakeQuad(float left, float right, float bottom, float top, LVecBase4f color) {
        var cm = new CardMaker("quad");
        cm.SetFrame(left, right, bottom, top);
        cm.SetColor(color);
        var node = cm.Generate();
        var np = aspect2d.AttachNewNode(node);
        np.SetTransparency(TransparencyAttrib_Mode.M_alpha);
        np.SetDepthTest(false);
        np.SetBin("fixed", 0);
        return np;
    }

    bool Down(int btn) => mouseWatcher != null && btn != 0 && mouseWatcher.IsButtonDown(btn);
    bool Edge(int btn, ref bool prev) {
        bool now = Down(btn);
        bool fire = now && !prev;
        prev = now;
        return fire;
    }

    static void SetText(TextNode n, string t) { n.SetText(t); n.ForceUpdate(); }
}
