using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace MGConsole
{
    public enum ConsoleResizeMode
    {
        // Window resizing scales the rendered console (no change in cols/rows).
        Stretch,
        // Window resizing changes cols/rows and resizes the underlying pty/screen.
        Reflow,
    }

    public sealed class Settings
    {
        public int Cols { get; set; } = 133;
        public int Rows { get; set; } = 40;
        public bool Resizable { get; set; } = true;

        // Application to launch on startup. Defaults to the Windows command prompt.
        public string AutoExec { get; set; } = "cmd.exe";

        // What to do when the launched process exits.
        //   true  ? relaunch AutoExec (terminal stays open forever)
        //   false ? close MGConsole entirely
        public bool RestartOnExit { get; set; } = true;

        // Font scale multiplier — supports fractional values (e.g. 1.5, 2.25).
        // 1.0 = native 8×16 px glyphs. Affects window size, reflow snap, and rendering.
        public float FontScale { get; set; } = 1.0f;

        // When true the welcome / settings guide is shown on startup.
        // Automatically set to false after the first run.
        public bool FirstRunWelcome { get; set; } = true;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConsoleResizeMode ResizeMode { get; set; } = ConsoleResizeMode.Stretch;

        // Old-school CRT post-process effect (scanlines + glow + vignette).
        public bool CrtEffect { get; set; } = true;

        // 0.0 = no scanlines, 1.0 = fully opaque dark scanlines.
        public float ScanlineIntensity { get; set; } = 0.25f;

        // 0.0 = no glow, higher = stronger bloom.
        public float GlowIntensity { get; set; } = 1.0f;

        // 0.0 = no vignette, 1.0 = heavy darkening at edges.
        public float VignetteIntensity { get; set; } = 0.25f;

        // CRT horizontal wave displacement (slow rolling distortion seen on old monitors).
        // Amplitude in destination pixels — 0 = off, 1–3 looks authentic.
        public float WaveAmplitude { get; set; } = 1.5f;
        // Cycles per destination pixel along Y (smaller = longer wavelength).
        public float WaveFrequency { get; set; } = 0.0005f;
        // Cycles per second the wave scrolls vertically (negative = upward).
        public float WaveSpeed { get; set; } = 0.2f;
        // Wave shape: 1.0 = pure sine, >1 = spikier crests, <1 = flatter / squarer.
        public float WaveSharpness { get; set; } = 1.0f;
        // Extra additive bloom applied along the wave's displacement crests, like a
        // beam-deflection glow band rolling with the wave.
        // 0 = off, 0.5–1.5 looks natural, higher = exaggerated CRT artifact.
        public float WaveBrightness { get; set; } = 0.4f;

        // 16-color palette keyed by VGA color name.
        // Indices 0-7 are normal colors, 8-15 are their bright variants.
        public Dictionary<string, string> Palette { get; set; } = new()
        {
            ["black"]         = "#0C0C0C",
            ["red"]           = "#C50F1F",
            ["green"]         = "#13A10E",
            ["yellow"]        = "#C19C00",
            ["blue"]          = "#0037DA",
            ["magenta"]       = "#881798",
            ["cyan"]          = "#3A96DD",
            ["white"]         = "#CCCCCC",
            ["brightBlack"]   = "#767676",
            ["brightRed"]     = "#E74856",
            ["brightGreen"]   = "#16C60C",
            ["brightYellow"]  = "#F9F1A5",
            ["brightBlue"]    = "#3B78FF",
            ["brightMagenta"] = "#B4009E",
            ["brightCyan"]    = "#61D6D6",
            ["brightWhite"]   = "#F2F2F2",
        };

        // Canonical order used when mapping the named palette to SGR color indices.
        private static readonly string[] PaletteOrder =
        {
            "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow",
            "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
        };

        // Resolve the path relative to the actual executable so the file lives
        // next to the .exe and survives Visual Studio clean/rebuild cycles that
        // wipe AppContext.BaseDirectory (the bin output folder).
        private static readonly string SettingsPath =
            Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
                "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        public static Settings LoadOrCreate()
        {
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                    if (loaded != null)
                    {
                        loaded.Sanitize();
                        return loaded;
                    }
                }
                catch
                {
                    // File is corrupted — fall through and return defaults without
                    // overwriting, so the user can inspect/fix the file themselves.
                }
                return new Settings();
            }

            // File doesn't exist yet — write defaults once so the user has a
            // template to edit. Never write again from this method.
            var defaults = new Settings();
            try { defaults.Save(); } catch { /* best effort */ }
            return defaults;
        }

        public void Save()
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }

        public Color[] GetPaletteColors()
        {
            var fallback = new Settings().Palette;
            var result = new Color[16];
            for (int i = 0; i < 16; i++)
            {
                string name = PaletteOrder[i];
                string? hex = (Palette != null && Palette.TryGetValue(name, out var v)) ? v : null;
                string? fallbackHex = fallback.TryGetValue(name, out var f) ? f : null;
                result[i] = TryParseHex(hex, out var c) ? c
                           : (TryParseHex(fallbackHex, out var fb) ? fb : Color.Black);
            }
            return result;
        }

        private void Sanitize()
        {
            if (Cols < 20) Cols = 20;
            if (Cols > 500) Cols = 500;
            if (Rows < 5) Rows = 5;
            if (Rows > 200) Rows = 200;
            if (FontScale < 0.5f) FontScale = 0.5f;
            if (FontScale > 8.0f) FontScale = 8.0f;
            if (ScanlineIntensity < 0f) ScanlineIntensity = 0f;
            if (ScanlineIntensity > 1f) ScanlineIntensity = 1f;
            if (GlowIntensity < 0f) GlowIntensity = 0f;
            if (GlowIntensity > 20f) GlowIntensity = 20f;
            if (VignetteIntensity < 0f) VignetteIntensity = 0f;
            if (VignetteIntensity > 1f) VignetteIntensity = 1f;
            if (WaveAmplitude < 0f) WaveAmplitude = 0f;
            if (WaveAmplitude > 20f) WaveAmplitude = 20f;
            if (WaveFrequency < 0f) WaveFrequency = 0f;
            if (WaveFrequency > 1f) WaveFrequency = 1f;
            // WaveSpeed may be negative (upward scroll); clamp to a sane range.
            if (WaveSpeed < -10f) WaveSpeed = -10f;
            if (WaveSpeed > 10f) WaveSpeed = 10f;
            if (WaveSharpness < 0.1f) WaveSharpness = 0.1f;
            if (WaveSharpness > 10f) WaveSharpness = 10f;
            if (WaveBrightness < 0f) WaveBrightness = 0f;
            if (WaveBrightness > 5f) WaveBrightness = 5f;
        }

        private static bool TryParseHex(string? s, out Color color)
        {
            color = Color.Black;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                return false;
            color = new Color((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }
    }
}
