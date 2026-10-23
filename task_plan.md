# macOS Beta development

1. Inspect Windows contract and reference licenses — complete.
2. Implement isolated macOS Swift package, native status item, Keychain, lifecycle, settings — complete; SDK compilation passed.
3. Test mock HTTPS, quota semantics, authorization, refresh/backoff, Keychain on macOS CI — complete; eight XCTest cases passed on each native runner.
4. Build arm64 + x86_64, package Universal app, upload unsigned Beta and evidence — complete; both native runners built both architectures and launched the app successfully.
5. Write installation, privacy, research, and physical Mac acceptance checklist — complete; validation status explicitly records limits.

Success: green GitHub macOS run with downloadable app; Windows tree and published release unchanged.
Boundary: no production server, provider tokens, or official release publication.

Earlier billing blocker resolved by user. Successful run: https://github.com/WanDa249/CPAplus-Quota/actions/runs/39362148256 . Downloaded Universal archive and verified SHA-256, both CPU slices, bundle structure and permissions. Source commit 2e290938ea6190607c276c187aadf969c7aa34ca. Physical Mac acceptance and Developer ID/notarization are outside this CI-complete Beta milestone and remain prerequisites for formal distribution.

## Account-pool parity update

6. Read local Windows/server account-selection contract — complete. This feature is separate from public v0.1.1 display changes and from local main-account selection.
7. Add explicit preferred-account and auto-reset actions, server-confirmed state and old-server compatibility — complete.
8. Verify HTTP/HTTPS behavior, state synchronization, no automatic PUT and error paths on both macOS runners; deliver new unsigned Universal Beta — complete; run 39411760648 is green, 15 XCTest cases per architecture. Recommended artifact 12259358288. Source 0a7fa8a.

Boundary remains Mac source and synthetic tests only; do not modify production, Windows code or Releases.

## Windows 0.1.2 parity — 2026-10-24

9. Compare public Windows ee629f2 and local follow-up fixes with Mac — complete. Sync quota and interaction semantics; retain native anchored popover rather than porting Windows edge-dock geometry.
10. Implement weekly-only display, foreground action priority, stable detail layout and account-bound note drafts — complete; compiled on both native architectures, view-size checks passed.
11. Add cancellation/race/error/note/weekly regressions; build and test on both macOS runners — complete. First run 39996929418 failed two fixture waits; eef6055 passed 21 tests per CPU, Universal packaging/signature/plist and native launch in run 39997430290.
12. Inspect downloadable 0.1.2-beta Universal app, document evidence and remaining real-Mac acceptance — complete. Both final artifacts/logs downloaded; version/build, executable bits, two CPU slices, bundled docs/license and SHA-256 verified. Recommended artifact 12520107461; SHA-256 67852299ed612bd83c7b305a00142f22327043a26314d403479843a8c3315f3f. Only docs updated after tested source; physical Mac acceptance and formal signing/notarization remain pending.
