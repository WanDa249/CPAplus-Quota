import XCTest
import QuotaCore
@testable import CPAplusQuota

func selectionFixture(mode: String? = "auto", alias: String? = nil) -> Data {
    var json = try! JSONSerialization.jsonObject(with: Data(fixture.utf8)) as! [String: Any]
    var accounts = json["accounts"] as! [[String: Any]]
    var second = accounts[0]
    second["alias"] = "账号 def456"
    second["displayName"] = "测试账号二"
    accounts.append(second)
    json["accounts"] = accounts
    if let mode {
        var selection = ["mode": mode]
        selection["alias"] = alias
        json["selection"] = selection
    }
    return try! JSONSerialization.data(withJSONObject: json)
}

func mockAPI() throws -> QuotaAPI {
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [MockProtocol.self]
    return try QuotaAPI(origin: "https://quota.invalid", session: URLSession(configuration: configuration))
}

final class SelectionTests: XCTestCase {
    func testSelectionScopeAndOldServerParsing() throws {
        XCTAssertNil(try QuotaResult.decode(selectionFixture(mode: nil)).selection)
        XCTAssertFalse(try QuotaResult.decode(selectionFixture(mode: nil)).selectionSupported)
        XCTAssertTrue(try QuotaResult.decode(selectionFixture()).selectionSupported)
        XCTAssertEqual(try QuotaResult.decode(selectionFixture(mode: "manual", alias: "账号 def456")).selection?.alias, "账号 def456")
        XCTAssertThrowsError(try QuotaResult.decode(selectionFixture(mode: "manual", alias: "outside-pool")))
        XCTAssertThrowsError(try QuotaResult.decode(selectionFixture(mode: "manual")))
        XCTAssertFalse(try QuotaResult.decode(selectionFixture(mode: "future-mode")).selectionSupported)
    }
    func testExplicitPUTAndAutoResetDoNotRefreshQuota() async throws {
        let api = try mockAPI()
        var methods: [String] = []
        MockProtocol.handler = { request in
            methods.append(request.httpMethod!)
            XCTAssertEqual(request.httpMethod, "PUT")
            XCTAssertEqual(request.url?.path, "/v1/me/account-selection")
            XCTAssertNil(request.url?.query)
            XCTAssertNil(request.httpBody)
            XCTAssertEqual(request.timeoutInterval, 8)
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer cpa-fake-key-only")
            let account = request.value(forHTTPHeaderField: "X-Quota-Account")
            if let account { XCTAssertEqual(account, "def456") }
            return (200, [:], account == nil ? selectionFixture() : selectionFixture(mode: "manual", alias: "账号 def456"))
        }
        let selected = try await api.selectAccount(key: "cpa-fake-key-only", alias: "账号 def456")
        XCTAssertEqual(selected.selection?.mode, "manual")
        XCTAssertEqual(selected.accounts[0].lastSuccessAt, "2026-10-20T00:00:00Z")
        let automatic = try await api.selectAccount(key: "cpa-fake-key-only", alias: nil)
        XCTAssertEqual(automatic.selection?.mode, "auto")
        XCTAssertEqual(methods, ["PUT", "PUT"])
    }
    func testSelectionFailuresNeverFallbackToGETOrReportSuccess() async throws {
        let api = try mockAPI()
        for (code, expected) in [(401, APIError.unauthorized), (403, .forbidden), (404, .http(404, 0)),
                                 (405, .http(405, 0)), (409, .http(409, 0)), (429, .http(429, 0)), (302, .redirect)] {
            var count = 0
            MockProtocol.handler = { request in
                count += 1
                XCTAssertEqual(request.httpMethod, "PUT")
                return (code, [:], selectionFixture())
            }
            do { _ = try await api.selectAccount(key: "cpa-fake-key-only", alias: "账号 def456"); XCTFail("Must reject selection failure") }
            catch { XCTAssertEqual(error as? APIError, expected) }
            XCTAssertEqual(count, 1)
        }
        MockProtocol.handler = { _ in (200, [:], selectionFixture(mode: nil)) }
        do { _ = try await api.selectAccount(key: "cpa-fake-key-only", alias: nil); XCTFail("Missing confirmation") }
        catch { XCTAssertEqual(error as? APIError, .invalidResponse) }
        MockProtocol.handler = { _ in throw URLError(.timedOut) }
        do { _ = try await api.selectAccount(key: "cpa-fake-key-only", alias: "账号 def456"); XCTFail("Timeout must fail") }
        catch { XCTAssertEqual((error as? URLError)?.code, .timedOut) }
    }
    @MainActor
    func testAppOnlyWritesOnExplicitActionAndFollowsServerChanges() async throws {
        let suite = "CPAplusQuota.selection.tests." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        var mode = "auto"
        var alias: String?
        var putStatus = 200
        var writes = 0
        MockProtocol.handler = { request in
            if request.httpMethod == "PUT" {
                writes += 1
                if putStatus != 200 { return (putStatus, [:], Data()) }
                alias = request.value(forHTTPHeaderField: "X-Quota-Account").map { "账号 " + $0 }
                mode = alias == nil ? "auto" : "manual"
            } else { XCTAssertEqual(request.httpMethod, "GET") }
            return (200, [:], selectionFixture(mode: mode, alias: alias))
        }
        let store = QuotaStore(api: try mockAPI(), key: "cpa-fake-key-only", origin: "https://quota.invalid", defaults: defaults)
        XCTAssertEqual(writes, 0)
        store.read(force: true)
        await idle(store)
        XCTAssertEqual(writes, 0)
        XCTAssertEqual(store.account?.alias, "账号 abc123")
        store.select("账号 def456")
        store.read(force: true)
        await idle(store)
        XCTAssertEqual(writes, 0, "Local display changes and polling must not write routing preferences")
        store.select("账号 abc123")
        store.preferAccount("outside-pool")
        XCTAssertEqual(writes, 0)
        store.preferAccount("账号 def456")
        XCTAssertTrue(store.busy)
        XCTAssertEqual(store.result?.selection?.mode, "auto", "No optimistic success before server reply")
        store.preferAccount("账号 abc123") // Busy: must not submit a second write.
        await idle(store)
        XCTAssertEqual(writes, 1)
        XCTAssertEqual(store.result?.selection?.alias, "账号 def456")
        XCTAssertEqual(store.account?.alias, "账号 def456")
        XCTAssertFalse(store.canPreferAccount, "Already preferred")
        mode = "manual"; alias = "账号 abc123" // Another device selected A.
        store.read(force: true)
        await idle(store)
        XCTAssertEqual(store.account?.alias, "账号 abc123")
        XCTAssertEqual(writes, 1)
        store.preferAccount(nil)
        await idle(store)
        XCTAssertEqual(store.result?.selection?.mode, "auto")
        XCTAssertEqual(writes, 2)
        putStatus = 409
        store.preferAccount("账号 def456")
        await idle(store)
        XCTAssertEqual(writes, 3)
        XCTAssertEqual(store.account?.alias, "账号 abc123")
        XCTAssertTrue(store.connectionFailed)
        XCTAssertFalse(store.canChangeSelection)
        XCTAssertTrue(store.message.contains("切换未确认"))
        store.gate.success() // Advance past transport backoff without sleeping in the test.
        store.read(force: true)
        await idle(store)
        XCTAssertFalse(store.connectionFailed)
        XCTAssertEqual(writes, 3, "Reconciliation is GET only; never retry PUT automatically")
        XCTAssertEqual(store.result?.selection?.mode, "auto")
        // Server-side depletion/removal/restart resets manual preference to auto.
        mode = "manual"; alias = "账号 def456"
        store.read(force: true)
        await idle(store)
        XCTAssertEqual(store.account?.alias, "账号 def456")
        mode = "auto"; alias = nil
        store.read(force: true)
        await idle(store)
        XCTAssertEqual(store.account?.alias, "账号 abc123")
        XCTAssertEqual(writes, 3)
    }
    @MainActor
    func testOldServersKeepLocalDisplayAndHideRoutingActions() async throws {
        let suite = "CPAplusQuota.old-server.tests." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        var writes = 0
        MockProtocol.handler = { request in
            if request.httpMethod == "PUT" { writes += 1 }
            return (200, [:], selectionFixture(mode: nil))
        }
        let store = QuotaStore(api: try mockAPI(), key: "cpa-fake-key-only", origin: "https://quota.invalid", defaults: defaults)
        store.read(force: true)
        await idle(store)
        XCTAssertFalse(store.canChangeSelection)
        store.select("账号 def456")
        XCTAssertEqual(store.account?.alias, "账号 def456")
        store.preferAccount("账号 abc123")
        store.preferAccount(nil)
        XCTAssertEqual(writes, 0)
    }
    @MainActor
    private func idle(_ store: QuotaStore) async {
        for _ in 0..<200 {
            if !store.busy { return }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("App request did not settle")
    }
}
