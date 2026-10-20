import AppKit
import Security

public enum CodexIdentity {
    public static let bundleID = "com.openai.codex"
    public static let teamID = "2DC432GLL2"

    public static func verified(_ app: NSRunningApplication) -> Bool {
        guard app.bundleIdentifier == bundleID, let url = app.bundleURL else { return false }
        return verifiedBundle(at: url)
    }
    public static func verifiedBundle(at url: URL) -> Bool {
        guard Bundle(url: url)?.bundleIdentifier == bundleID else { return false }
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code else { return false }
        // Constants verified against OpenAI's codex-rs/cli/src/desktop_app/mac.rs.
        let expression = "anchor apple generic and identifier \"\(bundleID)\" and certificate leaf[subject.OU] = \"\(teamID)\""
        var requirement: SecRequirement?
        guard SecRequirementCreateWithString(expression as CFString, [], &requirement) == errSecSuccess else { return false }
        return SecStaticCodeCheckValidity(code, [], requirement) == errSecSuccess
    }
}
