import Foundation

// Redirects are refused even on the same origin. TLS uses system trust without exceptions.
public final class NoRedirectDelegate: NSObject, URLSessionTaskDelegate {
    public func urlSession(_ session: URLSession, task: URLSessionTask,
                           willPerformHTTPRedirection response: HTTPURLResponse,
                           newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

public final class QuotaAPI {
    private let origin: String
    private let session: URLSession
    public init(origin: String, session: URLSession? = nil) throws {
        self.origin = try Endpoint.normalize(origin)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil
        configuration.httpShouldSetCookies = false
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        self.session = session ?? URLSession(configuration: configuration, delegate: NoRedirectDelegate(), delegateQueue: nil)
    }
    deinit { session.invalidateAndCancel() }

    public func read(key: String) async throws -> QuotaResult { try await send(key: key, alias: nil) }
    public func refresh(key: String, alias: String) async throws -> QuotaResult {
        guard !alias.isEmpty, !alias.contains(where: { $0.isNewline || $0.asciiValue == 0 }) else { throw APIError.invalidResponse }
        return try await send(key: key, alias: alias)
    }
    private func send(key: String, alias: String?) async throws -> QuotaResult {
        guard Endpoint.validKey(key) else { throw APIError.invalidKey }
        guard let url = URL(string: origin + "/v1/me/quotas" + (alias == nil ? "" : "/refresh")) else { throw APIError.invalidEndpoint }
        var request = URLRequest(url: url)
        request.httpMethod = alias == nil ? "GET" : "POST"
        request.timeoutInterval = alias == nil ? 8 : 125
        request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization")
        request.setValue("no-cache, no-store", forHTTPHeaderField: "Cache-Control")
        if let alias { request.setValue(alias.replacingOccurrences(of: "账号 ", with: ""), forHTTPHeaderField: "X-Quota-Account") }
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw APIError.invalidResponse }
        let code = response.statusCode
        if code == 401 { throw APIError.unauthorized }
        if code == 403 { throw APIError.forbidden }
        if (300..<400).contains(code) { throw APIError.redirect }
        if alias != nil && (code == 404 || code == 405) {
            var cached = try await read(key: key)
            cached.refreshStatus = "not_supported"
            return cached
        }
        guard (200..<300).contains(code) else {
            throw APIError.http(code, Self.retryDelay(response.value(forHTTPHeaderField: "Retry-After")))
        }
        return try QuotaResult.decode(data)
    }
    public static func retryDelay(_ value: String?, now: Date = Date()) -> TimeInterval {
        guard let value else { return 0 }
        if let seconds = Double(value), seconds.isFinite { return max(0, seconds) }
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.timeZone = TimeZone(secondsFromGMT: 0)
        format.dateFormat = "EEE, dd MMM yyyy HH:mm:ss z"
        return max(0, format.date(from: value)?.timeIntervalSince(now) ?? 0)
    }
}
