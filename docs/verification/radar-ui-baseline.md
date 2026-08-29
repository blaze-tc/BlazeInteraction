# Radar UI Stability Baseline

Captured on 2026-08-28 before the Radar UI stability changes, at commit
`1874d49`.

## Automated load fixture

- `RadarUiLoadFixture.CreateSnapshot(100_000, 42)` creates 100,000 deterministic,
  finite raw points and 100,000 deterministic, finite valid points.
- `ManualRadarUiDispatcher` retains callbacks without running them and drains them
  in FIFO order. It is the deterministic queue used by the later coalescing and
  render-scheduling tests.
- The focused characterization run passed: 2/2 tests.

## Pre-change regression baseline

All suites passed before production code was changed:

| Suite | Result |
| --- | ---: |
| Radar Bridge WPF | 164/164 |
| Radar Configuration | 51/51 |
| Radar Processing | 66/66 |
| Radar IPC | 26/26 |
| Radar Provider | 49/49 |

## Live process baseline

The measurements below are process samples rather than profiler estimates. CPU
percentage is normalized by the machine's logical processor count.

| Scenario | Input/output state | Interval | CPU time | Normalized CPU | Working set | Threads | Responding |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| Standalone console idle | No Unity topology; simulation start rejected | 10 s | 4.922 s | 3.076% | 159.3 to 159.5 MB | 30 | Yes |
| Project-scoped Bridge simulation | Unity connected; Radar 1/1 connected; 28 raw, 28 filtered, 2 fused targets | 10.01 s | 8.484 s | 5.297% | 225.1 to 225.6 MB | 45 | Yes |

Three point views were visible simultaneously during the simulation sample:
raw points, filtered points, and fused-screen output.

## Reproduced pre-change behavior

- Starting a standalone console without the Unity project topology produced the
  expected association error and could not be used as a simulation-load sample.
- With the project-scoped Bridge and Unity in Play Mode, simulation ran and the
  console continued responding; no unhandled exception was visible in the
  console log during the sample.
- Activating **放大编辑** did not expose an editor child window in the currently
  packaged Bridge. The main process stayed alive and responsive, but continuous
  editor zoom and parameter-entry measurements could therefore not be collected
  honestly from that package. This is retained as a pre-change lifecycle failure
  observation and will be covered by deterministic lifecycle/render tests before
  the package is republished.

The 100,000-point fixture is deliberately much larger than the 28-point live
simulation frame so point-budget and pending-work bounds can be verified without
depending on hardware or timing.

## Post-change automated gate

Captured on 2026-08-29 after the bounded rendering and expanded-editor lifecycle
changes.

- The deterministic 100,000-point UI fixture passes with bounded retained point
  collections and coalesced dispatcher work.
- The expanded editor lifecycle test passes for 100 consecutive open/close
  cycles, including owner-close cleanup.
- The complete RadarControl regression set passes: 484/484 tests.
- The complete BlazeInteraction solution passes: 966/966 tests, including all
  Radar provider and compatibility suites.

Status: **AUTOMATED GATE PASS**. A 30-minute live Radar hardware soak with
100,000 points per frame was not performed in this session and remains an
explicit manual verification item; it is not represented as completed here.
