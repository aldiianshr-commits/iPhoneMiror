# Contributing to iPhoneMiror

Thanks for helping! The most valuable contribution right now is **testing on real iPhones** (see
[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md#how-to-verify)) and reporting the results.

## Ground rules

* Use only public APIs, documented protocols, or open protocols with a cited source. Do not invent Apple endpoints or protocol
  messages, and do not add code that bypasses iOS security (jailbreak exploits, DRM/FairPlay circumvention, passcode bypass).
* Never handle Apple Account passwords. Authentication goes through Apple's own web page only.
* Check licenses before using third-party code. GPL code must not be linked into the app; LGPL protocol knowledge must be
  re-implemented, not copied. Record new dependencies in `THIRD-PARTY-NOTICES.md`.
* No placeholder buttons: every UI action must do something real or be hidden.
* Do not claim a feature works on a device or iOS version unless you tested it; update the verification log.

## Development

```powershell
dotnet test iPhoneMiror.Libraries.slnx -c Release          # runs on Windows, macOS, Linux
dotnet build src/iPhoneMiror.App -c Debug -p:Platform=x64   # Windows only
```

* C# latest, nullable enabled, file-scoped namespaces, `async` all the way, cancellation tokens on I/O.
* Libraries (`Core`, `Connection`, `DeviceDiscovery`, `Mirroring`, `Input`) must not reference WinUI.
* New protocol code needs unit tests; flows that touch the device need an integration test against the emulators in
  `tests/iPhoneMiror.IntegrationTests/Fakes`.

## Pull requests

1. Fork and create a branch from `main`.
2. Keep changes focused; update docs when behaviour changes.
3. CI must be green (tests on Windows and Linux, x64/ARM64 publish, installer, UI smoke test).
