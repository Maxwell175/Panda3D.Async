using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Interrogate;
using Panda3D.Async;
using Panda3D.Core;

namespace Panda3D.AsteroidsAsync;

/// <summary>
/// Asteroids built on Panda3D.Async coroutines: <c>await PandaTask.NextFrame()</c>
/// for per-frame logic, <c>await PandaTask.Delay(seconds)</c> for timed waits
/// (respawn, bullet lifetime), and <c>PandaTask.Spawn</c> for fire-and-forget
/// bullets.  All logic runs on the default (single-threaded) chain.
/// </summary>
internal sealed class AsteroidsGame {
    const float SpritePos = 55f;
    const float ScreenX = 20f;
    const float ScreenY = 15f;
    const float TurnRate = 360f;
    const float Accel = 10f;
    const float MaxVel = 6f;
    const float MaxVelSq = MaxVel * MaxVel;
    const float BulletLife = 2f;
    const float BulletRepeat = 0.2f;
    const float BulletSpeed = 10f;
    const float AstInitVel = 1f;
    const float AstInitScale = 3f;
    const float AstVelScale = 2.2f;
    const float AstSizeScale = 0.6f;
    const float AstMinScale = 1.1f;
    const float RespawnDelay = 2f;
    const float Deg2Rad = MathF.PI / 180f;

    static readonly string AssetsRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "asteroids")).Replace('\\', '/');
    static readonly string ModelsRoot = AssetsRoot + "/models";
    static readonly string TexturesRoot = AssetsRoot + "/textures";

    // ------ engine state ------
    IGraphicsEngine engine = null!;
    IGraphicsWindow window = null!;
    ILoader loader = null!;
    IClockObject clock = null!;
    Randomizer rng = null!;
    IAsyncTaskManager taskManager = null!;
    DataGraphTraverser dgTraverser = null!;

    // ------ scene ------
    INodePath render = null!;
    INodePath cameraRoot = null!;
    INodePath aspect2d = null!;
    INodePath planeModel = null!;
    INodePath ship = null!;
    INodePath dataRoot = null!;
    TextNode statusText = null!;
    INodePath statusLabel = null!;

    // ------ textures ------
    ITexture shipTex = null!;
    ITexture bulletTex = null!;
    readonly List<ITexture> asteroidTextures = new();

    // ------ input ------
    IMouseWatcher mouseWatcher = null!;
    int kLeft, kRight, kThrust, kFire, kEscape;

    // ------ game state ------
    float shipVX, shipVZ;
    bool alive;
    bool quit;
    double gameTime;
    double nextBulletTime;

    sealed class AsteroidState {
        public INodePath Node = null!;
        public ITexture Tex = null!;
        public float VX, VZ;
    }

    readonly List<AsteroidState> asteroids = new();

    sealed class BulletState {
        public INodePath Node = null!;
        public float VX, VZ;
    }

    readonly List<BulletState> bullets = new();

    // ====================================================================
    //  Entry point
    // ====================================================================

    public static void Main(string[] args) {
        var game = new AsteroidsGame();
        try {
            game.Initialize();
            game.RunLoop();
        } catch (Exception ex) {
            Console.Error.WriteLine(ex);
        } finally {
            game.engine?.RemoveAllWindows();
        }
    }

    // ====================================================================
    //  Bootstrap
    // ====================================================================

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
        wp.SetSize(800, 600);
        wp.SetTitle("Panda3D C# Asteroids — async/await edition");

        var output = engine.MakeOutput(pipe, "window", 0, fb, wp,
            (int)GraphicsPipe_BufferCreationFlags.BF_require_window)
            ?? throw new InvalidOperationException("make_output returned null");
        window = output.CastTo<GraphicsWindow>()
            ?? throw new InvalidOperationException("Output is not a GraphicsWindow");

        output.SetClearColorActive(true);
        output.SetClearColor(new LVecBase4f(0, 0, 0, 1));

        render = new NodePath("render");
        var camNode = new Camera("camera");
        var lens = new PerspectiveLens();
        lens.SetAspectRatio(800f / 600f);
        camNode.SetLens(lens);
        camNode.SetScene(render);
        cameraRoot = render.AttachNewNode(camNode);
        output.MakeDisplayRegion().SetCamera(cameraRoot);

        new FrameRateMeter("fps").SetupWindow(window);
        SetupAspect2d(output);

        loader = Loader.GetGlobalPtr();
        clock = ClockObject.GetGlobalClock();
        rng = new Randomizer();
        taskManager = AsyncTaskManager.GetGlobalPtr();
        dgTraverser = new DataGraphTraverser();

        LoadAssets();
        CreateScene();
        InitInput();

        clock.Reset();
    }

    void SetupAspect2d(IGraphicsOutput output) {
        float ar = 800f / 600f;
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

    void LoadAssets() {
        planeModel = LoadModel(ModelsRoot + "/plane.egg");
        shipTex = LoadTexture(TexturesRoot + "/ship.png");
        bulletTex = LoadTexture(TexturesRoot + "/bullet.png");
        asteroidTextures.Add(LoadTexture(TexturesRoot + "/asteroid1.png"));
        asteroidTextures.Add(LoadTexture(TexturesRoot + "/asteroid2.png"));
        asteroidTextures.Add(LoadTexture(TexturesRoot + "/asteroid3.png"));
    }

    void CreateScene() {
        LoadObject(LoadTexture(TexturesRoot + "/stars.jpg"), 146f, 0, 0, 200f, false);
        ship = LoadObject(shipTex, 1f, 0, 0, SpritePos, true);

        float ar = 800f / 600f;
        Label("Panda3D: Asteroids (async/await)", ar - 0.1f, -1f + 0.02f, 0.07f, TextProperties_Alignment.A_right);
        Label("ESC: Quit", -ar + 0.07f, 1f - 0.10f, 0.05f, TextProperties_Alignment.A_left);
        Label("[Left]: Turn Left", -ar + 0.07f, 1f - 0.16f, 0.05f, TextProperties_Alignment.A_left);
        Label("[Right]: Turn Right", -ar + 0.07f, 1f - 0.22f, 0.05f, TextProperties_Alignment.A_left);
        Label("[Up]: Accelerate", -ar + 0.07f, 1f - 0.28f, 0.05f, TextProperties_Alignment.A_left);
        Label("[Space]: Fire", -ar + 0.07f, 1f - 0.34f, 0.05f, TextProperties_Alignment.A_left);
        string mode = RuntimeFeature.IsDynamicCodeCompiled ? "Shared" : "NativeAOT";
        Label($"C# • {mode} • Panda3D.Async", -ar + 0.07f, 1f - 0.42f, 0.04f, TextProperties_Alignment.A_left);

        statusText = new TextNode("status");
        SetText(statusText, "");
        statusText.SetAlign(TextProperties_Alignment.A_center);
        statusText.SetTextColor(new LVecBase4f(1, 1, 1, 1));
        statusText.SetShadowColor(new LVecBase4f(0, 0, 0, 0.5f));
        statusText.SetShadow(new LVecBase2f(0.04f, 0.04f));
        statusText.SetBin("fixed");
        statusText.SetDrawOrder(0);
        statusLabel = aspect2d.AttachNewNode(statusText);
        Pos(statusLabel, 0, 0, -0.9f);
        Scale(statusLabel, 0.07f);
    }

    void InitInput() {
        dataRoot = new NodePath("data");
        var mouseNode = new MouseAndKeyboard(window, 0, "mouse");
        var mouse = dataRoot.AttachNewNode(mouseNode);
        mouseWatcher = new MouseWatcher("watcher");
        mouse.AttachNewNode(mouseWatcher);

        var reg = ButtonRegistry.Ptr();
        kLeft = reg.FindButton("arrow_left");
        kRight = reg.FindButton("arrow_right");
        kThrust = reg.FindButton("arrow_up");
        kFire = reg.FindButton("space");
        kEscape = reg.FindButton("escape");
    }

    // ====================================================================
    //  Main loop — the ONLY place that calls RenderFrame + Poll
    // ====================================================================

    void RunLoop() {
        PandaTask.Spawn(GameLoopAsync);

        while (!window.IsClosed() && !quit) {
            dgTraverser.Traverse(dataRoot.Node());
            engine.RenderFrame();
            taskManager.Poll();
        }
    }

    // ====================================================================
    //  Game coroutine — replaces the imperative Update() call tree
    // ====================================================================

    async PandaTask GameLoopAsync() {
        while (!window.IsClosed() && !quit) {
            alive = true;
            gameTime = 0;
            nextBulletTime = 0;
            shipVX = shipVZ = 0;
            ship.SetR(0);
            Pos(ship, 0, SpritePos, 0);
            ship.Show();
            SpawnAsteroids();

            await PlayRoundAsync();

            ClearBullets();
            ClearAsteroids();
            ship.Hide();
            Status("Ship destroyed");

            await RespawnCountdownAsync();
        }
    }

    /// <summary>Run the live phase until the ship is destroyed.</summary>
    async PandaTask PlayRoundAsync() {
        while (alive && !quit) {
            float dt = MathF.Min((float)clock.GetDt(), 0.05f);
            gameTime += dt;

            if (Down(kEscape)) {
                quit = true;
                return;
            }

            UpdateShip(dt);
            MoveAsteroids(dt);
            MoveBullets(dt);
            CheckBulletHits();
            CheckShipHit();

            if (alive && asteroids.Count == 0)
                SpawnAsteroids();

            if (alive)
                Status($"Asteroids: {asteroids.Count}  Bullets: {bullets.Count}");

            await PandaTask.NextFrame();
        }
    }

    /// <summary>
    /// Count down to respawn.  Uses <c>PandaTask.Delay</c> — no manual
    /// timer variable needed.
    /// </summary>
    async PandaTask RespawnCountdownAsync() {
        const int steps = 10;
        for (int i = steps; i > 0; i--) {
            Status($"Respawning in {i * RespawnDelay / steps:0.0}s");
            await PandaTask.Delay(RespawnDelay / steps);
        }
    }

    // ====================================================================
    //  Ship
    // ====================================================================

    void UpdateShip(float dt) {
        float heading = ship.GetR();
        if (Down(kRight)) heading += dt * TurnRate;
        else if (Down(kLeft)) heading -= dt * TurnRate;
        ship.SetR(heading % 360f);

        heading = ship.GetR();
        if (Down(kThrust)) {
            float rad = heading * Deg2Rad;
            shipVX += MathF.Sin(rad) * Accel * dt;
            shipVZ += MathF.Cos(rad) * Accel * dt;
            float sq = shipVX * shipVX + shipVZ * shipVZ;
            if (sq > MaxVelSq) {
                float s = MaxVel / MathF.Sqrt(sq);
                shipVX *= s;
                shipVZ *= s;
            }
        }
        MoveWrapped(ship, shipVX, shipVZ, dt);

        if (Down(kFire) && gameTime > nextBulletTime) {
            FireBullet();
            nextBulletTime = gameTime + BulletRepeat;
        }
    }

    // ====================================================================
    //  Bullets — each bullet is a fire-and-forget coroutine
    // ====================================================================

    void FireBullet() {
        float dir = ship.GetR() * Deg2Rad;
        float vx = shipVX + MathF.Sin(dir) * BulletSpeed;
        float vz = shipVZ + MathF.Cos(dir) * BulletSpeed;
        var node = LoadObject(bulletTex, 0.2f, ship.GetX(), ship.GetZ(), SpritePos, true);
        var bullet = new BulletState { Node = node, VX = vx, VZ = vz };
        bullets.Add(bullet);

        PandaTask.Spawn(() => BulletLifetimeAsync(bullet));
    }

    /// <summary>
    /// Each bullet self-destructs after <see cref="BulletLife"/> seconds.
    /// No bookkeeping timer in the main loop — the coroutine owns the
    /// lifetime.
    /// </summary>
    async PandaTask BulletLifetimeAsync(BulletState bullet) {
        await PandaTask.Delay(BulletLife);

        if (bullets.Remove(bullet))
            bullet.Node.RemoveNode();
    }

    void MoveBullets(float dt) {
        for (int i = 0; i < bullets.Count; i++)
            MoveWrapped(bullets[i].Node, bullets[i].VX, bullets[i].VZ, dt);
    }

    void ClearBullets() {
        for (int i = 0; i < bullets.Count; i++)
            bullets[i].Node.RemoveNode();
        bullets.Clear();
    }

    // ====================================================================
    //  Asteroids
    // ====================================================================

    void SpawnAsteroids() {
        ClearAsteroids();
        for (int i = 0; i < 10; i++) {
            var tex = asteroidTextures[rng.RandomInt(asteroidTextures.Count)];
            var node = LoadObject(tex, AstInitScale, 0, 0, SpritePos, true);

            int xc = rng.RandomInt(((int)ScreenX + 1) * 2 - 9) - (int)ScreenX;
            if (xc >= -4) xc += 9;
            node.SetX(xc);

            int zc = rng.RandomInt(((int)ScreenY + 1) * 2 - 9) - (int)ScreenY;
            if (zc >= -4) zc += 9;
            node.SetZ(zc);

            float h = (float)rng.RandomReal(MathF.PI * 2f);
            asteroids.Add(new AsteroidState {
                Node = node, Tex = tex,
                VX = MathF.Sin(h) * AstInitVel,
                VZ = MathF.Cos(h) * AstInitVel,
            });
        }
    }

    void MoveAsteroids(float dt) {
        for (int i = 0; i < asteroids.Count; i++)
            MoveWrapped(asteroids[i].Node, asteroids[i].VX, asteroids[i].VZ, dt);
    }

    void ClearAsteroids() {
        for (int i = 0; i < asteroids.Count; i++)
            asteroids[i].Node.RemoveNode();
        asteroids.Clear();
    }

    void AsteroidHit(int index) {
        var ast = asteroids[index];
        float oldScale = ast.Node.GetScale().GetX();

        if (oldScale <= AstMinScale) {
            ast.Node.RemoveNode();
            asteroids.RemoveAt(index);
            return;
        }

        float ns = oldScale * AstSizeScale;
        Scale(ast.Node, ns);

        float speed = MathF.Sqrt(ast.VX * ast.VX + ast.VZ * ast.VZ) * AstVelScale;
        float dx = -ast.VZ, dz = ast.VX;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.0001f) { dx = 1; dz = 0; len = 1; }
        ast.VX = dx / len * speed;
        ast.VZ = dz / len * speed;

        var n2 = LoadObject(ast.Tex, ns, 0, 0, SpritePos, true);
        Pos(n2, ast.Node.GetX(), ast.Node.GetY(), ast.Node.GetZ());
        asteroids.Add(new AsteroidState {
            Node = n2, Tex = ast.Tex,
            VX = -ast.VX, VZ = -ast.VZ,
        });
    }

    // ====================================================================
    //  Collision
    // ====================================================================

    void CheckBulletHits() {
        for (int bi = bullets.Count - 1; bi >= 0; bi--) {
            if (bi >= bullets.Count) continue;
            var b = bullets[bi];
            float bx = b.Node.GetX(), bz = b.Node.GetZ(), bs = b.Node.GetSx();

            for (int ai = asteroids.Count - 1; ai >= 0; ai--) {
                var a = asteroids[ai];
                float ax = a.Node.GetX(), az = a.Node.GetZ(), asize = a.Node.GetSx();
                float dx = bx - ax, dz2 = bz - az, r = (bs + asize) * 0.5f;
                if (dx * dx + dz2 * dz2 < r * r) {
                    b.Node.RemoveNode();
                    bullets.RemoveAt(bi);
                    AsteroidHit(ai);
                    break;
                }
            }
        }
    }

    void CheckShipHit() {
        float ss = ship.GetSx(), sx = ship.GetX(), sz = ship.GetZ();
        for (int i = 0; i < asteroids.Count; i++) {
            var a = asteroids[i];
            float dx = sx - a.Node.GetX(), dz = sz - a.Node.GetZ();
            float r = (ss + a.Node.GetSx()) * 0.5f;
            if (dx * dx + dz * dz < r * r) {
                alive = false;
                return;
            }
        }
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    INodePath LoadObject(ITexture tex, float scale, float x, float z, float depth, bool transparency) {
        var obj = planeModel.CopyTo(cameraRoot);
        Pos(obj, x, depth, z);
        Scale(obj, scale);
        obj.SetBin("unsorted", 0);
        obj.SetDepthTest(false);
        if (transparency) obj.SetTransparency((TransparencyAttrib_Mode)1);
        obj.SetTexture(tex, 1);
        return obj;
    }

    INodePath LoadModel(string path) {
        var node = loader.LoadSync(Filename.FromOsSpecific(path))
            ?? throw new InvalidOperationException($"Could not load model: {path}");
        return new NodePath(node);
    }

    ITexture LoadTexture(string path) {
        var tex = TexturePool.LoadTexture(Filename.FromOsSpecific(path))
            ?? throw new InvalidOperationException($"Could not load texture: {path}");
        tex.SetWrapU(SamplerState_WrapMode.WM_clamp);
        tex.SetWrapV(SamplerState_WrapMode.WM_clamp);
        return tex;
    }

    void MoveWrapped(INodePath obj, float vx, float vz, float dt) {
        float x = obj.GetX() + vx * dt;
        float z = obj.GetZ() + vz * dt;
        float rad = obj.GetSx() * 0.5f;
        if (x - rad > ScreenX) x = -ScreenX;
        else if (x + rad < -ScreenX) x = ScreenX;
        if (z - rad > ScreenY) z = -ScreenY;
        else if (z + rad < -ScreenY) z = ScreenY;
        Pos(obj, x, obj.GetY(), z);
    }

    void Label(string text, float x, float z, float scale, TextProperties_Alignment align) {
        var tn = new TextNode("label");
        SetText(tn, text);
        tn.SetAlign(align);
        tn.SetTextColor(new LVecBase4f(1, 1, 1, 1));
        tn.SetShadowColor(new LVecBase4f(0, 0, 0, 0.5f));
        tn.SetShadow(new LVecBase2f(0.04f, 0.04f));
        tn.SetBin("fixed");
        tn.SetDrawOrder(0);
        var baked = tn.Generate();
        var np = aspect2d.AttachNewNode(baked);
        Pos(np, x, 0, z);
        Scale(np, scale);
    }

    void Status(string text) => SetText(statusText, text);

    bool Down(int btn) => mouseWatcher != null && btn != 0 && mouseWatcher.IsButtonDown(btn);

    static void SetText(TextNode n, string t) { n.SetText(t); n.ForceUpdate(); }
    static void Pos(INodePath n, float x, float y, float z) { using var p = new LPoint3f(x, y, z); n.SetPos(p); }
    static void Scale(INodePath n, float s) { using var v = new LVecBase3f(s, s, s); n.SetScale(v); }
}
