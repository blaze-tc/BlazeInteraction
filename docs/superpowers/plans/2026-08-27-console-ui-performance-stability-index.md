# Console UI and Stability Implementation Index

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the approved shared console styling, CameraVision three-preview workspace, and Radar high-load stability changes through independently testable plans.

**Architecture:** Execute three plans in order. The shared theme plan creates the UI foundation, the Camera plan consumes it without changing Unity's public interaction API, and the Radar plan hardens the existing point-cloud UI before the final cross-stack regression gate.

**Tech Stack:** .NET 8, WPF, xUnit, OpenCvSharp, Blaze Interaction IPC, Unity 2021.3 package tests

**Spec:** `docs/superpowers/specs/2026-08-27-console-ui-performance-stability-design.md`

## Global Constraints

- Write a failing test before every production change.
- Run existing Radar regression tests before and after every task that can affect Radar.
- Camera UI preview defaults to at most 15 FPS; Camera capture, inference, and Unity output keep their configured rates.
- Camera uses one `InteractionPoint` per hand: `PixelPosition` is the hand center and `Fp` contains all 21 hand landmarks.
- Radar keeps its existing contract: `PixelPosition` is the target center and `Fp` contains complete real scan points.
- Display throttling, trail reduction, and visual sampling must never alter processing or IPC payloads.
- Do not introduce Camera-specific Unity receiving APIs or require changes to existing Unity consumer code.

## Execution Order and Checkpoints

1. `docs/superpowers/plans/2026-08-27-shared-console-theme.md`
   - Checkpoint: selector, provider header, Radar, and Camera can resolve the same theme resource keys.
   - Required Radar gate: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`.
2. `docs/superpowers/plans/2026-08-27-camera-control-workspace.md`
   - Checkpoint: real-aspect three-preview workspace, device mode dropdowns, latest-only 15 FPS UI rendering, and Camera `Fp` compatibility.
   - Required Camera gate: all `Blaze.Provider.CameraVision.Tests` pass.
3. `docs/superpowers/plans/2026-08-27-radar-ui-stability.md`
   - Checkpoint: latest-only UI dispatch, bounded display workload, responsive editing, editor lifecycle safety, and full Gate A regression.
   - Required final gate: Radar, Interaction Core, Provider, IPC, release layout, and Unity package tests all pass.

Each plan ends in a separate commit. Stop at its checkpoint if any existing Radar test regresses.
