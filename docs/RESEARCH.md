# Research notes

This document records what was investigated, what is publicly documented, and which approaches are legally usable.
No Apple API, endpoint, protocol or authentication mechanism in iPhoneMiror is invented: each one below has a source.

## Apple iPhone Mirroring / Continuity

*iPhone Mirroring* (macOS Sequoia+) and Continuity are **private, undocumented** Apple protocols available only between a Mac and an
iPhone signed in to the same Apple Account. There is no public SDK or API for Windows. Implementing them would require reverse
engineering and circumventing device security, which this project does not do. Apple Account sign-in therefore **cannot** unlock
iPhone Mirroring and the app says so in the UI.

## Device discovery and USB communication

| Mechanism | Source | Used for |
|---|---|---|
| usbmux protocol (Apple Mobile Device Service, `127.0.0.1:27015` on Windows) | libusbmuxd (LGPL-2.1) `include/usbmuxd-proto.h`, `src/libusbmuxd.c` | Device list, attach/detach events, tunnels to device ports, pair-record storage |
| lockdownd (device port 62078) | libimobiledevice (LGPL-2.1) `src/lockdown.c`, `common/userpref.c` | Device info, pairing, TLS sessions, battery domain |
| Pairing certificates | libimobiledevice `pair_record_generate_keys_and_certs` | RSA-2048 root/host keys, device cert from `DevicePublicKey`, serial 0, 10 years, SHA-256 |
| mDNS `_apple-mobdev2._tcp` / `_remotepairing._tcp` | RFC 6762/6763; service names observed by libimobiledevice/pymobiledevice3 | Wi-Fi discovery |
| Bluetooth LE manufacturer data, company id `0x004C` | Bluetooth SIG assigned numbers; Windows `BluetoothLEAdvertisementWatcher` docs | Nearby Apple device presence |

iPhoneMiror **re-implements** these protocols in C#; no LGPL code is copied or linked.

## Real-time screen streaming

| Option | Assessment | Decision |
|---|---|---|
| WebDriverAgent MJPEG server (port 9100) | Open source (BSD-3), uses Apple's XCTest; requires a developer-signed runner on the device | **Implemented** (primary) |
| AirPlay screen mirroring receiver | Protocol reverse-engineered by UxPlay/RPiPlay (GPL-3.0); includes FairPlay handshake code | **Integrated as an external, separately installed program** (UxPlay); no AirPlay/FairPlay code in iPhoneMiror |
| QuickTime USB screen capture | Reverse-engineered (e.g. quicktime_video_hack); requires replacing the Apple USB driver with WinUSB, breaking iTunes/Apple Devices | Not implemented |
| `screenshotr` / DVT instruments services | Require mounting the (personalized) Developer Disk Image and iOS 17+ RemoteXPC tunnels; low frame rate | Not implemented in this release |

## Input forwarding

iOS exposes no public API for remote input from another computer. Legal options:

* **WebDriverAgent / XCTest** — synthesizes touches and key presses through Apple's UI-testing framework. **Implemented.**
  Routes verified against `WebDriverAgentLib/Commands/*.m`: `/wda/tap`, `/wda/doubleTap`, `/wda/touchAndHold`, W3C `/actions`
  (pointer type `touch`), `/wda/keys`, `/wda/homescreen`, `/wda/pressButton` (`home`, `volumeup`, `volumedown`), `/appium/settings`.
* **Bluetooth HID with AssistiveTouch** — the iPhone accepts a Bluetooth mouse/keyboard; Windows cannot act as a HID peripheral
  without extra hardware (e.g. an ESP32 BLE-HID bridge). Documented as possible future work.

## Sign in with Apple

Apple's REST flow (`https://appleid.apple.com/auth/authorize`, `response_type=code id_token`, `response_mode` `query`/`fragment`/`form_post`,
`state`, `nonce`) and identity-token validation against `https://appleid.apple.com/auth/keys` (RS256, `iss`, `aud`, `exp`, `nonce`).
Apple requires an HTTPS redirect URI, so a static relay page forwards the fragment to `iphonemiror://`. No password is ever handled.

## Open-source projects reviewed

| Project | License | Use |
|---|---|---|
| libimobiledevice / libusbmuxd | LGPL-2.1 | Protocol reference only |
| appium/WebDriverAgent | BSD-3-Clause | Runs on the iPhone (installed by the user); route/setting names referenced |
| FDH2/UxPlay | GPL-3.0 | Optional external program, launched as a separate process |
| pymobiledevice3 | GPL-3.0 | Background reading (iOS 17+ tunnels); no code used |
| quicktime_video_hack | MIT | Background reading; approach rejected (driver replacement) |
