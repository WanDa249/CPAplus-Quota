# Validation status — 2026-10-20

## Actual macOS CI attempt

Run: https://github.com/WanDa249/CPAplus-Quota/actions/runs/39360788662

Commit: `6706e63c9a228824272d94aa61dd2608b6ef8eea`

Both `macos-15` (arm64) and `macos-15-intel` jobs were rejected before checkout. GitHub check annotations report exactly:

> The job was not started because your account is locked due to a billing issue.

There are no compiler/test logs and no app artifact from this attempt. This is an account-level Actions lock, not an SDK compilation result. The account owner must resolve the billing issue. Then rerun the latest branch run; do not treat the failed attempt as a build.

## Local Windows checks

- All nine Swift files parsed with tree-sitter-swift. This checks grammar only, not Apple SDK types, linking, or deployment compatibility.
- GitHub Actions YAML and app Info.plist parsed; menu-only mode and macOS 13 target checked.
- Both Bash scripts passed `bash -n`; Python HTTPS fixture passed syntax compilation.
- The mock server ran locally over real loopback TLS with a generated certificate and Python client. Cache timestamp stability, POST update timestamp, 401/403/302/429 and account-header isolation passed. This validates the fixture, not the Swift transport.
- `git diff --check` passed. Windows source/assets/build script, public Windows README/privacy/notices, and release assets have not been changed.

## Still unverified

Swift SDK compilation, Swift automated tests, real macOS Keychain behavior, native launch smoke test, arm64/x86_64 binaries, Universal packaging and downloaded Gatekeeper behavior are pending a working macOS runner. All desktop interaction checks in README additionally require a physical Mac even after CI passes.
