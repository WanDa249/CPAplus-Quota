import XCTest
import SwiftUI
import QuotaCore
@testable import CPAplusQuota

// Hold responses without blocking URLSession's callback queue. All fixture state stays on the main actor.
final class HeldProtocol: URLProtocol {
    @MainActor static var handler: ((HeldProtocol) -> Void)?
    @MainActor private(set) var stopped = false
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { Task { @MainActor in Self.handler?(self) } }
    override func stopLoading() { Task { @MainActor in self.stopped = true } }
    @MainActor func finish(_ code: Int = 200, data: Data = selectionFixture()) {
        let response = HTTPURLResponse(url: request.url!, statusCode: code, httpVersion: "HTTP/1.1", headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }
}

final class InteractionTests: XCTestCase {
    func testOnlyExistingQuotaWindowsAppearInMenu() throws {
        let prefix = "{\"accounts\":[{\"alias\":\"a\",\"supported\":true,\"available\":true,\"availabilityKnown\":true"
        for (windows, title) in [
            (",\"weekly\":{\"remainingPercent\":46}", "周 46%"),
            (",\"fiveHour\":{\"remainingPercent\":61.5}", "5h 62%"),
            (",\"weekly\":{}", "周 —"),
            ("", "额度待更新")
        ] {
            let account = try QuotaResult.decode(Data((prefix + windows + "}]}").utf8)).accounts[0]
            XCTAssertEqual(account.menuTitle(expired: false), title)
            XCTAssertEqual(account.menuTitle(expired: true), title + " !")
        }
    }

    @MainActor
    func testRefreshPreemptsCacheReadAndProtectsActiveOperation() async throws {
        let (store, defaults, suite) = try makeStore()
        defer { defaults.removePersistentDomain(forName: suite); HeldProtocol.handler = nil }
        var methods: [String] = []
        var held: HeldProtocol?
        HeldProtocol.handler = { request in
            methods.append(request.request.httpMethod!)
            if methods.count == 1 { request.finish() }
            else { held = request }
        }
        store.read(force: true)
        await wait { !store.busy }
        let successAt = store.account?.lastSuccessAt
        store.read(force: true)
        await wait { methods.count == 2 }
        let passive = try XCTUnwrap(held)
        XCTAssertEqual(store.activity, .reading)
        XCTAssertTrue(store.canRefresh)
        XCTAssertTrue(store.canChangeSelection)
        store.refresh()
        XCTAssertEqual(store.activity, .refreshing)
        await wait { methods.count == 3 && passive.stopped }
        store.read(force: true)
        store.refresh()
        store.preferAccount("账号 def456")
        XCTAssertEqual(methods, ["GET", "GET", "POST"])
        XCTAssertEqual(store.account?.alias, "账号 abc123")
        XCTAssertTrue(store.busy, "Cancelled GET must not clear the active POST state")
        held?.finish(503)
        await wait { !store.busy }
        XCTAssertEqual(store.account?.lastSuccessAt, successAt)
        XCTAssertTrue(store.connectionFailed)
        XCTAssertTrue(store.title.hasSuffix(" !"))
        XCTAssertFalse(store.canRefresh, "Manual cooldown/backoff survives preemption")
        store.gate.success()
        store.read(force: true)
        await wait { methods.count == 4 }
        held?.finish()
        await wait { !store.busy }
        XCTAssertFalse(store.connectionFailed)
        XCTAssertEqual(store.account?.lastSuccessAt, successAt, "GET is not a real quota update")
    }

    @MainActor
    func testSelectionPreemptsCacheReadAndKeepsServerConfirmation() async throws {
        let (store, defaults, suite) = try makeStore()
        defer { defaults.removePersistentDomain(forName: suite); HeldProtocol.handler = nil }
        var methods: [String] = []
        var held: HeldProtocol?
        HeldProtocol.handler = { request in
            methods.append(request.request.httpMethod!)
            if methods.count == 1 { request.finish() }
            else { held = request }
        }
        store.read(force: true)
        await wait { !store.busy }
        store.read(force: true)
        await wait { methods.count == 2 }
        let passive = try XCTUnwrap(held)
        store.preferAccount("账号 def456")
        XCTAssertEqual(store.activity, .selecting)
        XCTAssertEqual(store.account?.alias, "账号 abc123")
        await wait { methods.count == 3 && passive.stopped }
        XCTAssertTrue(store.busy)
        store.preferAccount(nil)
        store.refresh()
        store.read(force: true)
        held?.finish(data: selectionFixture(mode: "manual", alias: "账号 def456"))
        await wait { !store.busy }
        XCTAssertEqual(methods, ["GET", "GET", "PUT"])
        XCTAssertEqual(store.account?.alias, "账号 def456")
        XCTAssertEqual(store.result?.selection?.alias, "账号 def456")
    }

    @MainActor
    func testNoteTargetStaysBoundToAccountAndService() async throws {
        let (store, defaults, suite) = try makeStore()
        defer { defaults.removePersistentDomain(forName: suite); HeldProtocol.handler = nil }
        HeldProtocol.handler = { $0.finish() }
        store.read(force: true)
        await wait { !store.busy }
        let editingAlias = try XCTUnwrap(store.account?.alias), editingOrigin = store.origin
        // Background read from another device changes the main display while a draft is being edited.
        HeldProtocol.handler = { $0.finish(data: selectionFixture(mode: "manual", alias: "账号 def456")) }
        store.read(force: true)
        await wait { !store.busy }
        XCTAssertEqual(store.account?.alias, "账号 def456")
        XCTAssertTrue(store.saveNote(" 原账号备注 ", alias: editingAlias, origin: editingOrigin))
        XCTAssertEqual(store.notes[editingAlias], "原账号备注")
        XCTAssertNil(store.notes["账号 def456"])
        XCTAssertFalse(store.saveNote("错误服务", alias: editingAlias, origin: "https://other.invalid"))
        HeldProtocol.handler = { $0.finish(data: Data("{\"accounts\":[]}".utf8)) }
        store.read(force: true)
        await wait { !store.busy }
        XCTAssertFalse(store.canSaveNote(alias: editingAlias, origin: editingOrigin))
        XCTAssertFalse(store.saveNote("账号已解绑", alias: editingAlias, origin: editingOrigin))
        XCTAssertEqual(store.notes[editingAlias], "原账号备注")
    }

    @MainActor
    func testAuthorizationErrorsClearRetainedData() async throws {
        for code in [401, 403] {
            let (store, defaults, suite) = try makeStore()
            HeldProtocol.handler = { $0.finish() }
            store.read(force: true)
            await wait { !store.busy }
            XCTAssertNotNil(store.result)
            HeldProtocol.handler = { $0.finish(code) }
            store.read(force: true)
            await wait { !store.busy }
            XCTAssertNil(store.result)
            XCTAssertTrue(store.connectionFailed)
            XCTAssertFalse(store.canChangeSelection)
            defaults.removePersistentDomain(forName: suite)
        }
        HeldProtocol.handler = nil
    }

    @MainActor
    func testDetailViewportDoesNotResizeDuringReadOrAccountChanges() async throws {
        let (store, defaults, suite) = try makeStore()
        defer { defaults.removePersistentDomain(forName: suite); HeldProtocol.handler = nil }
        let host = NSHostingController(rootView: QuotaDetail(store: store, openSettings: {}))
        let initial = host.view.fittingSize
        XCTAssertEqual(initial.width, 350, accuracy: 1)
        XCTAssertEqual(initial.height, 440, accuracy: 1)
        var held: HeldProtocol?
        HeldProtocol.handler = { held = $0 }
        store.read(force: true)
        await wait { held != nil }
        XCTAssertEqual(host.view.fittingSize, initial)
        held?.finish()
        await wait { !store.busy }
        store.select("账号 def456")
        host.view.layoutSubtreeIfNeeded()
        XCTAssertEqual(host.view.fittingSize, initial)
    }

    @MainActor private func makeStore() throws -> (QuotaStore, UserDefaults, String) {
        let suite = "CPAplusQuota.interaction.tests." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [HeldProtocol.self]
        let api = try QuotaAPI(origin: "https://quota.invalid", session: URLSession(configuration: configuration))
        return (QuotaStore(api: api, key: "cpa-fake-key-only", origin: "https://quota.invalid", defaults: defaults), defaults, suite)
    }
    @MainActor private func wait(_ condition: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<200 {
            if condition() { return }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("Interaction did not settle", file: file, line: line)
    }
}
