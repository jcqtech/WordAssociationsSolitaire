using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;

namespace WordAssociationsSolitaire
{
    /// Simple sound-effect manager: loads the game's WAV cues (converted from the source MP3s)
    /// and plays them by name. Everything is best-effort — a missing file or an unavailable audio
    /// device never breaks the game (the whole thing runs headless in --selftest).
    public partial class Game1
    {
        private readonly Dictionary<string, SoundEffect> _sounds = new();

        // Global sound-effect volume as a fraction of full loudness (0..1), applied through
        // SoundEffect.MasterVolume. Defaults to 0.3 (30% of full volume).
        private float _soundVolume = 0.3f;

        /// Push the current volume into the engine. Safe to call any time (no audio device -> no-op).
        private void ApplyVolume()
        {
            try { SoundEffect.MasterVolume = Math.Clamp(_soundVolume, 0f, 1f); }
            catch { /* audio unavailable (e.g. headless self-test) -> ignore */ }
        }

        // Logical cue names -> file (in the Sounds/ output folder).
        private static readonly string[] SoundNames =
        {
            "open-menu", "close-menu", "take-card", "place-card", "deny", "shuffling", "success", "highlight", "define",
            "firework-1", "firework-2", "firework-rise", // win-celebration fireworks
        };

        private void LoadSounds()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Sounds");
            foreach (var name in SoundNames)
            {
                try
                {
                    var path = Path.Combine(dir, name + ".wav");
                    if (!File.Exists(path)) continue;
                    using var fs = File.OpenRead(path);
                    _sounds[name] = SoundEffect.FromStream(fs);
                }
                catch { /* unsupported format / no audio device -> skip this cue */ }
            }
        }

        /// Play a sound cue by name (fire-and-forget). No-op if the cue failed to load or audio
        /// is unavailable, and safe to call every frame's worth of game events.
        private void PlaySound(string name)
        {
            if (_sounds.TryGetValue(name, out var fx))
            {
                try { fx.Play(); } catch { /* device lost / too many voices -> ignore */ }
            }
        }

        /// Play a firework cue panned to where the burst happens across the window (left..right)
        /// with a subtle random pitch, so the celebration feels spatial and varied. Still scaled
        /// by the global sound volume (MasterVolume).
        private void PlayFireworkSound(string name, float x, float volume = 0.9f)
        {
            if (!_sounds.TryGetValue(name, out var fx)) return;
            float pan = MathHelper.Clamp((x / Math.Max(1f, Layout.ScreenW)) * 2f - 1f, -1f, 1f);
            float pitch = (float)(_rng.NextDouble() * 0.24 - 0.12); // gentle ±0.12 variation
            try { fx.Play(MathHelper.Clamp(volume, 0f, 1f), pitch, pan); }
            catch { /* device lost / voice limit reached -> ignore */ }
        }
    }
}
