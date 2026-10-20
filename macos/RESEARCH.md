# Native implementation decisions

Reviewed on 2026-10-20 before implementation. This client is independently written; no third-party source or assets were copied and no external Swift package is required.

| Reference | Reviewed revision | License | Relevant design and scope |
| --- | --- | --- | --- |
| [HemSoft/codexbar-mac](https://github.com/HemSoft/codexbar-mac/tree/2f727dbb97d5b5ccbf5b331d7ae40d82a17c1ad3) | `2f727db` | [MIT, HemSoft 2026](https://github.com/HemSoft/codexbar-mac/blob/2f727dbb97d5b5ccbf5b331d7ae40d82a17c1ad3/LICENSE) | SwiftUI MenuBarExtra; Security generic-password Keychain; SMAppService; signed/notarized ZIP and Sparkle. Provider discovery, OAuth, history, alerts and Sparkle are outside this client's scope. |
| [wjx8/CodexBar](https://github.com/wjx8/CodexBar/tree/2a2fe461b7f2a01948754212edc29ddffaf31df0) | `2a2fe46` | [MIT, Jack Chen / wjx8 2026](https://github.com/wjx8/CodexBar/blob/2a2fe461b7f2a01948754212edc29ddffaf31df0/LICENSE) | Swift Package/AppKit, lifecycle notifications and polling. Name/path fallbacks in its lifecycle monitor are intentionally excluded here because they do not establish app identity. Touch Bar, voice input and supplier credentials are outside scope. |

## Menu bar and lifecycle

Both MenuBarExtra and NSStatusItem are native supported APIs. [NSStatusItem](https://developer.apple.com/documentation/appkit/nsstatusitem) gives this small agent explicit control over `isVisible`, label width, and an AppKit transient NSPopover without an additional SwiftUI scene lifecycle. The label contains only bounded status text and two percentages, not server-provided account names. SwiftUI supplies semantic system colors, normal controls and accessibility labels inside the popover/settings.

App lifecycle notifications come from [NSWorkspace.notificationCenter](https://developer.apple.com/documentation/appkit/nsworkspace/didlaunchapplicationnotification). KVO of runningApplications covers background-app changes; a 15-second reconciliation handles wake/races. Verified process results are cached by PID and launch date until that process disappears, avoiding repeated signature assessments. No active-window inspection, Accessibility, screen recording, or event taps are used. Automatic mode means the app process is running, not a window is in the foreground.

The official [OpenAI desktop installer source](https://github.com/openai/codex/blob/main/codex-rs/cli/src/desktop_app/mac.rs) establishes `com.openai.codex` and `2DC432GLL2`. Detection requires bundle metadata plus Apple-anchored code signature identifier and certificate team. Unknown/invalid builds fail closed; the user can still select always-show. Real installed Codex versions must be tested by Mac users; CI does not assert a positive vendor-app detection without that app present.

## Credentials and login

Only the CPA key is stored with Security's SecItem APIs, as a nonsynchronizing generic password scoped to the normalized HTTPS origin. Reads, updates and deletes have explicit failure handling. Keychain errors never fall back to plaintext. Settings are persisted only after saving the credential succeeds; a server change requires a freshly entered key and cancels/ignores previous in-flight results.

[SMAppService.mainApp](https://developer.apple.com/documentation/servicemanagement/smappservice/mainapp) replaces a bundled helper or custom launch agent and establishes macOS 13 as the minimum. The UI exposes system approval state and errors. Registration behavior for downloaded, ad-hoc-signed Apps still requires physical-device verification.

## Transport and semantics

The contract was read from the repository's Windows `QuotaApi.cs` and `AccountPresentation.cs`. GET is cache-only, POST is explicit and optional, `X-Quota-Account` uses the returned opaque alias without its presentation prefix. `remainingPercent` is displayed directly; `lastSuccessAt` is the actual upstream success timestamp. Unknown availability fails closed. Old cache remains visible with warnings on network errors; 401/403 clears visible accounts. Results are not written to disk. HTTPS uses system trust, no redirect, no cookie storage or HTTP cache; system proxy settings remain native to macOS.

The 0.1.1-beta account-pool update follows the newer local Windows/server implementation's optional `selection` and `PUT /v1/me/account-selection` contract. It sets a key-scoped server runtime preference rather than a global priority or local display preference. Selection/reset requires an explicit button action; GET, startup and local display choices never issue PUT. The returned scoped account list and selection are authoritative, including another device winning a race or the server resetting to auto. No provider request, administrator credential, production mutation or server-source publication is part of this Mac update.

## Build and distribution

Swift Package Manager plus a small bundle script is sufficient; an Xcode project generator is unnecessary. Each native [GitHub-hosted macOS runner](https://docs.github.com/en/actions/reference/runners/github-hosted-runners) runs tests, then builds arm64 and x86_64 and joins them with lipo. CI packages with ditto, validates bundle structure and ad-hoc signature, records logs and checksums, and uploads Actions artifacts. There is no Release upload step. No Developer ID credentials are requested or embedded.

The minimum production distribution path is Developer ID signing with Hardened Runtime, Apple notarization, and stapling, followed by physical Mac acceptance. Beta documentation explicitly distinguishes integrity checks, local ad-hoc signatures and publisher trust. No system security bypass is used as a distribution solution.
