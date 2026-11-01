# Installation

## 1. Prerequisites on the PC

| Component | Why | How |
|---|---|---|
| Windows 11 (x64 or ARM64) | Target OS | — |
| **Apple Mobile Device Service** | USB driver + usbmuxd used for USB and Wi-Fi connections | Install **Apple Devices** from the Microsoft Store, or **iTunes for Windows** |
| UxPlay *(optional)* | AirPlay screen mirroring receiver (view only) | Build/install UxPlay for Windows (MSYS2 + GStreamer, see UxPlay's README) and set its path in *Settings → Connection* |

## 2. Install iPhoneMiror

Download from the repository's **Releases** page (`nightly` pre-release or a tagged version):

* `iPhoneMiror-Setup-<version>-x64.exe` — installer (per-user, no admin rights). Registers the `iphonemiror://` link used by Sign in with Apple.
* `iPhoneMiror-win-x64-portable.zip` — portable folder; run `iPhoneMiror.exe`.
* ARM64 equivalents for Windows on ARM.

Windows SmartScreen may warn because the binaries are not code-signed; choose *More info → Run anyway* if you trust the build
(verify the SHA-256 in `SHA256SUMS.txt`).

## 3. Connect the iPhone

1. Plug in the iPhone, unlock it, tap **Trust** and enter the passcode.
2. Optional Wi-Fi: in Apple Devices (or iTunes) select the iPhone and enable **Show this iPhone when on Wi-Fi**. Keep both on the same network.

## 4. Screen mirroring and control (WebDriverAgent)

iOS has no public API that lets a Windows app capture the screen or inject touches. iPhoneMiror uses **WebDriverAgent**
(Appium, BSD-3-Clause), a test runner built on Apple's XCTest framework that provides an MJPEG screen stream and touch/keyboard
commands. Installing it requires a Mac with Xcode and an Apple ID for signing (free accounts work; apps expire after 7 days):

```bash
git clone https://github.com/appium/WebDriverAgent.git
cd WebDriverAgent
# open WebDriverAgent.xcodeproj, set your Team for WebDriverAgentRunner, then:
xcodebuild -project WebDriverAgent.xcodeproj -scheme WebDriverAgentRunner \
  -destination 'id=<iPhone UDID>' -allowProvisioningUpdates test
```

Keep the runner active on the iPhone (Developer Mode must be enabled on iOS 16+). Then in iPhoneMiror: *Mirroring* →
*WebDriverAgent stream* → **Start Mirroring**. Ports default to 8100 (HTTP) and 9100 (MJPEG) and can be changed in Settings.

## 5. AirPlay alternative (UxPlay)

Set the path to `uxplay.exe` in *Settings → Connection*, choose *AirPlay via UxPlay* on the Mirroring page and press **Start**.
On the iPhone open *Control Center → Screen Mirroring* and pick the receiver (default name "iPhoneMiror"). UxPlay shows the video in
its own window. Input control is not available over AirPlay.

## 6. Sign in with Apple (optional)

Requires an Apple Developer account:

1. Create a **Services ID** with *Sign in with Apple* enabled.
2. Host `tools/apple-signin-relay/index.html` on HTTPS (e.g. GitHub Pages) and register that URL as a Return URL of the Services ID.
3. In iPhoneMiror → *Apple Account*, enter the Services ID and redirect URI, **Save**, then **Sign in with Apple**.

## Build from source

```powershell
dotnet --version   # 10.0.x
dotnet test iPhoneMiror.Libraries.slnx -c Release
dotnet publish src/iPhoneMiror.App/iPhoneMiror.App.csproj -c Release -r win-x64 -p:Platform=x64 -o publish/win-x64
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DArch=x64 /DSourceDir="$PWD\publish\win-x64" installer\iPhoneMiror.iss
```

## Data locations and uninstall

See [ARCHITECTURE.md](ARCHITECTURE.md#persistence). Uninstalling removes the program and protocol registration; delete
`%LOCALAPPDATA%\iPhoneMiror` to remove settings, logs and pairing copies.
