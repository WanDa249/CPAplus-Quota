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
    private var defaults = UserDefaults.standard

    var account: QuotaAccount? { result?.accounts.first { $0.alias == selected } ?? result?.accounts.first }
    var configured: Bool { api != nil && key != nil }
    var shouldShow: Bool { !configured || alwaysShow || codexRunning }
    var canRefresh: Bool { configured && !busy && account?.supported == true && Date() >= gate.refreshAfter }
    var canChangeSelection: Bool { configured && !busy && !connectionFailed && result?.selectionSupported == true && Date() >= gate.readAfter }
    var canPreferAccount: Bool {
        canChangeSelection && account?.usable == true && account?.supported == true &&
        !(result?.selection?.mode == "manual" && result?.selection?.alias == account?.alias)
    }
    var selectionText: String {
        if connectionFailed { return "调用状态待确认，请等待重新读取。" }
        if result?.selection?.mode == "manual", let preferred = result?.accounts.first(where: { $0.alias == result?.selection?.alias }) {
            return "优先调用：" + label(preferred)
        }
        return "调用：按优先级自动排序"
    }
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
    // Isolate transport and preferences for app-state tests; never load a user's key.
    init(api: QuotaAPI, key: String, origin: String, defaults: UserDefaults) {
        self.defaults = defaults
        self.api = api
        self.key = key
        self.origin = origin
        loadPreferences()
    }
    func loadPreferences() {
        selected = defaults.string(forKey: "primary:" + origin) ?? ""
        notes = defaults.dictionary(forKey: "notes:" + origin) as? [String: String] ?? [:]
    }
    func label(_ account: QuotaAccount) -> String {
        if let note = notes[account.alias], !note.isEmpty { return note }
        if let name = account.displayName, !name.trimmingCharacters(in: .whitespaces).isEmpty { return name }
        return "账号 · " + account.alias.replacingOccurrences(of: "账号 ", with: "").prefix(6)
    }
    func select(_ alias: String) {
        selected = alias
        defaults.set(alias, forKey: "primary:" + origin)
    }
    func saveNote(_ note: String) {
        guard let account else { return }
        let value = String(note.trimmingCharacters(in: .whitespacesAndNewlines).prefix(40))
        notes[account.alias] = value.isEmpty ? nil : value
        defaults.set(notes, forKey: "notes:" + origin)
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
        defaults.set(normalized, forKey: "server")
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
    func preferAccount(_ alias: String?) {
        guard canChangeSelection else { return }
        if let alias {
            guard result?.accounts.contains(where: { $0.alias == alias && $0.usable && $0.supported == true }) == true else { return }
        }
        request(refresh: false, changeSelection: true, preferredAlias: alias)
    }
    private func request(refresh: Bool, changeSelection: Bool = false, preferredAlias: String? = nil) {
        guard let api, let key else { return }
        let version = generation
        let alias = account?.alias
        busy = true
        if changeSelection { message = "正在切换调用账号…" }
        operation = Task { [weak self] in
            do {
                let data: QuotaResult
                if changeSelection { data = try await api.selectAccount(key: key, alias: preferredAlias) }
                else if refresh, let alias { data = try await api.refresh(key: key, alias: alias) }
                else { data = try await api.read(key: key) }
                guard let self, !Task.isCancelled, version == self.generation else { return }
                let previousSelection = self.result?.selection
                self.result = data
                self.connectionFailed = false
                self.receivedAt = Date()
                self.serverAt = parseDate(data.serverTime) ?? self.receivedAt
                self.message = refresh ? data.refreshFeedback : ""
                if changeSelection {
                    let confirmed = preferredAlias == nil ? data.selection?.mode == "auto" :
                        data.selection?.mode == "manual" && data.selection?.alias == preferredAlias
                    self.message = confirmed ? (preferredAlias == nil ? "已恢复按优先级自动排序。" : "已优先使用所选账号。") :
                        "服务器未确认所选账号；已同步当前调用状态。"
                }
                self.gate.success()
                if data.refreshStatus == "deferred" || data.refreshStatus == "error" {
                    self.gate.failure(APIError.http(429, 60), now: Date())
                }
                if let selection = data.selection, selection.mode == "manual", let alias = selection.alias,
                   changeSelection || previousSelection != selection {
                    self.select(alias)
                } else if data.selection?.mode == "auto", changeSelection || previousSelection?.mode == "manual", let first = data.accounts.first {
                    self.select(first.alias)
                } else if !data.accounts.contains(where: { $0.alias == self.selected }), let first = data.accounts.first {
                    self.select(first.alias)
                }
            } catch {
                guard let self, !Task.isCancelled, version == self.generation else { return }
                // Never show raw server bodies, URLs, or credential-bearing transport errors.
                self.message = (error as? APIError)?.localizedDescription ?? "连接失败，请检查网络、HTTPS 证书与服务地址；显示上次结果。"
                let authorizationError = (error as? APIError) == .unauthorized || (error as? APIError) == .forbidden
                if changeSelection && !authorizationError { self.message = "切换未确认，请等待重新读取调用状态后再试；不会自动重发切换请求。" }
                self.connectionFailed = true
                if let apiError = error as? APIError, apiError == .unauthorized || apiError == .forbidden { self.result = nil }
                self.gate.failure(error, now: Date())
            }
            guard let self, version == self.generation else { return }
            self.busy = false
        }
    }
}
