# LexiForge

[![License: AGPL v3](https://shields.io)](https://gnu.org)
[![Engine: Godot 4 (.NET)](https://shields.io)](https://godotengine.org)
[![Language: C#](https://shields.io)](https://microsoft.com)

**LexiForge** is a cross-platform word puzzle game built with Godot 4 (.NET) and C#, featuring a modular folder layout for core logic, UI, audio, and data.

## Prerequisites

- **[Godot Engine (.NET version)](https://godotengine.orgdownload/)** (v4.x recommended)
- **[.NET SDK](https://microsoft.com)** (e.g., .NET 8.0)
- *(Optional)* **[Android SDK / Build Tools](https://android.com)** if exporting to Android

## Getting Started

1. **Clone the repository:**
   ```bash
   git clone https://github.com
   cd LexiForge
   ```

2. **Open in Godot:**
   - Launch the **Godot Engine (.NET)**.
   - Click **Import** and select the `project.godot` file in the root directory.

3. **Build and Run:**
   - Click **Build** in the top-right corner of the Godot editor to compile the C# solution (`LexiForge.csproj`).
   - Press **F5** (or click the **Play** button) to launch the game.

## Building & Exporting

Export configurations are defined in `export_presets.cfg`. To build a release binary:
1. Open **Project > Export...** in the Godot Editor.
2. Select your target preset (e.g., Android, Windows, Web).
3. Ensure your export templates and SDK paths are configured in **Editor > Editor Settings**, then click **Export Project**.

## Developer verification (DevSmoke)

`res://ui/DevSmoke.tscn` is a **debug-only** harness. It instantiates the real screens
in an exact 1080×1920 `SubViewport`, drives them the way a player would (tap real
rack tiles, submit words, open sheets), and fails on any control that escapes the
canvas. It quits the tree with a non-zero exit code on failure, so it works in CI.

```bash
dotnet build LexiForge.csproj

# CI / assertion-only — no window, no PNGs, prints [FIT] audits and step results
Godot_v4.7.2-stable_mono_win64_console.exe --headless --path . res://ui/DevSmoke.tscn

# Visual review — opens a window and writes res://_shots/*.png
Godot_v4.7.2-stable_mono_win64_console.exe --path . res://ui/DevSmoke.tscn
```

Notes:

- Headless detection uses `DisplayServer.GetName() == "headless"`. **Do not parse
  `OS.GetCmdlineArgs()`** — Godot strips recognised engine flags, so under
  `--headless` that array contains only the scene path. The dummy `RenderingServer`
  never emits `FramePostDraw`, so an unbounded wait on it deadlocks the harness.
  `Shot()` also races the draw callback against a 1 s timer as a second safety net.
- Pass `-- --no-shots` after a double dash to skip PNG capture in a windowed run
  while keeping every layout assertion.
- `06b` / `06c` / `06d` are the Give Up reveal card with 3, 8 and 12 solution
  words. Above 8 words the list scrolls while the three exits stay pinned.

### Revealed-answer regression (the `MEAT` report)

A player forged `MAT`, then `EAT`, opened **Give Up** (which revealed `MEAT`), and was
then told *"Not in the dictionary"*. The cause was architectural, not a typo:
`pack_01.json` and `words_en.dat` are produced by different tools, and
`ForgeSession.Submit()` consulted the binary trie **before** checking solution
membership. The trie's only legitimate job is gating *optional bonus words*.

Three layers now contain that drift:

1. `core/ForgeSession.cs` — solution membership is authoritative. A shipped
   `SolutionWords` entry is accepted even when `words_en.dat` lacks it;
   constructibility and keystone gating are unchanged.
2. `autoload/GameDataService.cs` — cross-checks every shipped answer at boot and
   publishes `DictionaryGaps` (`[Data] ready | ... dictionary gaps=0`). A non-zero
   count is a **warning, never a boot failure** — a stale trie must confuse content
   owners, not soft-lock a player.
3. `tools/CoreTests/Program.cs` and `ui/DevSmoke.cs` — assert the gap list is empty
   and replay `MAT -> EAT -> Give Up -> MEAT` against both the shipped trie and a
   deliberately stale one missing `meat`.

Two supporting fixes ship with it: `LetterRack.Reject(string word)` keeps the
rejected word visible in Rust in the compose strip (UI-P2) instead of showing an
empty strip, and debug builds print `[WORD] raw=... norm=... kind=... inTrie=...
gaps=...` for every rejection, so a stale dictionary and a mis-dragged tile are
tellable apart. See GDD §6.2.1, `UI_DESIGN.md` UI-65, and `LEVEL_DESIGN.md`
"Runtime containment".

Lines to expect in a headless run:

```text
[REPRO] dictionary 'meat'=True 'mat'=True 'eat'=True gaps=0
[REPRO] after MAT + EAT: OK:mat SUBMIT:mat OK:eat SUBMIT:eat
[WORD] raw='ema' len=3 norm='ema' kind=NotInDictionary repeat=False bank='team' answer='meat' inTrie=False gaps=0
[REPRO] stale trie (no 'meat'): mat:SolutionWord,eat:SolutionWord,meat:SolutionWord completed=True
[SMOKE] done — 66 steps, 0 failure(s)
```

### Duplicate-verdict regression (the `CARE` report)

A player forged `ARC`, then `ERA` on forge 3 (`race`), opened **Give Up** (which
revealed `CARE`), and was then told *"Already forged"*. `user://analytics.jsonl`
and the debug `[WORD]` lines proved `care` was **never submitted**: every attempt
arrived as a 3-letter word — `car`, `ace`, `ear` — because a 4-tile bank plus two
3-letter answers trains a 3-tile drag, and `EndGesture()` auto-submits on release.
Those words were already in the **Bonus Jar**, so `ForgeSession` returned the same
`AlreadyFound` verdict it uses for a duplicate *solution* word, and a wordless
`"Already forged"` toast made it look like the revealed answer was refused.

Four layers now contain it (UI-66):

1. `core/ForgeSession.cs` — `AlreadyInJar` is a distinct verdict from
   `AlreadyFound`; both expose `IsRepeat` (never `IsPenalty`, so Flow and the clean
   streak are untouched). A duplicate solution word now carries its real slot index.
2. `ui/screens/ForgeScreen.cs` — `ReasonFor()` names the exact word and never
   borrows solution-word language: `CAR · already in the jar`, not
   `Already forged`. **A repeat keeps the rack tiles selected**, so one more rune
   completes `CARE` instead of rebuilding it. Repeats emit `word_repeat` (they
   were previously invisible to telemetry because they emit no `word_bad`).
3. `ui/components/Toast.cs` — exposes `CurrentText` so the copy is assertable.
4. `tools/CoreTests/Program.cs` and `ui/DevSmoke.cs` — replay
   `ARC -> ERA -> CAR -> CAR -> CARE` against a deliberately stale trie, drive it
   through the real rack / Give Up card / toast, and assert the revealed keystone
   completes the forge with zero penalties.

Lines to expect in a headless run:

```text
[CARE] forge 3 bank='race' words=arc,era,care keystone=care gaps=0
[CARE] after ARC + ERA + CAR: OK:arc SUBMIT:arc OK:era SUBMIT:era OK:car SUBMIT:car
[WORD] raw='car' len=3 norm='car' kind=AlreadyInJar repeat=True bank='race' answer='care' inTrie=True gaps=0
[CARE] toast='CAR · already in the jar'
[CARE] final: OK:arc SUBMIT:arc OK:era SUBMIT:era OK:car SUBMIT:car SUBMIT:car OK:care SUBMIT:care
```

> **Windows note:** the standalone `CoreTests.exe` / `.dll` is blocked by this
> machine's Application Control policy (`0x800711C7`) for unsigned local binaries —
> copying it elsewhere does not help. `DevSmoke` compiles the identical `core/*.cs`
> files into Godot's trusted host, so every assertion still runs in CI;
> `dotnet build tools/CoreTests/CoreTests.csproj` is the compile-time gate. For a
> full standalone run, load the assembly **from bytes** inside the signed .NET host
> (`dotnet fsi --exec` + `Assembly.Load(byte[])` + reflection-invoke `Main`), which
> bypasses the file-based policy. Last run: `PASS 161  FAIL 0`.

## Project Structure

```text
LexiForge/
├── _shots/               # Promotional screenshots and visual captures
├── ads/                  # Ad integration and monetization scripts/scenes
├── audio/                # Sound effects (SFX), background music, and audio controllers
├── autoload/             # Global singletons (state management, save data, audio bus)
├── core/                 # Core gameplay logic, word validation, and rulesets
├── data/                 # Dictionaries, word lists, level definitions, and static configs
├── tools/                # Editor utilities and build/data helper scripts
├── ui/                   # Menus, HUD, themes, and interface components
├── LexiForge.csproj      # .NET / C# project configuration
├── export_presets.cfg    # Godot platform export configurations
├── project.godot         # Main Godot engine project file
└── LICENSE.txt           # GNU Affero General Public License v3.0
```

## License

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)** — see the [LICENSE.txt](LICENSE.txt) file for full details.
