import Foundation

public struct QuotaWindow: Decodable {
    public let remainingPercent: Double?
    public let resetAt: String?

    public var percent: String {
        guard let value = remainingPercent, value.isFinite, (0...100).contains(value) else { return "—" }
        // .NET's default numeric formatting rounds midpoint values away from zero.
        return "\(Int(value.rounded(.toNearestOrAwayFromZero)))%"
    }
}

public struct QuotaAccount: Decodable, Identifiable {
    public let alias: String
    public let displayName: String?
    public let supported: Bool?
    public let frozen: Bool?
    public let disabled: Bool?
    public let available: Bool?
    public let availabilityKnown: Bool?
    public let cacheStatus: String?
    public let stale: Bool?
    public let lastSuccessAt: String?
    public let lastAttemptAt: String?
    public let fiveHour: QuotaWindow?
    public let weekly: QuotaWindow?
    public var id: String { alias }
    public var usable: Bool { availabilityKnown == true && available == true && frozen != true && disabled != true }
    public var state: String {
        if frozen == true { return "已冻结" }
        if disabled == true { return "已停用" }
        if availabilityKnown != true { return "状态未知" }
        return available == true ? "可用" : "不可用"
    }
    public var cacheState: String {
        if supported != true { return "不支持额度查询" }
        if cacheStatus == "error" { return stale == true ? "缓存过期 · 查询失败" : "查询失败 · 显示上次结果" }
        if stale == true { return "缓存过期 · 非实时额度" }
        return cacheStatus == "unavailable" ? "暂无额度缓存" : ""
    }
    public func expired(at serverNow: Date, staleAfter: Int?) -> Bool {
        if stale == true { return true }
        if [fiveHour?.resetAt, weekly?.resetAt].compactMap({ parseDate($0) }).contains(where: { $0 <= serverNow }) { return true }
        guard let success = parseDate(lastSuccessAt) else { return false }
        if success.timeIntervalSince(serverNow) > 60 { return true }
        guard let limit = staleAfter, limit > 0 else { return false }
        return serverNow.timeIntervalSince(success) > Double(limit)
    }
    public func menuTitle(expired: Bool) -> String {
        if !usable { return "CPA · \(state)" }
        if supported != true { return "CPA · 不支持查询" }
        let warning = expired || cacheStatus == "error" ? " !" : ""
        return "5h \(fiveHour?.percent ?? "—") · 周 \(weekly?.percent ?? "—")\(warning)"
    }
}

public struct QuotaResult: Decodable {
    public let accounts: [QuotaAccount]
    public let serverTime: String?
    public let staleAfterSeconds: Int?
    public var refreshStatus: String?

    public static func decode(_ data: Data) throws -> QuotaResult {
        let result: Self
        do { result = try JSONDecoder().decode(Self.self, from: data) }
        catch { throw APIError.invalidResponse }
        guard result.accounts.allSatisfy({ !$0.alias.isEmpty && !$0.alias.contains(where: { $0.isNewline }) }),
              Set(result.accounts.map(\.alias)).count == result.accounts.count else { throw APIError.invalidResponse }
        return result
    }
    public var refreshFeedback: String {
        switch refreshStatus {
        case "success": return "额度已更新。"
        case "deferred": return "查询冷却中，保留上次额度，请稍后再试。"
        case "error": return "额度查询失败，保留上次结果；未更新成功时间。"
        case "unsupported": return "此账号不支持额度查询。"
        case "not_supported": return "已读取缓存；服务器不支持主动查询，额度更新时间未改变。"
        case "cached": return "已读取服务器缓存。"
        default: return "已读取服务器缓存；成功时间未改变表示暂无新额度。"
        }
    }
}

public func parseDate(_ value: String?) -> Date? {
    guard let value else { return nil }
    let formatter = ISO8601DateFormatter()
    if let date = formatter.date(from: value) { return date }
    formatter.formatOptions.insert(.withFractionalSeconds)
    return formatter.date(from: value)
}

public enum Endpoint {
    public static func normalize(_ input: String) throws -> String {
        guard let url = URLComponents(string: input.trimmingCharacters(in: .whitespacesAndNewlines)),
              url.scheme?.lowercased() == "https", let host = url.host, !host.isEmpty,
              url.user == nil, url.password == nil, url.query == nil, url.fragment == nil,
              url.path.isEmpty || url.path == "/",
              url.port == nil || (1...65535).contains(url.port!) else { throw APIError.invalidEndpoint }
        var normalized = url
        normalized.scheme = "https"
        normalized.host = host.lowercased()
        normalized.path = ""
        if normalized.port == 443 { normalized.port = nil }
        guard let result = normalized.string else { throw APIError.invalidEndpoint }
        return result
    }
    public static func validKey(_ key: String) -> Bool {
        !key.isEmpty && key.utf8.count <= 512 && key.unicodeScalars.allSatisfy { (33...126).contains($0.value) }
    }
}

public enum APIError: Error, LocalizedError, Equatable {
    case invalidEndpoint, invalidKey, invalidResponse, unauthorized, forbidden, redirect
    case http(Int, TimeInterval)
    public var errorDescription: String? {
        switch self {
        case .invalidEndpoint: return "请输入 HTTPS 服务根地址，不含路径、凭据或参数。"
        case .invalidKey: return "CPA API Key 格式无效。"
        case .invalidResponse: return "服务器额度响应格式无效。"
        case .unauthorized: return "API Key 无效或已过期，请在设置中更换。"
        case .forbidden: return "当前密钥没有额度权限或未绑定账号，请联系管理员。"
        case .redirect: return "服务返回重定向，已阻止发送凭据。请核对服务根地址。"
        case .http(let code, _): return "服务请求失败（HTTP \(code)）；保留上次额度。"
        }
    }
}

public struct RequestGate {
    public private(set) var readAfter = Date.distantPast
    public private(set) var refreshAfter = Date.distantPast
    private var failures = 0
    public init() {}
    public mutating func beginRefresh(now: Date) { refreshAfter = now.addingTimeInterval(60) }
    public mutating func success() { failures = 0; readAfter = .distantPast }
    public mutating func failure(_ error: Error, now: Date) {
        failures = min(failures + 1, 6)
        var delay = min(300, 15 * pow(2, Double(failures - 1)))
        if case APIError.http(_, let retry) = error { delay = max(delay, retry) }
        if let api = error as? APIError, api == .unauthorized || api == .forbidden { delay = 300 }
        readAfter = now.addingTimeInterval(delay)
        refreshAfter = max(refreshAfter, readAfter)
    }
}
