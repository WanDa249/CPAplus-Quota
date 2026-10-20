import AppKit
import SwiftUI
import ServiceManagement
import QuotaCore

@MainActor
final class QuotaStore: ObservableObject {
    @Published var result: QuotaResult?
    @Published var message = ""
    @Published var busy = false
    @Published var connectionFailed = false
    @Published var codexRunning = false
    @Published var origin = UserDefaults.standard.string(forKey: "server") ?? ""
    @Published var alwaysShow = UserDefaults.standard.bool(forKey: "alwaysShow") {
        didSet { UserDefaults.standard.set(alwaysShow, forKey: "alwaysShow") }
    }
    @Published var selected = ""
    @Published var notes: [String: String] = [:]
    @Published var gate = RequestGate()
    private var api: QuotaAPI?
    private var key: String?
    private var generation = UUID()
    private var operation: Task<Void, Never>?
    private var receivedAt = Date()
    private var serverAt = Date()
    private let keychain = Keychain()

    var account: QuotaAccount? { result?.accounts.first { $0.alias == selected } ?? result?.accounts.first }
    var configured: Bool { api != nil && key != nil }
    var shouldShow: Bool { !configured || alwaysShow || codexRunning }
    var canRefresh: Bool { configured && !busy && account?.supported == true && Date() >= gate.refreshAfter }
    var serverNow: Date { serverAt.addingTimeInterval(Date().timeIntervalSince(receivedAt)) }
    var expired: Bool { account?.expired(at: serverNow, staleAfter: result?.staleAfterSeconds) ?? false }
    var title: String {
        let title = account?.menuTitle(expired: expired) ?? (configured ? "CPA · —" : "CPA · 设置")
        return connectionFailed && !title.hasSuffix(" !") ? title + " !" : title
    }
    var loginStatus: String {
        switch SMAppService.mainApp.status {
        case .enabled: return "已开启"
        case .requiresApproval: return "等待系统批准，请在系统设置中允许"
        case .notFound: return "不可用，请先将 App 移入应用程序目录"
        default: return "未开启"
        }
    }

    init() {
        loadPreferences()
        guard !origin.isEmpty else { return }
        do {
            key = try keychain.read(origin: origin)
            if key != nil { api = try QuotaAPI(origin: origin) }
        } catch { message = error.localizedDescription }
    }
    func loadPreferences() {
        selected = UserDefaults.standard.string(forKey: "primary:" + origin) ?? ""
        notes = UserDefaults.standard.dictionary(forKey: "notes:" + origin) as? [String: String] ?? [:]
    }
    func label(_ account: QuotaAccount) -> String {
        if let note = notes[account.alias], !note.isEmpty { return note }
        if let name = account.displayName, !name.trimmingCharacters(in: .whitespaces).isEmpty { return name }
        return "账号 · " + account.alias.replacingOccurrences(of: "账号 ", with: "").prefix(6)
    }
    func select(_ alias: String) {
        selected = alias
        UserDefaults.standard.set(alias, forKey: "primary:" + origin)
    }
    func saveNote(_ note: String) {
        guard let account else { return }
        let value = String(note.trimmingCharacters(in: .whitespacesAndNewlines).prefix(40))
        notes[account.alias] = value.isEmpty ? nil : value
        UserDefaults.standard.set(notes, forKey: "notes:" + origin)
    }
    func configure(server: String, replacementKey: String) throws {
        let normalized = try Endpoint.normalize(server)
        // Changing servers never reuses the old server's secret.
        let nextKey: String
        if replacementKey.isEmpty && normalized == origin, let key { nextKey = key }
        else {
            guard Endpoint.validKey(replacementKey) else { throw APIError.invalidKey }
            nextKey = replacementKey
        }
        let nextAPI = try QuotaAPI(origin: normalized)
        try keychain.save(nextKey, origin: normalized)
        operation?.cancel()
        generation = UUID()
        result = nil
        busy = false
        connectionFailed = false
        gate = RequestGate()
        origin = normalized
        key = nextKey
        api = nextAPI
        message = ""
        UserDefaults.standard.set(normalized, forKey: "server")
        loadPreferences()
        read(force: true)
    }
    func forgetKey() throws {
        try keychain.delete(origin: origin)
        operation?.cancel()
        generation = UUID()
        key = nil
        api = nil
        result = nil
        busy = false
        connectionFailed = false
        message = "已删除此服务的 Keychain 密钥。"
    }
    func setLogin(_ enabled: Bool) throws {
        if enabled { try SMAppService.mainApp.register() }
        else { try SMAppService.mainApp.unregister() }
        objectWillChange.send()
    }
    func read(force: Bool = false) {
        guard !busy, Date() >= gate.readAfter, force || shouldShow else { return }
        request(refresh: false)
    }
    func refresh() {
        guard canRefresh else { return }
        gate.beginRefresh(now: Date())
        request(refresh: true)
    }
    private func request(refresh: Bool) {
        guard let api, let key else { return }
        let version = generation
        let alias = account?.alias
        busy = true
        operation = Task { [weak self] in
            do {
                let data: QuotaResult
                if refresh, let alias { data = try await api.refresh(key: key, alias: alias) }
                else { data = try await api.read(key: key) }
                guard let self, !Task.isCancelled, version == self.generation else { return }
                self.result = data
                self.connectionFailed = false
                self.receivedAt = Date()
                self.serverAt = parseDate(data.serverTime) ?? self.receivedAt
                self.message = refresh ? data.refreshFeedback : ""
                self.gate.success()
                if data.refreshStatus == "deferred" || data.refreshStatus == "error" {
                    self.gate.failure(APIError.http(429, 60), now: Date())
                }
                if !data.accounts.contains(where: { $0.alias == self.selected }), let first = data.accounts.first {
                    self.select(first.alias)
                }
            } catch {
                guard let self, !Task.isCancelled, version == self.generation else { return }
                // Never show raw server bodies, URLs, or credential-bearing transport errors.
                self.message = (error as? APIError)?.localizedDescription ?? "连接失败，请检查网络、HTTPS 证书与服务地址；显示上次结果。"
                self.connectionFailed = true
                if let apiError = error as? APIError, apiError == .unauthorized || apiError == .forbidden { self.result = nil }
                self.gate.failure(error, now: Date())
            }
            guard let self, version == self.generation else { return }
            self.busy = false
        }
    }
}
