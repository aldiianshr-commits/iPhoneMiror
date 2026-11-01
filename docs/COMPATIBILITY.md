# Compatibility

> **Status of this build: not yet verified on a physical iPhone.** All device-facing code is implemented against documented or
> well-established open protocols and is exercised in CI against protocol emulators, but no real iPhone was available while this
> build was produced. Nothing below is claimed as "verified" until it has been tested on real hardware and recorded here.

## Windows

| Target | Status |
|---|---|
| Windows 11 x64 | Builds in CI (`windows-latest`); app launched and all pages rendered in CI (screenshots in the README) |
| Windows 11 ARM64 | Builds in CI (cross-compiled); not executed — no ARM64 runner used |
| Windows 10 (1809+) | Should work (minimum OS 10.0.17763); Mica backdrop falls back to a solid background |

Requirements: **Apple Mobile Device Service**, installed with *Apple Devices* (Microsoft Store) or *iTunes for Windows*. The app is
self-contained (.NET runtime and Windows App SDK included).

## iOS

| iOS | Detection & pairing | Wi-Fi | Mirroring (WebDriverAgent) | Input (WebDriverAgent) | Mirroring (AirPlay/UxPlay) |
|---|---|---|---|---|---|
| 26.x | Implemented · not verified | Implemented · not verified | Needs WDA · not verified | Needs WDA · not verified | Needs UxPlay · not verified |
| 27.x | Implemented · not verified (iOS 27 is not released as of November 2026) | same | same | same | same |
| ≤ 18.x | Not targeted; detection likely works | — | — | — | — |

The app reads the iOS version automatically (`ProductVersion`) and shows a per-feature compatibility report for every device in
Device Manager (`CompatibilityEvaluator`). When a version is verified on hardware, add it to the `verifiedVersions` list passed
to `CompatibilityEvaluator` and record the result below.

## Verification log

| Date | Device | iOS | Connection | Result | Tester |
|---|---|---|---|---|---|
| — | — | — | — | No hardware verification yet | — |

### How to verify

1. Install the build, connect the iPhone by USB, unlock it, tap **Trust**. Expected: Device Manager shows name, model, iOS version and
   battery; status *Connected*, trust *Trusted*.
2. Unplug and re-plug. Expected: *Reconnecting* → *Connected* without a new Trust prompt.
3. With Wi-Fi sync enabled (Apple Devices → *Show this iPhone when on Wi-Fi*), unplug. Expected: device appears with connection *Wi-Fi*.
4. Install and run WebDriverAgent on the iPhone, start mirroring. Expected: Display window shows the screen; fps shown in the toolbar.
5. Click, double-click, drag, wheel, type, Ctrl+Shift+H in the Display window. Expected: the iPhone reacts.
6. AirPlay: set the UxPlay path, choose *AirPlay via UxPlay*, start, select the receiver in Control Center → Screen Mirroring.
