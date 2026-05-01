using System;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MGConsole
{
    public class Game1 : Game
    {
        private readonly Settings _settings;
        private int _cols;
        private int _rows;

        private GraphicsDeviceManager _graphics;
        private SpriteBatch _spriteBatch;

        private ConsoleFont _consoleFont;
        private TerminalScreen _screen;
        private ConPty _pty;
        private StreamWriter _ptyWriter;
        private readonly object _screenLock = new();

        // CRT post-process resources.
        private RenderTarget2D _sceneTarget;
        // Progressive bilinear-downsample chain for bloom: [0]=½, [1]=¼, [2]=? of scene size.
        private readonly RenderTarget2D[] _bloomMips = new RenderTarget2D[3];
        private Texture2D _scanlineTex;
        private Texture2D _vignetteTex;
        private int _vignetteSize;

        // True while the first-run welcome screen is displayed (PTY not yet started).
        private bool _showingWelcome;

        private KeyboardState _prevKeyboardState;
        private double _keyRepeatTimer;
        private Keys _lastKey = Keys.None;
        private const double KeyRepeatDelay = 0.45;
        private const double KeyRepeatRate = 0.04;

        // Suppress recursive resize handling while we adjust the back buffer ourselves.
        private bool _suppressResize;

        public Game1()
        {
            _settings = Settings.LoadOrCreate();
            // Apply palette globally before any TerminalScreen is created.
            TerminalScreen.Palette = _settings.GetPaletteColors();

            _cols = _settings.Cols;
            _rows = _settings.Rows;

            _graphics = new GraphicsDeviceManager(this);
            _graphics.PreferredBackBufferWidth  = (int)Math.Round(_cols * ConsoleFont.GlyphWidth  * _settings.FontScale);
            _graphics.PreferredBackBufferHeight = (int)Math.Round(_rows * ConsoleFont.GlyphHeight * _settings.FontScale);
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            Window.AllowUserResizing = _settings.Resizable;
            Window.Title = "MGConsole";
            Window.ClientSizeChanged += OnClientSizeChanged;
        }

        protected override void Initialize()
        {
            _consoleFont = new ConsoleFont(GraphicsDevice, _settings.FontScale);
            _screen = new TerminalScreen(_cols, _rows);

            if (_settings.FirstRunWelcome)
            {
                _showingWelcome = true;
                lock (_screenLock)
                    FeedWelcomeScreen();
            }
            else
            {
                StartPty();
            }

            Exiting += (s, e) => { try { _pty?.Dispose(); } catch { } };
            base.Initialize();

            // Set the window icon to the embedded application icon so the
            // title bar and taskbar match the .exe icon.
            try
            {
                var form = System.Windows.Forms.Control.FromHandle(Window.Handle)
                           as System.Windows.Forms.Form;
                if (form != null)
                {
                    using var ms = new MemoryStream(Properties.Resources.Icon1);
                    form.Icon = new System.Drawing.Icon(ms);
                }
            }
            catch { /* best effort — non-fatal if icon load fails */ }
        }

        protected override void LoadContent()
        {
            _spriteBatch = new SpriteBatch(GraphicsDevice);
        }

        private void StartPty()
        {
            _pty = new ConPty();
            _pty.Start(_settings.AutoExec, (short)_cols, (short)_rows);
            _ptyWriter = new StreamWriter(_pty.Input, new UTF8Encoding(false)) { AutoFlush = true };
            Task.Run(ReadPtyAsync);
        }

        private async Task ReadPtyAsync()
        {
            // Capture a local reference so the watcher task is unaffected by
            // _pty being reassigned during a restart.
            var pty = _pty;

            void Log(string msg)
            {
                System.Diagnostics.Debug.WriteLine("[MGConsole] " + msg);
                try { Console.Error.WriteLine("[MGConsole] " + msg); } catch { }
            }

            Log($"ReadPtyAsync started. RestartOnExit = {_settings.RestartOnExit}");

            // Watcher handles EVERYTHING when the child exits: the optional
            // Environment.Exit, ConPty cleanup, and restart. The read loop is
            // intentionally NOT part of this flow — closing a FileStream from
            // another thread does not reliably cancel a pending synchronous
            // ReadAsync on a Windows pipe handle, so we cannot count on the
            // read loop unblocking. If the old loop gets orphaned, the new
            // PTY's loop runs alongside it harmlessly.
            _ = Task.Run(() =>
            {
                try
                {
                    while (!pty.HasExited) Thread.Sleep(100);

                    Log($"Child process exited. RestartOnExit = {_settings.RestartOnExit}");

                    if (!_settings.RestartOnExit)
                    {
                        Log("Hard-exiting application via Environment.Exit(0).");
                        Environment.Exit(0);
                        return;
                    }

                    Log("Restarting child process...");
                    try { pty.Dispose(); }
                    catch (Exception ex) { Log("pty.Dispose threw: " + ex.Message); }

                    _pty = null;
                    _ptyWriter = null;

                    lock (_screenLock)
                        _screen.ClearAll();

                    StartPty();
                    Log("New child process started.");
                }
                catch (Exception ex)
                {
                    Log("Watcher crashed: " + ex);
                    // If anything goes wrong while restarting, fall back to exiting.
                    Environment.Exit(1);
                }
            });

            // Read loop. Self-terminates when the pipe closes cleanly. If the
            // sync I/O won't cancel and this loop hangs after the child dies,
            // it just sits idle — the watcher already restarted (or exited)
            // the application without depending on it.
            var reader = new StreamReader(pty.Output, Encoding.UTF8);
            char[] buf = new char[4096];
            try
            {
                while (true)
                {
                    int n = await reader.ReadAsync(buf, 0, buf.Length);
                    if (n <= 0) break;
                    lock (_screenLock)
                    {
                        _screen.Feed(new ReadOnlySpan<char>(buf, 0, n));
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Read loop exception (harmless): " + ex.Message);
            }

            Log("Read loop exited.");
        }

        protected override void Update(GameTime gameTime)
        {
            if (_showingWelcome)
            {
                // Any newly pressed key dismisses the welcome screen.
                KeyboardState state = Keyboard.GetState();
                if (state.GetPressedKeys().Length > 0 && _prevKeyboardState.GetPressedKeys().Length == 0)
                {
                    _showingWelcome = false;
                    _settings.FirstRunWelcome = false;
                    _settings.Save();
                    lock (_screenLock)
                        _screen.ClearAll();
                    StartPty();
                }
                _prevKeyboardState = state;
                base.Update(gameTime);
                return;
            }

            HandleKeyboardInput(gameTime);
            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            int nativeW = _cols * _consoleFont.ScaledWidth;
            int nativeH = _rows * _consoleFont.ScaledHeight;
            int bbW = GraphicsDevice.PresentationParameters.BackBufferWidth;
            int bbH = GraphicsDevice.PresentationParameters.BackBufferHeight;

            EnsureCrtResources(nativeW, nativeH);

            // ---- Pass 1: render terminal into the offscreen scene target ----
            GraphicsDevice.SetRenderTarget(_sceneTarget);
            GraphicsDevice.Clear(TerminalScreen.Palette[0]);
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            DrawTerminal(gameTime);
            _spriteBatch.End();

            // ---- Pass 2: build bloom mip chain (all offscreen, before touching back buffer) ----
            // We do ALL render-target switching here so the back buffer is never abandoned
            // mid-frame. Switching away from the back buffer and returning to it can cause
            // DirectX to silently discard its previous contents.
            if (_settings.CrtEffect && _settings.GlowIntensity > 0f)
            {
                RenderTarget2D[] sources = { _sceneTarget, _bloomMips[0], _bloomMips[1] };
                for (int m = 0; m < 3; m++)
                {
                    if (_bloomMips[m] == null) continue;
                    GraphicsDevice.SetRenderTarget(_bloomMips[m]);
                    GraphicsDevice.Clear(Color.Black);
                    _spriteBatch.Begin(samplerState: SamplerState.LinearClamp,
                                       blendState: BlendState.Opaque);
                    _spriteBatch.Draw(sources[m],
                        new Rectangle(0, 0, _bloomMips[m].Width, _bloomMips[m].Height),
                        Color.White);
                    _spriteBatch.End();
                }
            }

            // ---- Pass 3: composite everything to the back buffer in one uninterrupted pass ----
            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(Color.Black);

            // Destination rectangle: stretch fills back buffer; reflow uses native size.
            Rectangle dest = _settings.ResizeMode == ConsoleResizeMode.Stretch
                ? new Rectangle(0, 0, bbW, bbH)
                : new Rectangle(0, 0, nativeW, nativeH);

            // 3a) Base scene — sharp pixel-perfect terminal render.
            //     Optionally sliced into wavy horizontal bands for the CRT roll effect.
            //     LinearClamp is required when waving so sub-pixel offsets actually
            //     interpolate; PointClamp would snap them back to integer pixels.
            bool waveActive = _settings.CrtEffect && _settings.WaveAmplitude > 0f;
            _spriteBatch.Begin(
                samplerState: waveActive ? SamplerState.LinearClamp : SamplerState.PointClamp,
                blendState: BlendState.Opaque);
            if (waveActive)
                DrawWavySlices(_sceneTarget, dest, Color.White, gameTime);
            else
                _spriteBatch.Draw(_sceneTarget, dest, Color.White);
            _spriteBatch.End();

            if (_settings.CrtEffect)
            {
                // 3b) Bloom — additively blend each mip over the base scene.
                //     Tightest mip = bright core halo; widest = soft ambient haze.
                //     When waves are active, the bloom is also sliced and (optionally)
                //     brightness-boosted at the wave crests so the glow surges along
                //     with the displacement, giving a beam-deflection look.
                if (_settings.GlowIntensity > 0f)
                {
                    ReadOnlySpan<float> mipWeights = stackalloc float[] { 0.55f, 0.35f, 0.20f };
                    _spriteBatch.Begin(samplerState: SamplerState.LinearClamp,
                                       blendState: BlendState.Additive);
                    for (int m = 0; m < 3; m++)
                    {
                        if (_bloomMips[m] == null) continue;
                        float alpha = _settings.GlowIntensity * mipWeights[m];
                        Color tint = Color.White * alpha;
                        if (waveActive)
                            DrawWavySlices(_bloomMips[m], dest, tint, gameTime,
                                           crestStrength: _settings.WaveBrightness);
                        else
                            _spriteBatch.Draw(_bloomMips[m], dest, tint);
                    }
                    _spriteBatch.End();
                }

                // 3c) Scanlines — tiled 1×2 dark-row texture.
                if (_settings.ScanlineIntensity > 0f && _scanlineTex != null)
                {
                    _spriteBatch.Begin(samplerState: SamplerState.PointWrap,
                                       blendState: BlendState.AlphaBlend);
                    _spriteBatch.Draw(_scanlineTex,
                        dest,
                        new Rectangle(0, 0, dest.Width, dest.Height),
                        Color.White);
                    _spriteBatch.End();
                }

                // 3d) Vignette — radial edge darkening.
                if (_settings.VignetteIntensity > 0f && _vignetteTex != null)
                {
                    _spriteBatch.Begin(samplerState: SamplerState.LinearClamp,
                                       blendState: BlendState.AlphaBlend);
                    _spriteBatch.Draw(_vignetteTex, dest, Color.White);
                    _spriteBatch.End();
                }
            }

            base.Draw(gameTime);
        }

        // Feeds a styled welcome / settings guide into the terminal screen using
        // ANSI escape codes so it renders through the normal draw pipeline
        // (including the CRT effect). Called once on first run, before the PTY starts.
        private void FeedWelcomeScreen()
        {
            // Helpers — keep the message building concise.
            const string ESC = "\x1B[";
            string Reset()              => $"{ESC}0m";
            string Fg(int c)            => $"{ESC}{30 + c}m";
            string BrightFg(int c)      => $"{ESC}{90 + c}m";
            string Bold()               => $"{ESC}1m";

            // VGA palette indices used in the message.
            const int Black   = 0;
            const int Cyan    = 6;
            const int White   = 7;
            const int BrCyan  = 6; // bright via BrightFg
            const int BrWhite = 7;
            const int Yellow  = 3;
            const int BrYellow = 3;
            const int Green   = 2;

            int w = Math.Min(_screen.Cols, 72); // box width, capped to terminal width
            string hBar   = new string('?', w - 2);
            string midBar = new string('?', w - 2);
            string pad    = new string(' ', w - 2);

            string BoxTop() => $"{BrightFg(Cyan)}?{hBar}?{Reset()}";
            string BoxBot() => $"{BrightFg(Cyan)}?{hBar}?{Reset()}";
            string BoxDiv() => $"{BrightFg(Cyan)}?{hBar}?{Reset()}";
            string BoxMid() => $"{BrightFg(Cyan)}?{pad}?{Reset()}";

            // Centre-pads text to fit inside the box (between the two ? borders).
            string Row(string label, string value = "", int labelColor = Yellow, int valColor = BrWhite)
            {
                string inner = value.Length > 0
                    ? $"{BrightFg(labelColor)}{label,-20}{Reset()}{BrightFg(valColor)}{value}{Reset()}"
                    : $"{Bold()}{BrightFg(labelColor)}{label}{Reset()}";

                // Strip ANSI codes to measure visible length for padding.
                int visible = System.Text.RegularExpressions.Regex.Replace(
                    label + value, @"\x1B\[[0-9;]*m", "").Length;
                int labelLen = value.Length > 0 ? 20 : label.Length;
                int visLen   = (value.Length > 0 ? labelLen : label.Length) + value.Length;
                int space    = w - 2 - visLen;
                string rPad  = space > 0 ? new string(' ', space) : "";
                return $"{BrightFg(Cyan)}?{Reset()} {inner}{rPad}{BrightFg(Cyan)}?{Reset()}";
            }

            string Centre(string text, int color = BrCyan)
            {
                int visLen = System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;]*m", "").Length;
                int total  = w - 2;
                int lPad   = Math.Max(0, (total - visLen) / 2);
                int rPad   = Math.Max(0, total - visLen - lPad);
                return $"{BrightFg(Cyan)}?{Reset()}{new string(' ', lPad)}{BrightFg(color)}{text}{Reset()}{new string(' ', rPad)}{BrightFg(Cyan)}?{Reset()}";
            }

            string settingsPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? ".",
                "settings.json");

            var sb = new StringBuilder();
            sb.AppendLine(BoxTop());
            sb.AppendLine(Centre($"Welcome to {Bold()}MGConsole{Reset()}", BrCyan));
            sb.AppendLine(Centre("A MonoGame-powered CRT Terminal Emulator", White));
            sb.AppendLine(BoxMid());
            sb.AppendLine(BoxDiv());
            sb.AppendLine(BoxMid());
            sb.AppendLine(Row("  CONFIGURATION"));
            sb.AppendLine(Row("  " + midBar[..(w - 4)]));
            sb.AppendLine(Row("  Settings file:", settingsPath, Yellow, BrWhite));
            sb.AppendLine(Row("  Edit in any text editor and restart.", "", White));
            sb.AppendLine(Row("  Your changes are never overwritten.", "", White));
            sb.AppendLine(BoxMid());
            sb.AppendLine(BoxDiv());
            sb.AppendLine(BoxMid());
            sb.AppendLine(Row("  GENERAL SETTINGS"));
            sb.AppendLine(Row("  " + midBar[..(w - 4)]));
            sb.AppendLine(Row("  autoExec",       "Shell or app to launch  (cmd.exe, pwsh.exe…)"));
            sb.AppendLine(Row("  restartOnExit",  "true = relaunch autoExec, false = quit MGConsole"));
            sb.AppendLine(Row("  cols / rows",     "Terminal grid dimensions"));
            sb.AppendLine(Row("  fontScale",       "Glyph size  (1.0 = native, 1.5, 2.0, 2.5…)"));
            sb.AppendLine(Row("  resizeMode",      "Reflow (adjusts grid)  or  Stretch (scales)"));
            sb.AppendLine(BoxMid());
            sb.AppendLine(BoxDiv());
            sb.AppendLine(BoxMid());
            sb.AppendLine(Row("  CRT EFFECT"));
            sb.AppendLine(Row("  " + midBar[..(w - 4)]));
            sb.AppendLine(Row("  crtEffect",         "true / false  — master toggle"));
            sb.AppendLine(Row("  glowIntensity",      "Phosphor bloom  (0.0 = off, ~6.0 strong)"));
            sb.AppendLine(Row("  scanlineIntensity",  "Scanline darkness  (0.0 – 1.0)"));
            sb.AppendLine(Row("  vignetteIntensity",  "Edge darkening  (0.0 – 1.0)"));
            sb.AppendLine(BoxMid());
            sb.AppendLine(BoxDiv());
            sb.AppendLine(BoxMid());
            sb.AppendLine(Row("  CRT WAVES (rolling distortion)"));
            sb.AppendLine(Row("  " + midBar[..(w - 4)]));
            sb.AppendLine(Row("  waveAmplitude",   "Pixel displacement  (0 = off, 1–3 subtle)"));
            sb.AppendLine(Row("  waveFrequency",   "Cycles per pixel    (smaller = longer wave)"));
            sb.AppendLine(Row("  waveSpeed",       "Vertical scroll Hz  (negative = upward)"));
            sb.AppendLine(Row("  waveSharpness",   "1 = sine, >1 spiky, <1 flat / square"));
            sb.AppendLine(Row("  waveBrightness",  "Bloom surge along wave crests  (0–5)"));
            sb.AppendLine(BoxMid());
            sb.AppendLine(BoxDiv());
            sb.AppendLine(BoxMid());
            sb.AppendLine(Row("  PALETTE"));
            sb.AppendLine(Row("  " + midBar[..(w - 4)]));
            sb.AppendLine(Row("  16 named colors:", "black, red, green, yellow…", Yellow, BrWhite));
            sb.AppendLine(Row("  Each entry is an", "#RRGGBB hex color string.", Yellow, BrWhite));
            sb.AppendLine(BoxMid());
            sb.AppendLine(BoxDiv());
            sb.AppendLine(BoxMid());
            sb.AppendLine(Row("  TIPS"));
            sb.AppendLine(Row("  " + midBar[..(w - 4)]));
            sb.AppendLine(Row("  • Delete settings.json to regenerate defaults."));
            sb.AppendLine(Row("  • Set firstRunWelcome:true to see this again."));
            sb.AppendLine(BoxMid());
            sb.AppendLine(BoxDiv());
            sb.AppendLine(Centre($"{BrightFg(Green)}Press any key to continue…{Reset()}", Green));
            sb.AppendLine(BoxBot());

            _screen.Feed(sb.ToString().AsSpan());
        }

        // Renders the terminal cells (backgrounds, glyphs, cursor) using the
        // currently active SpriteBatch.
        // composite) space, since this is drawn into _sceneTarget.
        private void DrawTerminal(GameTime gameTime)
        {
            lock (_screenLock)
            {
                int gw = _consoleFont.ScaledWidth;
                int gh = _consoleFont.ScaledHeight;
                Color defaultBg = TerminalScreen.Palette[0];

                // 1) Backgrounds
                for (int r = 0; r < _screen.Rows; r++)
                {
                    for (int c = 0; c < _screen.Cols; c++)
                    {
                        var cell = _screen.Buffer[r, c];
                        if (cell.Bg != defaultBg)
                        {
                            _spriteBatch.Draw(_consoleFont.Pixel,
                                new Rectangle(c * gw, r * gh, gw, gh),
                                cell.Bg);
                        }
                    }
                }

                // 2) Glyphs
                for (int r = 0; r < _screen.Rows; r++)
                {
                    for (int c = 0; c < _screen.Cols; c++)
                    {
                        var cell = _screen.Buffer[r, c];
                        char ch = cell.Ch;
                        if (ch == ' ') continue;
                        if (!_consoleFont.TryGetGlyph(ch, out Rectangle src)) continue;
                        _spriteBatch.Draw(_consoleFont.FontTexture,
                            new Rectangle(c * gw, r * gh, gw, gh),
                            src, cell.Fg);
                    }
                }

                // 3) Cursor (blinking block)
                if (_screen.CursorVisible &&
                    _screen.CursorRow >= 0 && _screen.CursorRow < _screen.Rows &&
                    _screen.CursorCol >= 0 && _screen.CursorCol < _screen.Cols)
                {
                    bool on = (gameTime.TotalGameTime.TotalMilliseconds % 1000) < 500;
                    if (on)
                    {
                        _spriteBatch.Draw(_consoleFont.Pixel,
                            new Rectangle(_screen.CursorCol * gw, _screen.CursorRow * gh, gw, gh),
                            new Color(220, 220, 220, 180));
                    }
                }
            }
        }

        // Draws `source` into `dest` as a vertical stack of horizontal slices,
        // each offset horizontally by amplitude·shape(2?(y·freq + t·speed)).
        // Produces the classic slow CRT roll/wobble distortion.
        //
        // Sub-pixel rendering: the X offset is kept as a float and passed via
        // the Vector2 overload of SpriteBatch.Draw, so motion is continuous
        // instead of snapping pixel-by-pixel. Caller MUST be using a sampler
        // with linear filtering (LinearClamp/LinearWrap) for the fractional
        // offset to actually interpolate — point sampling would re-quantize it.
        //
        // crestStrength > 0 modulates each slice's tint alpha by the wave's
        // local intensity (|shape|), brightening the slice in proportion to
        // how far it's currently being displaced. Combined with an Additive
        // blend state this produces a glow band rolling with the wave crests.
        private void DrawWavySlices(Texture2D source, Rectangle dest, Color tint,
                                    GameTime gameTime, float crestStrength = 0f)
        {
            float t         = (float)gameTime.TotalGameTime.TotalSeconds;
            float amp       = _settings.WaveAmplitude;
            float freq      = _settings.WaveFrequency;
            float speed     = _settings.WaveSpeed;
            float sharpness = _settings.WaveSharpness;

            // Slice height adapts to the wavelength so we always have enough
            // samples per cycle for a smooth-looking wave on the Y axis.
            // Aim for ~10 slices per wavelength, clamped to a sane pixel range.
            float wavelengthDestPx = freq > 0f ? 1f / freq : dest.Height;
            int sliceHeight = (int)Math.Clamp(wavelengthDestPx / 10f, 1f, 16f);
            int sliceCount  = Math.Max(8, dest.Height / sliceHeight);

            int srcH = source.Height;
            int srcW = source.Width;
            float scaleX = (float)dest.Width  / srcW;

            for (int i = 0; i < sliceCount; i++)
            {
                // Tile the destination height exactly with integer math (no gaps).
                int dstY0 = dest.Y + (int)((long)i       * dest.Height / sliceCount);
                int dstY1 = dest.Y + (int)((long)(i + 1) * dest.Height / sliceCount);
                int dstSliceH = dstY1 - dstY0;
                if (dstSliceH <= 0) continue;

                // Map dest band back to source band.
                int srcY0 = (int)((long)(dstY0 - dest.Y) * srcH / dest.Height);
                int srcY1 = (int)Math.Ceiling((double)(dstY1 - dest.Y) * srcH / dest.Height);
                int srcSliceH = Math.Max(1, srcY1 - srcY0);

                // Shaped wave: sign(sin) · |sin|^sharpness.
                // sharpness = 1 ? pure sine. >1 spikes the crests, <1 flattens.
                float yMid    = (dstY0 + dstY1) * 0.5f;
                float phase   = MathF.PI * 2f * (yMid * freq + t * speed);
                float s       = MathF.Sin(phase);
                float shaped  = MathF.Sign(s) * MathF.Pow(MathF.Abs(s), sharpness);
                float xOffset = amp * shaped;

                // Per-slice tint: optionally boosted at displacement crests.
                Color sliceTint = tint;
                if (crestStrength > 0f)
                {
                    float intensity = MathF.Abs(shaped); // 0 at zero-crossing, 1 at crest
                    sliceTint = tint * (1f + crestStrength * intensity);
                }

                // Vector2 position + Vector2 scale overload preserves the
                // fractional X coordinate; the Rectangle overload would truncate.
                _spriteBatch.Draw(
                    source,
                    position:        new Vector2(dest.X + xOffset, dstY0),
                    sourceRectangle: new Rectangle(0, srcY0, srcW, srcSliceH),
                    color:           sliceTint,
                    rotation:        0f,
                    origin:          Vector2.Zero,
                    scale:           new Vector2(scaleX, dstSliceH / (float)srcSliceH),
                    effects:         SpriteEffects.None,
                    layerDepth:      0f);
            }
        }

        // Lazily (re)creates the offscreen render target and procedural CRT
        // textures (scanlines + vignette) when the terminal pixel size changes.
        private void EnsureCrtResources(int nativeW, int nativeH)
        {
            if (nativeW < 1) nativeW = 1;
            if (nativeH < 1) nativeH = 1;

            if (_sceneTarget == null ||
                _sceneTarget.Width != nativeW ||
                _sceneTarget.Height != nativeH)
            {
                _sceneTarget?.Dispose();
                _sceneTarget = new RenderTarget2D(
                    GraphicsDevice, nativeW, nativeH, false,
                    SurfaceFormat.Color, DepthFormat.None, 0,
                    RenderTargetUsage.PreserveContents);

                // Rebuild bloom mip chain at ½, ¼, ? of scene size.
                // Minimum 1px to avoid zero-size textures.
                for (int m = 0; m < _bloomMips.Length; m++)
                {
                    _bloomMips[m]?.Dispose();
                    int mipW = Math.Max(1, nativeW >> (m + 1));
                    int mipH = Math.Max(1, nativeH >> (m + 1));
                    _bloomMips[m] = new RenderTarget2D(
                        GraphicsDevice, mipW, mipH, false,
                        SurfaceFormat.Color, DepthFormat.None, 0,
                        RenderTargetUsage.DiscardContents);
                }
            }

            // Scanline texture: 1x2 vertically tiled. Row 0 transparent, row 1 dark.
            // Built once; intensity controls the alpha so we rebuild only if needed.
            byte alpha = (byte)Math.Clamp((int)(_settings.ScanlineIntensity * 255f), 0, 255);
            if (_scanlineTex == null)
            {
                _scanlineTex = new Texture2D(GraphicsDevice, 1, 2);
            }
            _scanlineTex.SetData(new[]
            {
                new Color((byte)0, (byte)0, (byte)0, (byte)0),
                new Color((byte)0, (byte)0, (byte)0, alpha),
            });

            // Vignette: radial alpha gradient. Stretched to fill on draw.
            int desiredSize = 256;
            byte vAlpha = (byte)Math.Clamp((int)(_settings.VignetteIntensity * 255f), 0, 255);
            if (_vignetteTex == null || _vignetteSize != desiredSize)
            {
                _vignetteTex?.Dispose();
                _vignetteSize = desiredSize;
                _vignetteTex = new Texture2D(GraphicsDevice, desiredSize, desiredSize);
            }
            BuildVignette(_vignetteTex, vAlpha);
        }

        private static void BuildVignette(Texture2D tex, byte maxAlpha)
        {
            int w = tex.Width;
            int h = tex.Height;
            var data = new Color[w * h];
            float cx = (w - 1) * 0.5f;
            float cy = (h - 1) * 0.5f;
            float maxDist = MathF.Sqrt(cx * cx + cy * cy);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float d = MathF.Sqrt(dx * dx + dy * dy) / maxDist; // 0 center .. 1 corner
                    // Smooth easing: keep center clear, darken sharply toward edge.
                    float t = d * d;
                    if (t > 1f) t = 1f;
                    byte a = (byte)(maxAlpha * t);
                    data[y * w + x] = new Color((byte)0, (byte)0, (byte)0, a);
                }
            }
            tex.SetData(data);
        }

        // ---------------- Resize ----------------

        private void OnClientSizeChanged(object? sender, EventArgs e)
        {
            if (_suppressResize) return;
            if (_screen == null) return;

            if (_settings.ResizeMode == ConsoleResizeMode.Reflow)
            {
                int w = Math.Max(1, Window.ClientBounds.Width);
                int h = Math.Max(1, Window.ClientBounds.Height);

                int newCols = Math.Max(20, w / _consoleFont.ScaledWidth);
                int newRows = Math.Max(5,  h / _consoleFont.ScaledHeight);

                // Always compute snapped size so the window snaps even when
                // cols/rows are unchanged (e.g. drag smaller than one glyph).
                int snappedW = newCols * _consoleFont.ScaledWidth;
                int snappedH = newRows * _consoleFont.ScaledHeight;

                if (newCols != _cols || newRows != _rows)
                {
                    _cols = newCols;
                    _rows = newRows;

                    lock (_screenLock)
                        _screen.Resize(_cols, _rows);

                    try { _pty?.Resize((short)_cols, (short)_rows); } catch { }
                }

                _suppressResize = true;
                try
                {
                    // Resize the back buffer to the snapped dimensions.
                    _graphics.PreferredBackBufferWidth = snappedW;
                    _graphics.PreferredBackBufferHeight = snappedH;
                    _graphics.ApplyChanges();

                    // Force the WinForms client area to match exactly so the OS
                    // window border doesn't sit at a fractional glyph boundary.
                    var form = System.Windows.Forms.Control.FromHandle(Window.Handle)
                               as System.Windows.Forms.Form;
                    if (form != null)
                        form.ClientSize = new System.Drawing.Size(snappedW, snappedH);
                }
                finally { _suppressResize = false; }
            }
            // Stretch mode: nothing to do - Draw() applies a scaling transform.
        }

        // ---------------- Input ----------------

        private void HandleKeyboardInput(GameTime gameTime)
        {
            KeyboardState state = Keyboard.GetState();
            bool shift = state.IsKeyDown(Keys.LeftShift) || state.IsKeyDown(Keys.RightShift);
            bool ctrl = state.IsKeyDown(Keys.LeftControl) || state.IsKeyDown(Keys.RightControl);
            bool alt = state.IsKeyDown(Keys.LeftAlt) || state.IsKeyDown(Keys.RightAlt);

            // Newly pressed keys
            foreach (Keys key in state.GetPressedKeys())
            {
                if (!_prevKeyboardState.IsKeyDown(key))
                {
                    SendKey(key, shift, ctrl, alt);
                    _lastKey = key;
                    _keyRepeatTimer = 0;
                }
            }

            // Auto-repeat for the last held key
            if (_lastKey != Keys.None && state.IsKeyDown(_lastKey))
            {
                _keyRepeatTimer += gameTime.ElapsedGameTime.TotalSeconds;
                double threshold = KeyRepeatDelay;
                while (_keyRepeatTimer >= threshold)
                {
                    SendKey(_lastKey, shift, ctrl, alt);
                    _keyRepeatTimer -= KeyRepeatRate;
                    threshold = KeyRepeatRate;
                }
            }
            else
            {
                _lastKey = Keys.None;
                _keyRepeatTimer = 0;
            }

            _prevKeyboardState = state;
        }

        private void SendKey(Keys key, bool shift, bool ctrl, bool alt)
        {
            // Special navigation / function keys -> VT sequences
            string? seq = key switch
            {
                Keys.Up => "\x1b[A",
                Keys.Down => "\x1b[B",
                Keys.Right => "\x1b[C",
                Keys.Left => "\x1b[D",
                Keys.Home => "\x1b[H",
                Keys.End => "\x1b[F",
                Keys.PageUp => "\x1b[5~",
                Keys.PageDown => "\x1b[6~",
                Keys.Insert => "\x1b[2~",
                Keys.Delete => "\x1b[3~",
                Keys.F1 => "\x1bOP",
                Keys.F2 => "\x1bOQ",
                Keys.F3 => "\x1bOR",
                Keys.F4 => "\x1bOS",
                Keys.F5 => "\x1b[15~",
                Keys.F6 => "\x1b[17~",
                Keys.F7 => "\x1b[18~",
                Keys.F8 => "\x1b[19~",
                Keys.F9 => "\x1b[20~",
                Keys.F10 => "\x1b[21~",
                Keys.F11 => "\x1b[23~",
                Keys.F12 => "\x1b[24~",
                Keys.Enter => "\r",
                Keys.Tab => shift ? "\x1b[Z" : "\t",
                Keys.Escape => "\x1b",
                Keys.Back => "\x7f",
                _ => null,
            };

            if (seq != null)
            {
                Write(seq);
                return;
            }

            // Ctrl+letter -> control byte
            if (ctrl && key >= Keys.A && key <= Keys.Z)
            {
                char c = (char)((key - Keys.A) + 1);
                Write(c.ToString());
                return;
            }

            char? ch = KeyToChar(key, shift);
            if (ch.HasValue)
            {
                if (alt) Write("\x1b" + ch.Value);
                else Write(ch.Value.ToString());
            }
        }

        private void Write(string s)
        {
            try { _ptyWriter.Write(s); }
            catch { }
        }

        private static char? KeyToChar(Keys key, bool shift)
        {
            if (key >= Keys.A && key <= Keys.Z)
            {
                char c = (char)('a' + (key - Keys.A));
                return shift ? char.ToUpper(c) : c;
            }
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                string normal = "0123456789";
                string shifted = ")!@#$%^&*(";
                int idx = key - Keys.D0;
                return shift ? shifted[idx] : normal[idx];
            }
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
                return (char)('0' + (key - Keys.NumPad0));

            return key switch
            {
                Keys.Space => ' ',
                Keys.OemPeriod => shift ? '>' : '.',
                Keys.OemComma => shift ? '<' : ',',
                Keys.OemMinus => shift ? '_' : '-',
                Keys.OemPlus => shift ? '+' : '=',
                Keys.OemQuestion => shift ? '?' : '/',
                Keys.OemSemicolon => shift ? ':' : ';',
                Keys.OemQuotes => shift ? '"' : '\'',
                Keys.OemOpenBrackets => shift ? '{' : '[',
                Keys.OemCloseBrackets => shift ? '}' : ']',
                Keys.OemPipe => shift ? '|' : '\\',
                Keys.OemTilde => shift ? '~' : '`',
                Keys.Decimal => '.',
                Keys.Add => '+',
                Keys.Subtract => '-',
                Keys.Multiply => '*',
                Keys.Divide => '/',
                _ => null,
            };
        }
    }
}
