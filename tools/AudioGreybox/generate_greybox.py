#!/usr/bin/env python3
"""
LexiForge audio GREYBOX generator (temporary scratch SFX + music stems).

WHY THIS EXISTS
    Designers need sound in the build on day one, long before the recorded
    assets are delivered. These procedurally synthesised WAVs are intentionally
    plain: they exist to validate timing, routing, voice budgets, ducking and
    adaptive music behaviour. They are NOT shippable sound design.

SINGLE SOURCE OF TRUTH
    This script reads godot/data/audio_events.json. It never keeps a second list
    of files. Add an event + streams to the catalog, re-run this script, and a
    matching scratch WAV appears. CoreTests then asserts every catalogued path
    exists on disk, so a typo breaks CI instead of going silently mute.

OUTPUTS
    godot/audio/sfx/**/*.wav      short one-shots, 44.1 kHz / mono / 16-bit
    godot/audio/music/*.wav       4 stems, 22.05 kHz / mono / 16-bit, seamless
    godot/audio/amb/*.wav         room bed, 22.05 kHz / mono, seamless
    design/audio_asset_manifest.csv   production tracking sheet

RUN
    python godot/tools/AudioGreybox/generate_greybox.py

FINAL ASSET REPLACEMENT
    Replace every .wav with a recorded/normalised asset at the SAME path (or
    switch the catalog to .ogg - see design/AUDIO_DESIGN.md section 9). Keep
    the event ids stable; the runtime only knows event ids.
"""

from __future__ import annotations

import csv
import json
import math
import random
import struct
import wave
from pathlib import Path

HERE = Path(__file__).resolve()
REPO = HERE.parents[3]
GODOT = HERE.parents[2]
CATALOG_PATH = GODOT / "data" / "audio_events.json"
MANIFEST_PATH = REPO / "design" / "audio_asset_manifest.csv"

SFX_RATE = 44100
LOOP_RATE = 22050
TAU = 2.0 * math.pi

# D major pentatonic, Hz. The game's sonic key (see AUDIO_DESIGN.md section 2).
D2, G3, A3, B3, D3, E4, FS4, A4, B4, D5, FS5, A5, D4 = (
    73.42, 196.00, 220.00, 246.94, 146.83, 329.63, 369.99, 440.00, 493.88,
    587.33, 739.99, 880.00, 293.66
)
PENTATONIC = [D4, E4, FS4, A4, B4, D5, FS5, A5]


# ----------------------------------------------------------------- primitives
def sine(freq: float, dur: float, sr: int, decay: float, amp: float = 1.0,
         attack: float = 0.003, phase: float = 0.0, detune: float = 0.0) -> list[float]:
    n = int(dur * sr)
    out = [0.0] * n
    for i in range(n):
        t = i / sr
        env = min(1.0, t / attack) * math.exp(-t / decay)
        out[i] = amp * env * math.sin(TAU * (freq + detune) * t + phase)
    return out


def chirp(f0: float, f1: float, dur: float, sr: int, decay: float,
          amp: float = 1.0) -> list[float]:
    n = int(dur * sr)
    out = [0.0] * n
    phase = 0.0
    for i in range(n):
        t = i / sr
        f = f0 + (f1 - f0) * (t / dur)
        phase += TAU * f / sr
        out[i] = amp * min(1.0, t / 0.003) * math.exp(-t / decay) * math.sin(phase)
    return out


def noise_burst(dur: float, sr: int, rng: random.Random, decay: float,
                amp: float = 1.0, cutoff: float | None = None,
                attack: float = 0.001) -> list[float]:
    n = int(dur * sr)
    out = [0.0] * n
    y = 0.0
    alpha = 1.0
    if cutoff:
        rc = 1.0 / (TAU * cutoff)
        alpha = (1.0 / sr) / (rc + 1.0 / sr)
    for i in range(n):
        t = i / sr
        white = rng.uniform(-1.0, 1.0)
        y += alpha * (white - y)
        sample = y if cutoff else white
        out[i] = amp * min(1.0, t / attack) * math.exp(-t / decay) * sample
    return out


def mix(*buffers: list[float]) -> list[float]:
    n = max((len(b) for b in buffers), default=0)
    out = [0.0] * n
    for b in buffers:
        for i, v in enumerate(b):
            out[i] += v
    return out


def add_at(dst: list[float], src: list[float], at_seconds: float, sr: int) -> None:
    start = int(at_seconds * sr)
    for i, v in enumerate(src):
        j = start + i
        if 0 <= j < len(dst):
            dst[j] += v


def pad_to(buf: list[float], n: int) -> list[float]:
    return buf + [0.0] * max(0, n - len(buf))


def tone(freq: float, dur: float, sr: int, decay: float, amp: float,
         harmonics: tuple[float, ...] = (1.0, 0.35, 0.12)) -> list[float]:
    parts = []
    for h, a in enumerate(harmonics, start=1):
        parts.append(sine(freq * h, dur, sr, decay / (1.0 + 0.45 * (h - 1)),
                          amp * a, detune=0.15 * (h - 1)))
    return mix(*parts)


def bell(freq: float, dur: float, sr: int, amp: float = 1.0,
         metallic: bool = False) -> list[float]:
    ratios = (1.0, 2.0, 2.76, 5.4) if metallic else (1.0, 2.01, 3.01, 4.2)
    amps = (1.0, 0.5, 0.28, 0.14)
    parts = [sine(freq * r, dur, sr, dur / (2.6 + i), amp * a)
             for i, (r, a) in enumerate(zip(ratios, amps))]
    return mix(*parts)


def fade_edges(buf: list[float], sr: int, in_ms: float = 2.0,
               out_ms: float = 8.0) -> None:
    a = max(1, int(in_ms * sr / 1000))
    b = max(1, int(out_ms * sr / 1000))
    for i in range(min(a, len(buf))):
        buf[i] *= i / a
    for i in range(min(b, len(buf))):
        buf[len(buf) - 1 - i] *= i / b


def seamless_loop(buf: list[float], n: int, sr: int, fade_ms: float = 60.0) -> list[float]:
    """Generate n+f samples, crossfade the extra tail into the head."""
    f = int(fade_ms * sr / 1000)
    buf = pad_to(buf, n + f)
    out = [0.0] * n
    for i in range(f):
        w = i / f
        out[i] = buf[i] * w + buf[n + i] * (1.0 - w)
    out[f:n] = buf[f:n]
    return out


def normalise(buf: list[float], peak_dbfs: float) -> list[float]:
    peak = max((abs(v) for v in buf), default=0.0)
    if peak < 1e-9:
        return buf
    gain = (10.0 ** (peak_dbfs / 20.0)) / peak
    return [v * gain for v in buf]


def write_wav(path: Path, buf: list[float], sr: int) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    pcm = bytearray()
    for v in buf:
        s = max(-1.0, min(1.0, v))
        pcm.extend(struct.pack("<h", int(s * 32767)))
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(bytes(pcm))
    return path.stat().st_size


# ------------------------------------------------------------------- recipes
def recipe(kind: str, dur: float, sr: int, rng: random.Random,
           var: int, peak_dbfs: float) -> list[float]:
    """Return a finished, normalised, click-free buffer."""
    n = int(dur * sr)
    out = [0.0] * n
    jitter = 1.0 + rng.uniform(-0.02, 0.02)

    if kind == "tok":
        body = 620.0 * jitter
        add_at(out, sine(body, dur, sr, dur / 4.5, 0.8), 0.0, sr)
        add_at(out, sine(body * 1.98, dur, sr, dur / 8.0, 0.22), 0.0, sr)
        add_at(out, noise_burst(min(0.02, dur), sr, rng, 0.004, 0.32, cutoff=3200), 0.0, sr)

    elif kind == "tok_low":
        body = 210.0 * jitter
        add_at(out, sine(body, dur, sr, dur / 3.6, 0.85), 0.0, sr)
        add_at(out, noise_burst(0.018, sr, rng, 0.005, 0.18, cutoff=1100), 0.0, sr)

    elif kind == "tok_metal":
        body = 480.0 * jitter
        add_at(out, bell(body, dur, sr, 0.55, metallic=True), 0.0, sr)
        add_at(out, noise_burst(0.014, sr, rng, 0.003, 0.25, cutoff=4200), 0.0, sr)

    elif kind == "chirp_down":
        add_at(out, chirp(360 * jitter, 175, dur, sr, dur / 3.2, 0.75), 0.0, sr)
        add_at(out, noise_burst(0.012, sr, rng, 0.003, 0.09, cutoff=900), 0.0, sr)

    elif kind == "sweep_up":
        add_at(out, chirp(220, 760 * jitter, dur, sr, dur / 2.0, 0.35), 0.0, sr)
        add_at(out, noise_burst(dur, sr, rng, dur / 2.4, 0.42, cutoff=1800), 0.0, sr)

    elif kind == "sweep_down":
        add_at(out, chirp(900, 260 * jitter, dur, sr, dur / 2.2, 0.4), 0.0, sr)
        add_at(out, noise_burst(dur, sr, rng, dur / 2.0, 0.45, cutoff=1400), 0.0, sr)

    elif kind == "whoosh":
        add_at(out, noise_burst(dur, sr, rng, dur / 2.6, 0.7, cutoff=1100), 0.0, sr)

    elif kind == "rattle":
        count = 5
        for i in range(count):
            at = 0.012 + i * (dur * 0.62 / count) + rng.uniform(0, 0.008)
            add_at(out, sine(500 + i * 70 + rng.uniform(-40, 40),
                             min(0.05, dur - at), sr, 0.011, 0.42), at, sr)
            add_at(out, noise_burst(0.012, sr, rng, 0.003, 0.20, cutoff=3000), at, sr)
        add_at(out, noise_burst(dur * 0.5, sr, rng, dur / 4.0, 0.20, cutoff=900), dur * 0.18, sr)

    elif kind == "marimba":
        f = D4 * jitter
        add_at(out, tone(f, dur, sr, dur / 2.3, 0.85, (1.0, 0.34, 0.10)), 0.0, sr)
        add_at(out, noise_burst(0.012, sr, rng, 0.003, 0.10, cutoff=2600), 0.0, sr)

    elif kind == "bell":
        add_at(out, bell(D5 * jitter, dur, sr, 0.9), 0.0, sr)

    elif kind == "bell_high":
        add_at(out, bell(A5 * jitter, dur, sr, 0.8), 0.0, sr)

    elif kind == "bell_metal":
        bases = [A4, FS4, D4]
        add_at(out, bell(bases[var % len(bases)] * jitter, dur, sr, 0.9, metallic=True), 0.0, sr)

    elif kind == "chime_cluster":
        for k, f in enumerate([D4, FS4, A4, D5, FS5]):
            add_at(out, sine(f * jitter, dur - k * 0.06, sr,
                             dur / (2.0 + k * 0.35), 0.42 - k * 0.05), k * 0.065, sr)
        add_at(out, sine(D3, dur, sr, dur / 2.0, 0.30), 0.0, sr)
        add_at(out, bell(D5, dur, sr, 0.28), 0.10, sr)

    elif kind == "wobble":
        add_at(out, chirp(240 * jitter, 165, dur, sr, dur / 2.6, 0.7), 0.0, sr)
        add_at(out, sine(180, dur, sr, dur / 3.0, 0.22), 0.0, sr)

    elif kind == "resolve":
        add_at(out, tone(A4 * jitter, dur * 0.5, sr, dur / 3.0, 0.7), 0.0, sr)
        add_at(out, tone(D4, dur * 0.5, sr, dur / 2.4, 0.7), dur * 0.38, sr)

    elif kind == "arp":
        notes = [D4, FS4, A4, D5, FS5]
        count = min(len(notes), 5)
        step = dur * 0.62 / count
        for i in range(count):
            add_at(out, tone(notes[i] * jitter, dur * 0.6, sr, dur / 3.4, 0.62),
                   i * step, sr)
        add_at(out, sine(D2, dur, sr, dur / 2.2, 0.32), 0.0, sr)
        add_at(out, noise_burst(0.03, sr, rng, 0.008, 0.16, cutoff=800), 0.0, sr)

    elif kind == "arp_long":
        notes = [D4, FS4, A4, D5]
        step = dur * 0.66 / len(notes)
        for i, f in enumerate(notes):
            add_at(out, tone(f * jitter, dur * 0.55, sr, dur / 3.6, 0.62), i * step, sr)
        add_at(out, sine(D2, dur, sr, dur / 2.0, 0.38), 0.0, sr)
        add_at(out, noise_burst(0.04, sr, rng, 0.010, 0.14, cutoff=700), 0.0, sr)

    elif kind == "shimmer":
        for k, f in enumerate([D5, FS5, A5]):
            add_at(out, sine(f * jitter, dur, sr, dur / (1.9 + k * 0.4),
                             0.36 - k * 0.06), k * 0.05, sr)
        add_at(out, noise_burst(dur, sr, rng, dur / 2.4, 0.14, cutoff=5200), 0.0, sr)

    elif kind == "stamp":
        add_at(out, bell(FS5 * jitter, dur, sr, 0.7), 0.0, sr)
        add_at(out, noise_burst(0.06, sr, rng, 0.018, 0.36, cutoff=1400), dur * 0.5, sr)

    elif kind == "paper":
        add_at(out, noise_burst(dur * 0.8, sr, rng, dur / 2.0, 0.55, cutoff=2600), 0.0, sr)
        add_at(out, shimmer_pad(dur, sr), dur * 0.22, sr)

    elif kind == "pad_swell":
        add_at(out, pad_chord(dur, sr, [D3, A3, D4], 0.55), 0.0, sr)
        fade_edges(out, sr, in_ms=90, out_ms=180)

    elif kind == "creak":
        add_at(out, chirp(140, 92 * jitter, dur, sr, dur / 1.8, 0.42), 0.0, sr)
        add_at(out, noise_burst(dur, sr, rng, dur / 2.0, 0.24, cutoff=700), 0.0, sr)
        add_at(out, pad_chord(dur, sr, [D3, A3], 0.35), dur * 0.2, sr)

    elif kind == "hiss":
        add_at(out, chirp(760, 250, dur, sr, dur / 2.0, 0.28), 0.0, sr)
        add_at(out, noise_burst(dur, sr, rng, dur / 2.2, 0.55, cutoff=2400), 0.0, sr)

    elif kind == "ambience":
        # Low ember bed + sparse crackle. No tonal content, no rhythm.
        out = seamless_loop(_ambience_raw(dur, sr, rng), n, sr, fade_ms=200)
        return normalise(out, peak_dbfs)

    elif kind.startswith("stem_"):
        out = seamless_loop(_stem_raw(kind, dur, sr, rng), n, sr, fade_ms=80)
        return normalise(out, peak_dbfs)

    fade_edges(out, sr)
    return normalise(out, peak_dbfs)


def pad_chord(dur: float, sr: int, freqs: list[float], amp: float) -> list[float]:
    parts = []
    for i, f in enumerate(freqs):
        n = int(dur * sr)
        swell = [0.0] * n
        phase = i * 0.7
        for j in range(n):
            t = j / sr
            lfo = 0.55 + 0.45 * math.sin(TAU * 0.13 * t + phase)
            swell[j] = amp * lfo * math.sin(TAU * f * t + phase)
        parts.append(swell)
    return mix(*parts)


def shimmer_pad(dur: float, sr: int) -> list[float]:
    parts = [sine(f, dur, sr, dur / 3.0, 0.20) for f in (D5, FS5, A5)]
    return mix(*parts)


def _ambience_raw(dur: float, sr: int, rng: random.Random) -> list[float]:
    n = int(dur * sr) + int(0.2 * sr)
    bed = noise_burst(n / sr, sr, rng, 100.0, 0.42, cutoff=220, attack=0.25)
    out = pad_to(bed, n)[:n]
    # Sparse ember crackle, deliberately irregular so the loop point is hidden.
    for _ in range(14):
        at = rng.uniform(0.15, dur - 0.15)
        add_at(out, noise_burst(0.035, sr, rng, 0.008,
                                rng.uniform(0.10, 0.30), cutoff=2400), at, sr)
    # Slow bellows breath.
    for i in range(n):
        t = i / sr
        out[i] += 0.16 * math.sin(TAU * 0.09 * t) * math.exp(-((t - dur * 0.45) ** 2) / 6.0)
    return out


def _stem_raw(kind: str, dur: float, sr: int, rng: random.Random) -> list[float]:
    extra = int(0.1 * sr)
    n = int(dur * sr) + extra
    out = [0.0] * n
    beat = 60.0 / 78.0
    bar = 4 * beat

    if kind == "stem_bed":
        # Warm D major pad; chord changes once per bar, no melody, no rhythm.
        chords = [[D3, A3, D4], [B3, FS4, D4], [G3, D4, FS4], [A3, E4, A4]]
        for b in range(4):
            seg = pad_chord(bar + 0.1, sr, chords[b], 0.30)
            add_at(out, seg, b * bar, sr)
        for i in range(n):
            t = i / sr
            out[i] *= 0.72 + 0.28 * math.sin(TAU * 0.10 * t)

    elif kind == "stem_flow":
        # Light marimba eighths, D pentatonic. Melodic but never a lead hook.
        pattern = [0, 2, 4, 2, 5, 4, 2, 0, 1, 3, 5, 3, 4, 2, 1, 0,
                   0, 2, 4, 5, 4, 2, 1, 0, 2, 3, 5, 4, 2, 1, 0, 2]
        step = beat / 2
        for k, degree in enumerate(pattern):
            f = PENTATONIC[degree]
            add_at(out, tone(f, 0.30, sr, 0.10, 0.48, (1.0, 0.28, 0.07)),
                   k * step, sr)

    elif kind == "stem_heat":
        # Low warmth, not a threat: drone + soft breath on beats 1 and 3.
        for f, a in ((D2, 0.44), (D3, 0.22), (A3 * 0.5, 0.10)):
            for i in range(n):
                t = i / sr
                out[i] += a * (0.55 + 0.45 * math.sin(TAU * 0.06 * t)) * math.sin(TAU * f * t)
        for b in range(4):
            for beat_i in (0, 2):
                add_at(out, noise_burst(0.22, sr, rng, 0.075, 0.14, cutoff=620),
                       b * bar + beat_i * beat, sr)

    elif kind == "stem_draft":
        # Sparse high shimmer - the moment of choosing a rune.
        for k in range(8):
            f = [D5, FS5, A5, FS5, D5, A5, FS5, D5][k]
            add_at(out, bell(f, bar / 2, sr, 0.30), k * (bar / 2), sr)
        for i in range(n):
            t = i / sr
            out[i] += 0.05 * math.sin(TAU * 0.2 * t) * math.sin(TAU * A5 * t)
    return out


# event id -> (recipe kind, duration seconds)
EVENT_RECIPES: dict[str, tuple[str, float]] = {
    "ui.tap":          ("tok", 0.045),
    "ui.back":         ("tok_low", 0.060),
    "ui.open":         ("sweep_up", 0.150),
    "ui.close":        ("whoosh", 0.105),
    "ui.toggle":       ("tok", 0.050),
    "ui.disabled":     ("tok_low", 0.090),
    "tile.pick":       ("tok", 0.085),
    "tile.place":      ("tok_metal", 0.105),
    "tile.undo":       ("chirp_down", 0.075),
    "tile.shuffle":    ("rattle", 0.290),
    "word.solution":   ("marimba", 0.340),
    "word.keystone":   ("chime_cluster", 0.950),
    "word.bonus":      ("bell_high", 0.180),
    "word.reject":     ("wobble", 0.160),
    "flow.break":      ("resolve", 0.280),
    "forge.start":     ("sweep_up", 0.300),
    "forge.complete":  ("arp", 1.200),
    "forge.perfect":   ("shimmer", 0.800),
    "hint.reveal":     ("bell", 0.300),
    "nudge.reveal":    ("bell", 0.260),
    "heat.cool":       ("hiss", 0.700),
    "heat.threshold":  ("pad_swell", 0.600),
    "rune.draft.open": ("rattle", 0.600),
    "rune.pick":       ("bell_metal", 0.500),
    "rune.reroll":     ("rattle", 0.350),
    "run.start":       ("creak", 0.700),
    "run.end":         ("arp_long", 1.600),
    "coin.earn":       ("bell_high", 0.110),
    "mark.earn":       ("stamp", 0.450),
    "ad.reward":       ("resolve", 0.300),
    "atlas.unlock":    ("paper", 0.700),
    "daily.perfect":   ("arp", 1.000),
    "screen.whoosh":   ("whoosh", 0.180),
    "amb.forge_room":  ("ambience", 8.000),
}

STEM_RECIPES: dict[str, str] = {
    "bed": "stem_bed", "flow": "stem_flow", "heat": "stem_heat", "draft": "stem_draft"
}


def peak_from_lufs(lufs: float) -> float:
    """Greybox-only approximation. Final assets must be measured in LUFS."""
    return max(-30.0, min(-3.0, lufs + 12.0))


def main() -> int:
    catalog = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    res_root = GODOT
    rows: list[dict] = []
    total_bytes = 0
    generated = 0

    jobs: list[tuple[str, str, int, int, float, str, dict]] = []
    for e in catalog["events"]:
        for i, res in enumerate(e["streams"]):
            jobs.append((res, e["id"], i, len(e["streams"]),
                         e["loudnessTargetLufs"], e["bus"], e))
    for stem in catalog["music"]["stems"]:
        jobs.append((stem["path"], "music:" + stem["name"], 0, 1, -20.0, "Music", {}))
    amb = catalog["music"]["ambience"]
    jobs.append((amb["path"], "ambience", 0, 1, -30.0, "AMB", {}))

    # The room bed is referenced twice (as a looped event AND as music.ambience).
    # Write it once; the event form wins because it carries the production brief.
    seen: set[str] = set()
    jobs = [j for j in jobs if not (j[0] in seen or seen.add(j[0]))]

    loop_seconds = catalog["music"]["loopSeconds"]

    for res, event_id, var, count, lufs, bus, event in jobs:
        if not res.startswith("res://"):
            raise ValueError(f"not a res:// path: {res}")
        dest = res_root / res[len("res://"):]
        rng = random.Random(f"{event_id}:{var}")

        if event_id.startswith("music:"):
            stem_name = event_id.split(":", 1)[1]
            kind = STEM_RECIPES[stem_name]
            sr, dur = LOOP_RATE, loop_seconds
        elif event_id == "ambience":
            kind, sr, dur = "ambience", LOOP_RATE, 8.0
        else:
            if event_id not in EVENT_RECIPES:
                raise ValueError(f"no greybox recipe for event '{event_id}'")
            kind, dur = EVENT_RECIPES[event_id]
            sr = LOOP_RATE if event["loop"] else SFX_RATE

        peak = peak_from_lufs(lufs)
        buf = recipe(kind, dur, sr, rng, var, peak)
        size = write_wav(dest, buf, sr)
        total_bytes += size
        generated += 1

        pitch_mode = event.get("pitch", {}).get("mode", "fixed") if event else "fixed"
        rows.append({
            "event_id": event_id,
            "category": event.get("category", "music" if event_id.startswith("music:") else "ambience"),
            "bus": bus,
            "asset_path": res,
            "variation_index": var + 1,
            "variation_count": count,
            "sample_rate_hz": sr,
            "duration_ms": round(dur * 1000),
            "target_lufs": int(lufs),
            "greybox_peak_dbfs": round(peak, 1),
            "priority": event.get("priority", ""),
            "cooldown_ms": event.get("cooldownMs", ""),
            "max_instances": event.get("maxInstances", ""),
            "pitch_mode": pitch_mode,
            "loop": str(bool(event.get("loop", False)) or event_id.startswith("music:") or event_id == "ambience").lower(),
            "wired": str(bool(event.get("wired", False))).lower() if event else "true",
            "status": "GREYBOX - replace with recorded asset before soft launch",
            "trigger": event.get("trigger", "continuous bed" if not event else ""),
            "production_brief": event.get("productionBrief", "See AUDIO_DESIGN.md section 9 for loop/music delivery spec."),
            "mix_notes": event.get("mixNotes", ""),
        })

    MANIFEST_PATH.parent.mkdir(parents=True, exist_ok=True)
    with MANIFEST_PATH.open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)

    print(f"Generated {generated} greybox WAVs -> {res_root / 'audio'}")
    print(f"Total size: {total_bytes / 1024 / 1024:.2f} MiB")
    print(f"Manifest  : {MANIFEST_PATH.relative_to(REPO)} ({len(rows)} rows)")
    print("Reminder  : these are TEMP scratch sounds. Replace before soft launch.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
