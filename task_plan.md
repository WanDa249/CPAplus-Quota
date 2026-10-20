# macOS Beta development

1. Inspect Windows contract and reference licenses — complete.
2. Implement isolated macOS Swift package, native status item, Keychain, lifecycle, settings — source written, SDK compilation pending.
3. Test mock HTTPS, quota semantics, authorization, refresh/backoff, Keychain on macOS CI — blocked by account billing lock before job start.
4. Build arm64 + x86_64, package Universal app, upload unsigned Beta and evidence — pending working Runner; no binaries produced.
5. Write installation, privacy, research, and physical Mac acceptance checklist — complete; validation status explicitly records limits.

Success: green GitHub macOS run with downloadable app; Windows tree and published release unchanged.
Boundary: no production server, provider tokens, or official release publication.

External blocker: https://github.com/WanDa249/CPAplus-Quota/actions/runs/39360788662 — GitHub: "The job was not started because your account is locked due to a billing issue." User action required; cannot be fixed by source changes.
