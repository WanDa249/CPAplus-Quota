# macOS Beta validation — 2026-10-20

**CI passed and downloadable Universal app produced. Physical Mac acceptance remains pending.**

- [Successful GitHub Actions run](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39362148256)
- Built source: [`2e290938ea6190607c276c187aadf969c7aa34ca`](https://github.com/WanDa249/CPAplus-Quota/commit/2e290938ea6190607c276c187aadf969c7aa34ca)
- [Recommended Universal Beta artifact](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39362148256/artifacts/12237708092) — built on Apple Silicon, runs on either CPU architecture. GitHub login required; 30-day retention.
- [Equivalent Universal artifact built on Intel](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39362148256/artifacts/12237563778)
- [Apple Silicon evidence](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39362148256/artifacts/12237762993), [Intel evidence](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39362148256/artifacts/12237673414)

## Actual macOS results

| Check | Apple Silicon runner | Intel runner |
| --- | --- | --- |
| Environment | macOS 15.7.9, Xcode 16.4, Swift 6.1.2 | macOS 15.7.9, Xcode 16.4, Swift 6.1.2 |
| Swift SDK compile and automated tests | 8 tests, 0 failures | 8 tests, 0 failures |
| Native release arm64 and x86_64 compile | passed both | passed both |
| Universal Mach-O and plist validation | passed | passed |
| Ad-hoc signature integrity | passed | passed |
| App process launch / clean exit | passed | passed |
| Upload Beta and logs | passed | passed |

The eight XCTest cases exercise account parsing and invalid responses; Windows-compatible percentage and update-time semantics; unavailable/frozen/disabled/stale states; endpoint and key validation; GET/POST and 405 fallback; 401/403/429, refresh cooldown and exponential backoff; real Keychain read/write/replace/delete and service isolation; rejection of unrelated app identities; and real loopback HTTPS transport, redirects and TLS trust. Multiple scenarios are combined within each case. XCTest's eight tests passed; the separate Swift Testing runner reports zero tests because the suite uses XCTest.

The HTTPS fixture uses only synthetic keys and a run-local self-signed certificate, trusted only by the test delegate. Production URLSession's rejection of that same untrusted certificate is part of the passing test. No production service or provider is contacted.

The launch check starts the bundled native executable, observes it alive, and verifies clean exit after four seconds. It does not inspect menus, verify installed Codex detection, exercise login startup, or constitute desktop UX acceptance.

## Downloaded artifact inspection

Recommended inner ZIP: `CPAplusQuota-0.1.0-beta-macos-universal-UNSIGNED.zip`

- Size: 312,975 bytes.
- SHA-256: `7ee8425352bc1fbc34fd1f3cba0bd6069aa5423a0c00f0b839328c763d250744`.
- Downloaded and checked against CI's `SHA256SUMS.txt`.
- Mach-O fat header inspected: exactly `x86_64` and `arm64`.
- Bundle ID, executable permission bits, ad-hoc signature resources, embedded guide and MIT license verified.
- Deployment target is macOS 13.0. Runtime CI was on macOS 15.7.9; macOS 13/14 are not separately runtime-tested.

## Physical Mac testing still required

See [installation and feedback checklist](README.md#实体-mac-验收与反馈). In particular, downloaded Gatekeeper behavior, actual menu/popover layout, focus, light/dark modes, notch/multiple displays, positive official Codex identity detection and launch/quit linkage, Keychain persistence across restarts/upgrades, real CPAplus compatibility, sleep/network recovery, and login registration/approval remain unaccepted.

The package has an ad-hoc signature only. No Developer ID or Apple notarization is present. Signing integrity success is not publisher trust or Gatekeeper acceptance. Formal distribution requires Developer ID signing, Hardened Runtime, notarization/stapling and successful physical Mac testing. No formal Release has been created.

## Earlier attempts

The initial runs were rejected before execution by an account billing lock. The owner resolved it; the first executable attempt exposed an overly short fixture readiness deadline. Increasing that deadline with process diagnostics produced the successful run above. These earlier failures are retained in Actions history and are not described as compiler successes.

Local Windows grammar/YAML/plist/Bash/Python checks and loopback fixture checks also passed. Windows source, UI assets, build scripts and published v0.1.0-beta release have zero changes. Later documentation-only commits do not change the tested application source.
