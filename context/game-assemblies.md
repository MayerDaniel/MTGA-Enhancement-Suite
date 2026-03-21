# MTGA Game Assemblies

## Location
```
C:\Program Files\Wizards of the Coast\MTGA\MTGA_Data\Managed\
```

## Key Assemblies

| Assembly | Size | Purpose |
|----------|------|---------|
| `Core.dll` | ~12 MB | Main game logic — nav bar, UI controllers, deck builder, match manager, all features |
| `SharedClientCore.dll` | ~1.5 MB | Shared client code, event page models |
| `Wizards.MDN.GreProtobuf.dll` | ~1.4 MB | Protobuf message definitions for game server communication |
| `Assembly-CSharp.dll` | ~330 KB | Rendering features, HasbroGo SDK, scene control (smaller than expected) |

## Pre-Decompiled Sources
All key assemblies are decompiled to `decompiled/` at the repo root (gitignored):
```
decompiled/
├── Core/              # ~5,900 .cs files
├── SharedClientCore/  # ~1,236 .cs files
├── Assembly-CSharp/   # ~144 .cs files
└── GreProtobuf/       # ~405 .cs files
```

To regenerate after an MTGA patch:
```bash
rm -rf decompiled/Core
"C:/tools/dnspy/dnSpy.Console.exe" --no-color --no-resources \
  -o decompiled/Core \
  "C:/Program Files/Wizards of the Coast/MTGA/MTGA_Data/Managed/Core.dll" \
  --asm-path "C:/Program Files/Wizards of the Coast/MTGA/MTGA_Data/Managed/"
```

## Core.dll Namespace Breakdown (top 10 by file count)

| Namespace | Files | Contents |
|-----------|-------|----------|
| `Wotc.*` | 1,785 | Main game systems (events, providers, extensions, loc, wrapper) |
| `AssetLookupTree` | 1,455 | Asset loading and lookup system |
| `Epic.*` | 446 | Epic Games integration |
| `Wizards.*` | 290 | Arena enums, models, inventory, matchmaking |
| `Meta.*` | 196 | Main navigation, achievements, challenges, rewards |
| `Shared.*` | 123 | Shared utilities |
| `Code.*` | 96 | Client feature toggles, decks, input |
| `EventPage.*` | 81 | Event page components and layouts |
| `MTGA.*` | 38 | Keyboard manager, localization |
| `GreClient.*` | 33 | Game rules engine client |
| (root) | ~1,300+ | NavBarController, CustomButton, Bootstrap, GameManager, all UI controllers |

## Unity Modules Available
Key Unity DLLs in the Managed folder:
- `UnityEngine.CoreModule.dll` — GameObject, MonoBehaviour, Transform, etc.
- `UnityEngine.UI.dll` — UGUI (Image, Button, Canvas, LayoutGroup, etc.)
- `Unity.TextMeshPro.dll` — TextMeshProUGUI for text rendering
- `UnityEngine.IMGUIModule.dll` — Immediate mode GUI (debug UIs)
- `UnityEngine.UIModule.dll` — Canvas, CanvasRenderer

MTGA uses **UGUI** (UnityEngine.UI), NOT UIElements/UIToolkit.

## Bootstrap Sequence
1. `Bootstrap.Awake()` → `DontDestroyOnLoad`, starts `Coroutine_GameStartup()`
2. Platform auth, Wwise audio, server connection, login
3. `Coroutine_PostLogInSequence()` → loads NavBar scene, then individual page scenes
4. NavBar scene loads → `NavBarController.Awake()` fires (our patch point)
