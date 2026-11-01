# Security Policy

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting
(*Security → Report a vulnerability*) on https://github.com/aldiianshr-commits/iPhoneMiror. We aim to respond within 7 days.

## Security model

| Asset | Protection |
|---|---|
| Pair records (host private key that lets this PC talk to the iPhone) | Stored by Apple Mobile Device Service; iPhoneMiror's copy is encrypted with DPAPI (current user) in `%LOCALAPPDATA%\iPhoneMiror\pairing` |
| Lockdown sessions | TLS with the host certificate; the device certificate is pinned to the pair record |
| Apple Account | Sign in with Apple in the system browser; passwords never reach the app. Only subject and e-mail are kept, in Windows Credential Manager. Identity tokens are validated (signature via Apple's JWKS, issuer, audience, expiry, nonce, state) |
| Sign in callback | `state` and `nonce` are random per attempt and expire after 10 minutes; the relay page forwards only whitelisted parameters |
| WebDriverAgent | Reached through the usbmux tunnel (USB) or the local network (Wi-Fi). WebDriverAgent itself has no authentication: anyone on the same network can reach its ports while it runs. Prefer USB and stop the runner when not in use |
| Updates/binaries | Built by GitHub Actions from source; SHA-256 sums are published with each release. Binaries are not code-signed yet |

## Out of scope

iPhoneMiror does not bypass iOS security features and will not accept contributions that do.
