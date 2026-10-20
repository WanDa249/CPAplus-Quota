# macOS 0.1.1-beta account-pool validation — 2026-10-20

**CI passed on both native architectures and downloadable Universal app produced. Physical Mac acceptance remains pending.**

- [Successful GitHub Actions run](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39411760648)
- Built source: [`0a7fa8a072b602579a4b22e57270556949afad67`](https://github.com/WanDa249/CPAplus-Quota/commit/0a7fa8a072b602579a4b22e57270556949afad67)
- [Recommended Universal Beta artifact](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39411760648/artifacts/12259358288) — built on Apple Silicon, runs on either CPU architecture. GitHub login required; 30-day retention.
- [Apple Silicon evidence](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39411760648/artifacts/12259223258), [Intel evidence](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39411760648/artifacts/12260572418), [Intel-built Universal app](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39411760648/artifacts/12260142945).

## Actual macOS results

| Check | Apple Silicon runner | Intel runner |
| --- | --- | --- |
| Environment | macOS 15.7.9, Xcode 16.4, Swift 6.1.2 | macOS 15.7.9, Xcode 16.4, Swift 6.1.2 |
| Swift SDK compile and automated tests | 15 tests, 0 failures | 15 tests, 0 failures |
| Native release arm64 and x86_64 compile | passed both | passed both |
| Universal Mach-O and plist validation | passed | passed |
| Ad-hoc signature integrity | passed | passed |
| App process launch / clean exit | passed | passed |
| Upload Beta and logs | passed | passed |

The original eight XCTest cases cover account parsing, quota semantics, authorization, cooldown/backoff, real Keychain CRUD, identity rejection and real HTTPS/TLS trust. Seven additional cases cover selection parsing and scope boundaries; actual PUT/auto-reset headers and paths; 401/403/404/405/409/429, redirects, malformed confirmation and timeout without GET fallback; actual app-state flows with no writes on startup/polling/local display selection; busy and invalid-account guards; server-confirmed main display, cross-device changes and reset to auto; failure readback without retrying PUT; competing-device response handling; and old-server compatibility. The additional HTTPS case uses two real URLSession clients against the loopback fixture to prove selection/readback/reset and unchanged quota success time. These test the client and synthetic server contract, not the production scheduler or a physical desktop. XCTest has fifteen cases; the separate Swift Testing runner reports zero tests because the suite uses XCTest.

The HTTPS fixture uses only synthetic keys and a run-local self-signed certificate, trusted only by the test delegate. Production URLSession's rejection of that same untrusted certificate is part of the passing test. No production service or provider is contacted.

The launch check starts the bundled native executable, observes it alive, and verifies clean exit after four seconds. It does not inspect menus, verify installed Codex detection, exercise login startup, or constitute desktop UX acceptance.

## Downloaded artifact inspection

Recommended inner ZIP: `CPAplusQuota-0.1.1-beta-macos-universal-UNSIGNED.zip`

- Size: 347,420 bytes.
- SHA-256: `cdd8df5f48952acfe282c44d94251ac2238e2368e7887b1b083f6315826e016b`.
- Downloaded and checked against CI's `SHA256SUMS.txt`.
- Mach-O fat header inspected: exactly `x86_64` and `arm64`.
- Bundle ID, executable permission bits, ad-hoc signature resources, embedded guide and MIT license verified.
- Deployment target is macOS 13.0. Runtime CI was on macOS 15.7.9; macOS 13/14 are not separately runtime-tested.

## Physical Mac testing still required

See [installation and feedback checklist](README.md#实体-mac-验收与反馈). In particular, downloaded Gatekeeper behavior, actual menu/popover layout, focus, light/dark modes, notch/multiple displays, positive official Codex identity detection and launch/quit linkage, Keychain persistence across restarts/upgrades, actual Windows/Mac account-selection synchronization against a compatible CPAplus service, sleep/network recovery, and login registration/approval remain unaccepted.

The package has an ad-hoc signature only. No Developer ID or Apple notarization is present. Signing integrity success is not publisher trust or Gatekeeper acceptance. Formal distribution requires Developer ID signing, Hardened Runtime, notarization/stapling and successful physical Mac testing. No formal Release has been created.

## Earlier attempts

The initial runs were rejected before execution by an account billing lock. The owner resolved it; the first executable attempt exposed an overly short fixture readiness deadline. Increasing that deadline with process diagnostics produced the successful run above. These earlier failures are retained in Actions history and are not described as compiler successes.

The previous 0.1.0-beta passed both native runners in run 39362148256. For 0.1.1, Intel's first attempt passed all test/build/launch/upload steps, but a diagnostic cancellation raced with cleanup and marked the job cancelled. Its files were preserved locally and its job alone rerun without application-code changes. The final run conclusion is success. Both final artifacts and logs were downloaded, and both archive checksums, app versions and CPU slices verified.

Local Windows grammar/YAML/plist/Bash/Python checks also passed. This Mac iteration has no Windows source, UI, build script or Release changes and makes no production-server calls. Later documentation-only commits do not change the tested application source.
