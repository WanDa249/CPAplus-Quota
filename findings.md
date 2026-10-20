# Findings

- Windows source: src/QuotaApi.cs, AccountPresentation.cs, ServiceEndpoint.cs. HTTPS origin only; Bearer key; X-Quota-Account strips `账号 ` prefix. GET reads cache, POST optional; 404/405 fallback to GET. lastSuccessAt is upstream success, not client fetch time.
- Both reference repositories inspected at local shallow clones; MIT (HemSoft 2026; Jack Chen/wjx8 2026). No source copied. HemSoft uses MenuBarExtra with complex multiprovider/Sparkle stack; wjx8 uses NSWorkspace plus name/path fallbacks, which this implementation will avoid.
- AppKit NSStatusItem chosen for explicit visibility and bounded text; SwiftUI for popover/settings. macOS 13 minimum for SMAppService.
- Verified identity constants from OpenAI primary source: https://github.com/openai/codex/blob/main/codex-rs/cli/src/desktop_app/mac.rs — com.openai.codex, team 2DC432GLL2. Require Apple anchored signature as well; no name/path-only matching.
- NSWorkspace lifecycle notifications need workspace.notificationCenter. No Accessibility or screen recording needed for running-app mode.
- gh authenticated as repository owner with repo/workflow scopes; independent clone and macos/native-beta branch created.
