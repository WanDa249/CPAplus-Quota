import XCTest
import QuotaCore
import Security

let fixture = """
{"accounts":[{"alias":"账号 abc123","displayName":"测试主账号","supported":true,"frozen":false,"disabled":false,"available":true,"availabilityKnown":true,"cacheStatus":"fresh","stale":false,"lastSuccessAt":"2026-10-20T00:00:00Z","fiveHour":{"remainingPercent":61.5,"resetAt":"2026-10-20T05:00:00Z"},"weekly":{"remainingPercent":46,"resetAt":"2026-10-25T00:00:00Z"}}],"serverTime":"2026-10-20T00:00:10Z","staleAfterSeconds":300}
"""

final class MockProtocol: URLProtocol {
    static var handler: ((URLRequest) throws -> (Int, [String: String], Data))!
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let (status, headers, data) = try Self.handler(request)
            let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
}

final class QuotaTests: XCTestCase {
    func testContractPercentAndTrueUpdateTime() throws {
        let result = try QuotaResult.decode(Data(fixture.utf8))
        let account = try XCTUnwrap(result.accounts.first)
        XCTAssertEqual(account.fiveHour?.percent, "62%")
        XCTAssertEqual(account.menuTitle(expired: false), "5h 62% · 周 46%")
        XCTAssertEqual(account.lastSuccessAt, "2026-10-20T00:00:00Z")
        XCTAssertTrue(account.expired(at: parseDate("2026-10-20T00:05:01Z")!, staleAfter: 300))
        XCTAssertFalse(account.expired(at: parseDate("2026-10-20T00:00:15Z")!, staleAfter: 300))
        XCTAssertEqual(try QuotaResult.decode(Data(fixture.utf8)).accounts.first?.lastSuccessAt, account.lastSuccessAt)
    }
    func testUnavailableAndCacheSemantics() throws {
        for (field, state) in [("\"frozen\":false", "已冻结"), ("\"disabled\":false", "已停用")] {
            let changed = fixture.replacingOccurrences(of: field, with: field.replacingOccurrences(of: "false", with: "true"))
            let account = try XCTUnwrap(QuotaResult.decode(Data(changed.utf8)).accounts.first)
            XCTAssertFalse(account.usable)
            XCTAssertEqual(account.state, state)
            XCTAssertEqual(account.menuTitle(expired: false), "CPA · " + state)
        }
        let staleError = fixture.replacingOccurrences(of: "\"fresh\"", with: "\"error\"").replacingOccurrences(of: "\"stale\":false", with: "\"stale\":true")
        XCTAssertEqual(try QuotaResult.decode(Data(staleError.utf8)).accounts.first?.cacheState, "缓存过期 · 查询失败")
        let unknown = try QuotaResult.decode(Data("{\"accounts\":[{\"alias\":\"x\"}]}".utf8)).accounts[0]
        XCTAssertFalse(unknown.usable)
        XCTAssertEqual(unknown.state, "状态未知")
        XCTAssertNil(unknown.fiveHour)
        XCTAssertThrowsError(try QuotaResult.decode(Data("{}".utf8)))
        XCTAssertThrowsError(try QuotaResult.decode(Data("{\"accounts\":[{\"alias\":\"x\"},{\"alias\":\"x\"}]}".utf8)))
        XCTAssertEqual(try QuotaResult.decode(Data("{\"accounts\":[]}".utf8)).accounts.count, 0)
        let missing = fixture.replacingOccurrences(of: "61.5", with: "null")
        XCTAssertEqual(try QuotaResult.decode(Data(missing.utf8)).accounts.first?.fiveHour?.percent, "—")
    }
    func testEndpointAndKeyBoundaries() throws {
        XCTAssertEqual(try Endpoint.normalize(" HTTPS://Example.COM:443/ "), "https://example.com")
        for value in ["http://example.com", "https://user:key@example.com", "https://example.com/v1", "https://example.com?key=x", "https://example.com/#key"] {
            XCTAssertThrowsError(try Endpoint.normalize(value))
        }
        XCTAssertTrue(Endpoint.validKey("cpa-fake-key-only"))
        for value in ["", "a b", "a\nb", "中文", String(repeating: "x", count: 513)] { XCTAssertFalse(Endpoint.validKey(value)) }
    }
    func testBackoffAndRefreshCooldown() {
        let now = Date(timeIntervalSince1970: 1000)
        var gate = RequestGate()
        gate.beginRefresh(now: now)
        XCTAssertEqual(gate.refreshAfter.timeIntervalSince(now), 60)
        gate.failure(APIError.http(429, 180), now: now)
        XCTAssertEqual(gate.readAfter.timeIntervalSince(now), 180)
        XCTAssertEqual(gate.refreshAfter.timeIntervalSince(now), 180)
        gate.success()
        XCTAssertLessThan(gate.readAfter, now)
        gate.failure(APIError.forbidden, now: now)
        XCTAssertEqual(gate.readAfter.timeIntervalSince(now), 300)
        XCTAssertEqual(QuotaAPI.retryDelay("120", now: now), 120)
        XCTAssertEqual(QuotaAPI.retryDelay("Thu, 01 Jan 1970 00:20:00 GMT", now: now), 200)
        gate.success()
        for delay in [15.0, 30, 60, 120, 240, 300, 300] {
            gate.failure(APIError.invalidResponse, now: now)
            XCTAssertEqual(gate.readAfter.timeIntervalSince(now), delay)
        }
    }
    func testCacheReadsAndManualRefreshHeaders() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [MockProtocol.self]
        let api = try QuotaAPI(origin: "https://quota.invalid", session: URLSession(configuration: configuration))
        var methods: [String] = []
        MockProtocol.handler = { request in
            XCTAssertEqual(request.url?.scheme, "https")
            XCTAssertNil(request.url?.query)
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer cpa-fake-key-only")
            methods.append(request.httpMethod!)
            if request.httpMethod == "POST" { XCTAssertEqual(request.value(forHTTPHeaderField: "X-Quota-Account"), "abc123") }
            return (200, [:], Data(fixture.utf8))
        }
        let first = try await api.read(key: "cpa-fake-key-only")
        let second = try await api.read(key: "cpa-fake-key-only")
        _ = try await api.refresh(key: "cpa-fake-key-only", alias: "账号 abc123")
        XCTAssertEqual(methods, ["GET", "GET", "POST"])
        XCTAssertEqual(first.accounts[0].lastSuccessAt, second.accounts[0].lastSuccessAt)
        MockProtocol.handler = { request in (request.httpMethod == "POST" ? 405 : 200, [:], Data(fixture.utf8)) }
        let fallback = try await api.refresh(key: "cpa-fake-key-only", alias: "abc123")
        XCTAssertEqual(fallback.refreshStatus, "not_supported")
        for (status, expected) in [(401, APIError.unauthorized), (403, .forbidden), (302, .redirect), (429, .http(429, 90))] {
            MockProtocol.handler = { _ in (status, ["Retry-After": "90"], Data()) }
            do { _ = try await api.read(key: "cpa-fake-key-only"); XCTFail("Expected HTTP failure") }
            catch { XCTAssertEqual(error as? APIError, expected) }
        }
    }
    func testRealKeychainCRUDAndServerIsolation() throws {
        let keychain = Keychain(service: "io.github.WanDa249.CPAplusQuota.tests." + UUID().uuidString)
        let a = "https://a.invalid", b = "https://b.invalid"
        defer { try? keychain.delete(origin: a); try? keychain.delete(origin: b) }
        XCTAssertNil(try keychain.read(origin: a))
        try keychain.save("cpa-fake-a", origin: a)
        XCTAssertEqual(try keychain.read(origin: a), "cpa-fake-a")
        XCTAssertNil(try keychain.read(origin: b))
        try keychain.save("cpa-fake-b", origin: b)
        try keychain.save("cpa-fake-a-replaced", origin: a)
        XCTAssertEqual(try keychain.read(origin: a), "cpa-fake-a-replaced")
        XCTAssertEqual(try keychain.read(origin: b), "cpa-fake-b")
        try keychain.delete(origin: a)
        XCTAssertNil(try keychain.read(origin: a))
        XCTAssertThrowsError(try keychain.save("bad key", origin: b))
    }
    func testCodexIdentityRejectsUnrelatedApps() {
        XCTAssertFalse(CodexIdentity.verifiedBundle(at: URL(fileURLWithPath: "/System/Applications/TextEdit.app")))
        XCTAssertFalse(CodexIdentity.verifiedBundle(at: URL(fileURLWithPath: "/tmp/CPAplus-no-such-Codex.app")))
        XCTAssertEqual(CodexIdentity.bundleID, "com.openai.codex")
        XCTAssertEqual(CodexIdentity.teamID, "2DC432GLL2")
    }
}

// Trust only this run's generated test root, only inside this test delegate.
final class FixtureTrust: NSObject, URLSessionTaskDelegate {
    let certificate: SecCertificate
    init(certificate: SecCertificate) { self.certificate = certificate }
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust else { completionHandler(.performDefaultHandling, nil); return }
        SecTrustSetAnchorCertificates(trust, [certificate] as CFArray)
        SecTrustSetAnchorCertificatesOnly(trust, true)
        if SecTrustEvaluateWithError(trust, nil) { completionHandler(.useCredential, URLCredential(trust: trust)) }
        else { completionHandler(.cancelAuthenticationChallenge, nil) }
    }
}

final class HTTPSIntegrationTests: XCTestCase {
    func testAccountSelectionOverRealTLSAndCrossDeviceReadback() async throws {
        guard let origin = ProcessInfo.processInfo.environment["QUOTA_TEST_ORIGIN"],
              let certPath = ProcessInfo.processInfo.environment["QUOTA_TEST_CERT"] else { throw XCTSkip("Run scripts/test.sh for HTTPS") }
        let cert = try XCTUnwrap(SecCertificateCreateWithData(nil, Data(contentsOf: URL(fileURLWithPath: certPath)) as CFData))
        func client() throws -> QuotaAPI {
            let delegate = FixtureTrust(certificate: cert)
            return try QuotaAPI(origin: origin, session: URLSession(configuration: .ephemeral, delegate: delegate, delegateQueue: nil))
        }
        let first = try client(), second = try client()
        let key = "cpa-fake-selection"
        let before = try await first.read(key: key)
        XCTAssertEqual(before.selection?.mode, "auto")
        let selected = try await first.selectAccount(key: key, alias: "账号 def456")
        XCTAssertEqual(selected.selection?.alias, "账号 def456")
        let observed = try await second.read(key: key)
        XCTAssertEqual(observed.selection, selected.selection)
        XCTAssertEqual(observed.accounts[0].lastSuccessAt, before.accounts[0].lastSuccessAt)
        do { _ = try await first.selectAccount(key: key, alias: "outside-pool"); XCTFail("Unauthorized alias") }
        catch { XCTAssertEqual(error as? APIError, .http(404, 0)) }
        let stillSelected = try await second.read(key: key)
        XCTAssertEqual(stillSelected.selection?.alias, "账号 def456")
        _ = try await second.selectAccount(key: key, alias: nil)
        let reset = try await first.read(key: key)
        XCTAssertEqual(reset.selection?.mode, "auto")
        XCTAssertEqual(reset.accounts[0].lastSuccessAt, before.accounts[0].lastSuccessAt)
    }
    func testRealTLSCacheRefreshAuthAndRedirect() async throws {
        guard let origin = ProcessInfo.processInfo.environment["QUOTA_TEST_ORIGIN"],
              let certPath = ProcessInfo.processInfo.environment["QUOTA_TEST_CERT"] else {
            throw XCTSkip("Run with scripts/test.sh to include local HTTPS integration")
        }
        let cert = try XCTUnwrap(SecCertificateCreateWithData(nil, Data(contentsOf: URL(fileURLWithPath: certPath)) as CFData))
        let configuration = URLSessionConfiguration.ephemeral
        let delegate = FixtureTrust(certificate: cert)
        let api = try QuotaAPI(origin: origin, session: URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil))
        let cached = try await api.read(key: "cpa-fake-key-only")
        let again = try await api.read(key: "cpa-fake-key-only")
        XCTAssertEqual(cached.accounts[0].lastSuccessAt, again.accounts[0].lastSuccessAt)
        let refreshed = try await api.refresh(key: "cpa-fake-key-only", alias: "账号 abc123")
        XCTAssertEqual(refreshed.refreshStatus, "success")
        XCTAssertNotEqual(refreshed.accounts[0].lastSuccessAt, cached.accounts[0].lastSuccessAt)
        for (key, expected) in [("cpa-fake-unauthorized", APIError.unauthorized), ("cpa-fake-forbidden", .forbidden), ("cpa-fake-redirect", .redirect), ("cpa-fake-limited", .http(429, 120))] {
            do { _ = try await api.read(key: key); XCTFail("Expected failure") }
            catch { XCTAssertEqual(error as? APIError, expected) }
        }
        // Production transport must reject this self-signed certificate.
        let production = try QuotaAPI(origin: origin)
        do { _ = try await production.read(key: "cpa-fake-key-only"); XCTFail("Untrusted TLS must fail") }
        catch { XCTAssertFalse(error is APIError) }
    }
}
