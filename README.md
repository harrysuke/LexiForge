# LexiForge

[![License: AGPL v3](https://shields.io)](https://gnu.org)
[![Engine: Godot 4 (.NET)](https://shields.io)](https://godotengine.org)
[![Language: C#](https://shields.io)](https://microsoft.com)

**LexiForge** is a cross-platform word puzzle game built with Godot 4 (.NET) and C#, featuring a modular folder layout for core logic, UI, audio, and data.

---

## Quick Setup

1. **Clone the repository:**
   ```bash
   git clone https://github.com
   cd LexiForge
   ```
2. **Open in Godot:** Import `project.godot` using Godot 4 (.NET).
3. **Build and Run:** Restore/build `LexiForge.csproj` and run the project via the editor.


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

## License

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)** — see the [LICENSE.txt](LICENSE.txt) file for full details.
