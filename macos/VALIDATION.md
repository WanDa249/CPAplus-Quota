# macOS 0.1.2-beta interaction validation — 2026-10-24

**CI passed on both native architectures and downloadable Universal app produced. Physical Mac acceptance remains pending.**

- [Successful GitHub Actions run](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39997430290)
- Built source: [`eef60550a4355f70dd5627779c88beab0f2b88aa`](https://github.com/WanDa249/CPAplus-Quota/commit/eef60550a4355f70dd5627779c88beab0f2b88aa)
- [Recommended Universal Beta artifact](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39997430290/artifacts/12520107461) — built on Apple Silicon, runs on either CPU architecture. GitHub login required; 30-day retention.
- [Apple Silicon evidence](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39997430290/artifacts/12519449891), [Intel evidence](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39997430290/artifacts/12520402116), [Intel-built Universal app](https://github.com/WanDa249/CPAplus-Quota/actions/runs/39997430290/artifacts/12520132576).

## Actual macOS results

| Check | Apple Silicon runner | Intel runner |
| --- | --- | --- |
| Environment | macOS 15.7.9, Xcode 16.4, Swift 6.1.2 | macOS 15.7.9, Xcode 16.4, Swift 6.1.2 |
| Swift SDK compile and automated tests | 21 tests, 0 failures | 21 tests, 0 failures |
| Native release arm64 and x86_64 compile | passed both | passed both |
| Universal Mach-O and plist validation | passed | passed |
| Ad-hoc signature integrity | passed | passed |
| App process launch / clean exit | passed | passed |
| Upload Beta and logs | passed | passed |

The original fifteen XCTest cases cover account parsing, quota semantics, authorization, cooldown/backoff, real Keychain CRUD, identity rejection and real HTTPS/TLS trust; selection parsing and scope boundaries; actual PUT/auto-reset headers and paths; selection failure statuses, redirects, malformed confirmation and timeout without GET fallback; no writes on startup/polling/local display changes; server-confirmed display, cross-device changes, competing responses, reset to auto and old-server compatibility. Two real URLSession clients against the loopback fixture prove selection/readback/reset and unchanged quota success time.

Six new cases cover weekly-only/five-hour-only/unknown/absent-window menu text; explicit POST and PUT cancelling the exact held passive GET while duplicate actions and polling cannot replace the active operation; retained data and unchanged success time on HTTP 503 followed by GET recovery; 401/403 clearing retained data; note saving to the originally edited account despite another device changing the display, rejecting wrong origins and removed accounts; and actual NSHostingController fitting size staying 350×440 before, during and after data/account changes. All test keys and responses are synthetic. These tests do not establish production scheduler behavior or physical desktop usability. XCTest has twenty-one cases; the separate Swift Testing runner reports zero tests because the suite uses XCTest.

The HTTPS fixture uses only synthetic keys and a run-local self-signed certificate, trusted only by the test delegate. Production URLSession's rejection of that same untrusted certificate is part of the passing test. No production service or provider is contacted.

The launch check starts the bundled native executable, observes it alive, and verifies clean exit after four seconds. It does not inspect menus, verify installed Codex detection, exercise login startup, or constitute desktop UX acceptance.

## Downloaded artifact inspection

Recommended inner ZIP: `CPAplusQuota-0.1.2-beta-macos-universal-UNSIGNED.zip`

- Size: 378,343 bytes.
- SHA-256: `67852299ed612bd83c7b305a00142f22327043a26314d403479843a8c3315f3f`.
- Downloaded and checked against CI's `SHA256SUMS.txt`.
- Mach-O fat header inspected: exactly `x86_64` and `arm64`.
- Version 0.1.2/build 3, bundle ID, executable permission bits, ad-hoc signature resources, embedded guide and MIT license verified.
- Deployment target is macOS 13.0. Runtime CI was on macOS 15.7.9; macOS 13/14 are not separately runtime-tested.

## Physical Mac testing still required

See [installation and feedback checklist](README.md#实体-mac-验收与反馈). In particular, downloaded Gatekeeper behavior, actual menu/popover layout, focus, light/dark modes, notch/multiple displays, positive official Codex identity detection and launch/quit linkage, Keychain persistence across restarts/upgrades, actual Windows/Mac account-selection synchronization against a compatible CPAplus service, sleep/network recovery, and login registration/approval remain unaccepted.

The package has an ad-hoc signature only. No Developer ID or Apple notarization is present. Signing integrity success is not publisher trust or Gatekeeper acceptance. Formal distribution requires Developer ID signing, Hardened Runtime, notarization/stapling and successful physical Mac testing. No formal Release has been created.

## Earlier attempts

The initial runs were rejected before execution by an account billing lock. The owner resolved it; the first executable attempt exposed an overly short fixture readiness deadline. Increasing that deadline with process diagnostics produced the successful run above. These earlier failures are retained in Actions history and are not described as compiler successes.

The previous 0.1.0-beta passed both native runners in run 39362148256. For 0.1.1, Intel's first attempt passed all test/build/launch/upload steps, but a diagnostic cancellation raced with cleanup and marked the job cancelled. Its files were preserved locally and its job alone rerun without application-code changes. The final run conclusion is success. Both final artifacts and logs were downloaded, and both archive checksums, app versions and CPU slices verified.

For 0.1.2, run 39996929418 compiled both native test executables but failed two waits that asserted an exact global URLProtocol stop count; the other nineteen cases passed. Shared lifecycle callbacks can affect that count, so the fixture now checks the specific held request's stopped state and reports wait failures at their call sites. No product behavior was changed for this test correction. Run 39997430290 then passed all twenty-one cases on both CPUs, Universal packaging and launch. First-attempt evidence is retained in Actions. Final archives and logs from both CPUs were downloaded and inspected locally; they have different ZIP checksums, so use the checksum shipped with the chosen artifact.

Local Windows grammar/YAML/plist/Bash/Python checks also passed. This Mac iteration has no Windows source, UI, build script or Release changes and makes no production-server calls. Later documentation-only commits do not change the tested application source.
