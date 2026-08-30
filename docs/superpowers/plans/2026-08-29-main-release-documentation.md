# Blaze Interaction v1.1.0 Main Release Implementation Plan

> **For Codex:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Organize the repository, document installation/features/code extension paths, merge the verified Radar + CameraVision implementation into `main`, and publish a downloadable GitHub Release.

**Architecture:** Preserve the existing Interaction Core / Provider / IPC / Unity package boundaries. Treat `UnityPackage/com.blaze.interaction` as the distributable source, rebuild its embedded Windows Bridge from repository source, and publish one versioned `.tgz` asset. Keep historical specifications and test reports, while adding a concise documentation index that distinguishes active manuals from historical records.

**Tech Stack:** .NET 8, WPF, xUnit, PowerShell, Unity 2021.3+, UPM/npm package format, GitHub CLI.

---

### Task 1: Establish the v1.1.0 release identity

- [ ] Add or update release-identity tests before production changes.
- [ ] Change package, Bridge, provider manifests, and release scripts to `1.1.0` without changing Interaction IPC protocol version 1.
- [ ] Run the focused release and compatibility test projects.

### Task 2: Organize and complete repository documentation

- [ ] Add `docs/README.md` as the canonical documentation map.
- [ ] Refresh `README.md` and `INSTALL.md` for local `.tgz` installation and source installation.
- [ ] Add screenshot-led feature documentation for device selection, Radar, CameraVision, and Unity data consumption.
- [ ] Add repository structure, architecture, extension, build, test, and release guidance for future maintainers.
- [ ] Add `v1.1.0` release notes and clearly label historical plans/specifications.

### Task 3: Produce and inspect screenshots

- [ ] Capture current application and Unity views without exposing unrelated desktop content.
- [ ] Store optimized PNG files under `docs/images/`.
- [ ] Inspect every image and verify every Markdown image path.

### Task 4: Rebuild and verify distributable artifacts

- [ ] Run the complete .NET solution tests.
- [ ] Publish the self-contained Windows Bridge and both providers into the Unity package.
- [ ] Run embedded Bridge smoke tests for Radar and CameraVision.
- [ ] Run Unity EditMode and PlayMode package tests.
- [ ] Pack `com.blaze.interaction-1.1.0.tgz`, record SHA-256, and inspect archive contents.

### Task 5: Review and commit the release candidate

- [ ] Review the diff for accidental generated files, secrets, or user-owned untracked files.
- [ ] Commit the release candidate on `codex/camera-vision-hand-mvp-0-1`.
- [ ] Perform the required code-review pass and resolve any actionable findings.

### Task 6: Merge, push, and publish GitHub Release

- [ ] Update local `main` from `origin/main` without touching preserved untracked files.
- [ ] Merge the feature branch into `main` and rerun release-critical verification.
- [ ] Push `main`, create and push annotated tag `v1.1.0`.
- [ ] Create GitHub Release `Blaze Interaction SDK 1.1.0` with the `.tgz` asset and release notes.
- [ ] Download the published asset, compare SHA-256, and verify the public release URL.
