# Basic Interaction

Import **Basic Interaction** from Package Manager, open `BasicInteraction.unity`, and enter Play Mode. The scene keeps the normal Unity input stack: `RadarInputModule` drives the existing `EventSystem`; `GraphicRaycaster`, `PhysicsRaycaster`, and `Physics2DRaycaster` then reach the authored Button, Toggle, Slider, ScrollRect, 3D target, and 2D target. No adapter component replaces their native pointer interfaces.

The right-side diagnostics deliberately separate two signals:

- `ScreenFrameReceived` is filtered to the configured primary screen (`main`) and supplies live `PRIMARY`, `FRAME`, and pointer data.
- `PointerFrameReceived` remains visible as a compatibility-frame counter so an existing single-screen integration can be checked during the 1.2.0 upgrade.
- Frame history is capped at 200 lines; EventSystem/UGUI/error history is capped at 300 lines. `Down`, `Up`, and errors are immediate. Repeated `Move` entries are sampled at 10 Hz per pointer, while the live position always refreshes.

## Path A — mouse debug

1. Select `EventSystem > RadarInputModule` in the Hierarchy.
2. Set **Input Mode** to `RadarAndMouseDebug`.
3. Enter Play Mode.
4. Click the **Radar Button** and **Radar Toggle**. Press and drag the **Radar Slider**, drag/scroll the log area, then click and drag both the **3D Physics Target** and **2D Physics Target**.
5. Confirm the live panel says `INPUT: RadarAndMouseDebug` and the event history shows the ordinary `PointerEnter → PointerDown → PointerUp → PointerClick` callbacks. Drag paths also show `InitializeDrag → BeginDrag → Drag → EndDrag`.

Mouse debug uses native pointer ID `-1`. It verifies scene wiring and raycasters; it does not verify IPC.

## Path B — embedded Bridge Simulation

1. Select `EventSystem > RadarInputModule` and set **Input Mode** to `RadarOnly`.
2. Start the embedded `RadarBridge.exe` from the SDK launcher and select **Simulation** for the sensor assigned to screen `main`.
3. Connect Unity and confirm the badge changes to `IPC CONNECTED`.
4. Confirm `PRIMARY: main`, increasing frame sequence values, current frame age, pointer count, and dropped-batch count.
5. Move a simulated target over the same UGUI, 3D, and 2D targets. A normal interaction should report screen ID `main` with phase order `Hover/Move → Down → Move → Up`, plus the same native EventSystem callbacks used in Path A.
6. Verify each frame pointer line contains `[main/P{id}]`, normalized coordinates, screen-local pixel coordinates, and confidence.

If a control changes in Path A but not Path B, compare the IPC status, primary screen ID, frame age, pointer phase sequence, and EventSystem hit target before changing scene code.

## Player logs

The on-screen history is the fastest field diagnostic. The complete standalone log is also written to:

```text
%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\Player.log
```

In the Unity Editor, use **Console > Open Editor Log**. Preserve `Player.log` together with the RadarBridge run log when reporting an IPC, mapping, or raycast issue.
