# macOS Beta development

1. Inspect Windows contract and reference licenses — complete.
2. Implement isolated macOS Swift package, native status item, Keychain, lifecycle, settings — complete; SDK compilation passed.
3. Test mock HTTPS, quota semantics, authorization, refresh/backoff, Keychain on macOS CI — complete; eight XCTest cases passed on each native runner.
4. Build arm64 + x86_64, package Universal app, upload unsigned Beta and evidence — complete; both native runners built both architectures and launched the app successfully.
5. Write installation, privacy, research, and physical Mac acceptance checklist — complete; validation status explicitly records limits.

Success: green GitHub macOS run with downloadable app; Windows tree and published release unchanged.
Boundary: no production server, provider tokens, or official release publication.

Earlier billing blocker resolved by user. Successful run: https://github.com/WanDa249/CPAplus-Quota/actions/runs/39362148256 . Downloaded Universal archive and verified SHA-256, both CPU slices, bundle structure and permissions. Source commit 2e290938ea6190607c276c187aadf969c7aa34ca. Physical Mac acceptance and Developer ID/notarization are outside this CI-complete Beta milestone and remain prerequisites for formal distribution.
