# BepInEx Setup & Build Workflow

## BepInEx Version
- **BepInEx 5.4.23.2** (Mono, x64)
- NOT BepInEx 6 (IL2CPP) — MTGA uses Mono runtime

## Installation Location
```
C:\Program Files\Wizards of the Coast\MTGA\
├── winhttp.dll              # Unity doorstop — makes BepInEx load on game start
├── doorstop_config.ini      # Doorstop configuration
├── MTGA.exe                 # Game executable
└── BepInEx/
    ├── core/                # BepInEx runtime DLLs
    ├── plugins/             # Plugin DLLs go here
    │   └── MTGAEnhancementSuite/
    │       └── MTGAEnhancementSuite.dll
    └── config/              # Created on first run
        └── BepInEx.cfg      # Set [Logging.Console] Enabled = true for debug console
```

## Build References
BepInEx core DLLs for compilation are in `lib/` (gitignored):
- `lib/BepInEx.dll`
- `lib/0Harmony.dll`

## Build Command
```bash
cd <repo-root>
dotnet build Plugin/MTGAEnhancementSuite.csproj
```
Output: `Plugin/bin/Debug/MTGAEnhancementSuite.dll`

## Deploy Command
```bash
cp Plugin/bin/Debug/MTGAEnhancementSuite.dll \
   "C:/Program Files/Wizards of the Coast/MTGA/BepInEx/plugins/MTGAEnhancementSuite/"
```

## Enable BepInEx Console (for debugging)
After first launch, edit `C:\Program Files\Wizards of the Coast\MTGA\BepInEx\config\BepInEx.cfg`:
```ini
[Logging.Console]
Enabled = true
```
A console window will appear alongside MTGA showing all plugin logs.

## Plugin Structure
```csharp
[BepInPlugin("com.mtgaenhancement.suite", "MTGA Enhancement Suite", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    // Awake() → Harmony.PatchAll()
    // OnDestroy() → UnpatchSelf()
}
```

## Project References (.csproj)
All game DLLs referenced with `<Private>false</Private>` (do not copy to output):
- `Core.dll` — main game logic (NavBarController, CustomButton, etc.)
- `UnityEngine.CoreModule.dll` — Unity core types
- `UnityEngine.UI.dll` — UGUI components
- `Unity.TextMeshPro.dll` — Text rendering
- `UnityEngine.UIModule.dll` — Canvas system
- `UnityEngine.IMGUIModule.dll` — IMGUI (transitive dependency)

## Important Notes
- MTGA's own updater does NOT touch the `BepInEx/` folder — plugin survives patches
- MTGA patches every 2-3 weeks — class names in `Core.dll` can change
- All reflected names are centralized in `GameRefs.cs` for easy fixup
- Every Harmony patch is wrapped in try/catch to fail gracefully
