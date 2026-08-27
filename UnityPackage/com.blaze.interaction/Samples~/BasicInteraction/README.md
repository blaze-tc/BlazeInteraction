# Basic Interaction

Open `BasicInteraction.unity`. The scene contains the single Interaction launcher, one EventSystem with `InteractionInputModule`, authored UGUI/Physics2D/Physics3D targets, and an on-screen Interaction diagnostics overlay.

1. Configure the `main` surface under **Project Settings > Blaze Interaction**.
2. Enter Play Mode and choose Radar or CameraVision in the Bridge device selector.
3. For Radar, click **一键模拟** when no physical radar is connected. Confirm the moving yellow cursor and the short-lived `Fp` ring particles still appear.
4. For CameraVision, select a camera and confirm every tracked hand renders 21 colored joints and 21 connecting bones. Colors identify tracks only; they do not mean left or right hand.
5. Move multiple hands through the frame. Skeletons update by stable track ID, disappear on Cancel/disconnect, and reuse pooled UGUI objects.
6. Confirm the overlay reports `IPC: CONNECTED`, the selected provider, and current point phases. CameraVision hands keep `Fp` empty because the 21 landmarks are carried by `Extensions["hand"]`.
