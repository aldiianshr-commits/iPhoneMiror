# Architecture

iPhoneMiror is a .NET 10 / WinUI 3 desktop application split into small libraries that communicate through interfaces
defined in `iPhoneMiror.Core`. Everything except the UI project and the Bluetooth scanner is platform-neutral and is
unit- and integration-tested on both Windows and Linux in CI.

```
┌──────────────────────────────────────────────── iPhoneMiror.App (WinUI 3, MVVM) ───────────────────────────────────────────────┐
│ MainWindow (NavigationView)  Dashboard · Connect iPhone · Device Manager · Mirroring · Apple Account · Settings                │
│ DisplayWindow "iPhoneMiror Display"  (frames → Image, pointer/keyboard → GestureTranslator → IInputForwarder)                  │
│ ViewModels (CommunityToolkit.Mvvm) · DeviceHub · ThemeService · WindowService · WindowsCredentialStore · DPAPI protector        │
└───────────────┬──────────────────────┬───────────────────────┬──────────────────────┬──────────────────────┬──────────────────┘
                │                      │                       │                      │                      │
   iPhoneMiror.Connection   iPhoneMiror.DeviceDiscovery   iPhoneMiror.Mirroring    iPhoneMiror.Input      iPhoneMiror.Core
   usbmux client            mDNS / DNS-SD browser         WDA MJPEG source          WebDriverAgent client   models, plist codec,
   lockdown client + TLS    Windows BLE / classic BT      MJPEG parser, fps meter   gesture & key mapping   SQLite repository,
   pairing (cert gen)       DeviceDiscoveryService        UxPlay (AirPlay) runner   WdaInputForwarder       settings, logging,
   DeviceConnectionService                                MirroringService                                  Sign in with Apple
   DeviceTransport                                                                                          compatibility rules
```

## Data flow

1. **Detection.** `DeviceConnectionService` subscribes to Apple Mobile Device Service (usbmuxd, `127.0.0.1:27015`) with the
   usbmux `Listen` command and receives `Attached`/`Detached` events for USB and Wi-Fi (network) attachments.
2. **Information & trust.** For every attachment it opens `lockdownd` (device port 62078) through usbmux `Connect`, reads public
   values (`ProductType`, `ProductVersion`, …), loads a pair record (from the Apple service via `ReadPairRecord`, or the local
   DPAPI-protected copy) and starts a TLS session (`StartSession`, `EnableSessionSSL`) authenticated with the host certificate.
   If no valid record exists it generates root/host/device certificates (`PairRecordGenerator`) and sends `Pair`, polling while
   the iPhone shows **Trust This Computer?** (`PairingDialogResponsePending`), and saves the record with `SavePairRecord`.
3. **Reconnect.** Detach → `Reconnecting`; the next `Attached` event re-initializes with the stored record (no new prompt). If the
   service itself goes away the listen loop retries with exponential back-off (`ReconnectPolicy`).
4. **Mirroring.** `MirroringService` opens a tunnel to WebDriverAgent's MJPEG server (device port 9100, through usbmux over USB,
   or directly over Wi-Fi), parses `multipart/x-mixed-replace` frames (`MjpegStreamReader`) and raises `FrameReceived`.
   The Display window decodes only the newest frame (frames arriving while decoding are dropped to keep latency low).
   Stream quality is set on the device with `POST /session/{id}/appium/settings` (`mjpegServerFramerate`, …).
5. **Input.** Pointer events are mapped from view to device points (`CoordinateMapper`, letterbox-aware and orientation-aware),
   turned into gestures (`GestureTranslator`: tap, double tap, long press, swipe, wheel scroll) and sent to WebDriverAgent
   (`/wda/tap`, `/wda/doubleTap`, `/wda/touchAndHold`, W3C `/actions`, `/wda/keys`, `/wda/homescreen`, `/wda/pressButton`).
6. **AirPlay alternative.** `UxPlayReceiver` runs the separately installed UxPlay (GPL-3.0) as a child process and reports
   connection state by parsing its log. Video is rendered by UxPlay's own window; no input is possible over AirPlay.

## Key interfaces (iPhoneMiror.Core/Abstractions)

| Interface | Implementation | Purpose |
|---|---|---|
| `IDeviceConnectionService` | `DeviceConnectionService` | Attach/detach, info, pairing, reconnect |
| `IDeviceTransport` | `DeviceTransport` | TCP streams to device ports (usbmux or Wi-Fi) |
| `IDeviceDiscoveryService` | `DeviceDiscoveryService` | mDNS + Bluetooth discovery |
| `IMirroringService` | `MirroringService` | Session lifecycle, frames, stats |
| `IInputForwarder` | `WdaInputForwarder` | Touch, text and button input |
| `IMirrorQualityConfigurator` | `WdaInputForwarder` | Stream frame rate / quality / scale |
| `IAppleAccountService` | `AppleSignInService` | Sign in with Apple (OIDC) |
| `ICredentialStore` | `WindowsCredentialStore` | Windows Credential Manager |
| `IDataProtector` | `DpapiDataProtector` | Pair records at rest |
| `IDeviceRepository` | `SqliteDeviceRepository` | Recent devices and connection log |

## Persistence

| What | Where |
|---|---|
| Settings | `%LOCALAPPDATA%\iPhoneMiror\settings.json` |
| Recent devices, connection events | `%LOCALAPPDATA%\iPhoneMiror\iphonemiror.db` (SQLite, WAL) |
| Pair records (copy) | `%LOCALAPPDATA%\iPhoneMiror\pairing\*.pairrecord` (DPAPI, current user) |
| Apple identity (subject, e-mail) | Windows Credential Manager, target `iPhoneMiror/AppleAccount` |
| Logs | `%LOCALAPPDATA%\iPhoneMiror\logs\iPhoneMiror-yyyyMMdd.log` |

## Threading

Services raise events on thread-pool threads; `UiDispatcher` marshals them to the UI thread (`DeviceHub`, view models).
Per-device work is serialized with a semaphore; cancellations are cooperative.

## Testing

* `tests/iPhoneMiror.UnitTests` — codecs (XML/binary plist, usbmux, lockdown framing, DNS), certificate generation and chain
  validation, MJPEG parsing, coordinate/gesture/keyboard mapping, WebDriverAgent route usage, Sign in with Apple token validation,
  SQLite repository, settings, compatibility rules.
* `tests/iPhoneMiror.IntegrationTests` — runs the real client code against in-process **protocol emulators**: a usbmuxd that speaks
  the plist protocol on loopback, a lockdownd that performs pairing (including the pending Trust dialog and denial) and a real TLS
  session upgrade, and WebDriverAgent HTTP/MJPEG servers. These prove the implementation is consistent end-to-end; they do not
  replace testing with a physical iPhone (see [COMPATIBILITY.md](COMPATIBILITY.md)).
