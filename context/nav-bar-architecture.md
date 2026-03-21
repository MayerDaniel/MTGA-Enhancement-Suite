# MTGA Nav Bar Architecture

## Key Classes

### NavBarController (Core.dll)
- `public class NavBarController : MonoBehaviour, IKeyDownSubscriber, IKeySubscriber, IBackActionHandler`
- Manages all top-level navigation buttons
- Decompiled: `decompiled/Core/Core/NavBarController.cs` (~1371 lines)

## Nav Bar GameObject Hierarchy

```
Base                          ← top-level horizontal layout
├── FlexibleSpace
├── Nav_Home                  ← HomeButton lives here (standalone in Base)
├── Base_Middle               ← HorizontalLayoutGroup, holds most tabs
│   ├── Background            ← dark bar behind tabs
│   ├── Nav_Profile           ← active
│   ├── Nav_Decks             ← active
│   ├── Nav_Collection        ← INACTIVE
│   ├── Nav_Packs             ← active
│   ├── Nav_Store             ← active
│   ├── Nav_Mastery           ← active (best clone source)
│   └── Nav_Achievements      ← INACTIVE (do NOT clone — hidden button = hidden clone)
├── Base_RightSide            ← currencies, settings, mailbox
└── UX_Redline                ← inactive
```

**Critical discovery:** `Nav_Achievements` is `active: False`. Cloning it produces an invisible button. Always clone an active button like `Nav_Mastery`.

**Base_Middle layout:** `HorizontalLayoutGroup` with `spacing=0`, `childForceExpandWidth=False`. Size is driven by layout, not fixed dimensions (`sizeDelta=(0,0)`, anchors `(0,0)-(0,0)`). Adding a child automatically expands it.

## Tab Button Structure (e.g., Nav_Mastery)

Each tab button GameObject has:
- `RectTransform`, `Animator`, `CustomButton`, `LayoutElement`, `MinSizeByTextAdjuster`
- Children:
  - `Divider` (two Image dividers)
  - `SparkyHighlight` (tutorial highlight)
  - `GradientSprite` (hover effect)
  - `Particles` (spawn effect)
  - `TabON/` → `Tab` (Image), `MythicBar` (Image), `Glow` (Image)
  - `NotifyDot` (notification pip)
  - Text child with `TextMeshProUGUI` + `Localize`

## BepInEx Plugin Lifecycle Issue

The BepInEx chainloader creates plugin GameObjects that MTGA **destroys** during scene transitions. `DontDestroyOnLoad` on the plugin itself does NOT prevent this.

**Solution:** Create a separate `GameObject` with `HideFlags.HideAndDontSave` + `DontDestroyOnLoad`, add a custom `MonoBehaviour` to it. This survives all scene loads.

```csharp
var persistentObj = new GameObject("Name");
persistentObj.hideFlags = HideFlags.HideAndDontSave;
DontDestroyOnLoad(persistentObj);
persistentObj.AddComponent<PluginBehaviour>();
```

## Our Injection Strategy
1. Harmony postfix on `NavBarController.Awake()` + polling coroutine as fallback
2. Clone `MasteryButton.gameObject` into `Base_Middle` (same parent)
3. `SetAsLastSibling()` to place at end
4. Destroy all `Localize` components on clone
5. Disable decorative children (Overlay, Indicator, Lock, NotifyDot, SparkyHighlight, Particles)
6. Clear OnClick listeners, set text to "MTGA-ES"
7. Wire OnClick to toggle panel

## CustomButton (Core.dll)
- `public class CustomButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler`
- Key properties: `OnClick` (UnityEvent), `OnMouseover`, `OnMouseoff`, `Interactable` (bool)
- `SetText(string text)` — sets `TextMeshProUGUI` child directly
- Decompiled: `decompiled/Core/Core/CustomButton.cs`

## Localize Component
- `Wotc.Mtga.Loc.Localize : MonoBehaviour`
- Calls `DoLocalize()` on `OnEnable()` — will overwrite any text we set
- **Must be destroyed** on cloned buttons to prevent text overwrite

## NavContentType Enum
```csharp
None, EventLanding, Home, Draft, Store, DeckListViewer, BoosterChamber,
DeckBuilder, ConstructedDeckSelect, Profile, SealedBoosterOpen, LearnToPlay,
RewardTrack, RewardTree, ChallengeEventLanding, TableDraftQueue, PacketSelect,
Achievements, PrizeWall, FactionalizedEvent
```

## Access Patterns
```csharp
WrapperController.Instance.NavBarController
SceneLoader.GetSceneLoader().GetNavBar()
UnityUtilities.FindObjectOfType<NavBarController>(true)
```
