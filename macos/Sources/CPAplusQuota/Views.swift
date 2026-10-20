import SwiftUI
import ServiceManagement
import QuotaCore

func localTime(_ value: String?) -> String {
    guard let date = parseDate(value) else { return "未知" }
    return date.formatted(date: .abbreviated, time: .shortened)
}

struct QuotaDetail: View {
    @ObservedObject var store: QuotaStore
    let openSettings: () -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("CPAplus 额度").font(.headline)
                Spacer()
                if store.busy { ProgressView().controlSize(.small) }
                Button(action: openSettings) { Image(systemName: "gearshape") }
                    .buttonStyle(.borderless).help("设置")
            }
            if let account = store.account {
                Text(store.label(account)).font(.headline).lineLimit(2).textSelection(.enabled)
                if (store.result?.accounts.count ?? 0) > 1 {
                    Picker("主账号", selection: Binding(get: { store.account?.alias ?? "" }, set: store.select)) {
                        ForEach(store.result?.accounts ?? []) { Text(store.label($0)).tag($0.alias) }
                    }.controlSize(.small)
                }
                quotaRow("五小时剩余", window: account.fiveHour, account: account)
                quotaRow("周剩余", window: account.weekly, account: account)
                VStack(alignment: .leading, spacing: 4) {
                    Text(account.state)
                    if !account.cacheState.isEmpty { Text(account.cacheState) }
                    if store.expired && account.stale != true { Text("缓存过期 · 非实时额度") }
                    Text(parseDate(account.lastSuccessAt) == nil ? "额度尚未更新" : "额度更新于 \(localTime(account.lastSuccessAt))")
                }.font(.caption).foregroundStyle(.secondary)
            } else {
                Text(store.configured ? "暂无授权账号或额度缓存" : "请先设置 HTTPS 服务地址与 CPA API Key。")
                    .foregroundStyle(.secondary)
            }
            if !store.message.isEmpty { Text(store.message).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            HStack {
                Button("刷新额度") { store.refresh() }.disabled(!store.canRefresh)
                if Date() < store.gate.refreshAfter { Text("冷却中").font(.caption).foregroundStyle(.secondary) }
                Spacer()
                Button("退出") { NSApp.terminate(nil) }
            }.controlSize(.small)
        }
        .padding(18).frame(width: 330)
    }
    private func quotaRow(_ title: String, window: QuotaWindow?, account: QuotaAccount) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack { Text(title); Spacer(); Text(window?.percent ?? "—").monospacedDigit().fontWeight(.semibold) }
            if let value = window?.remainingPercent, value.isFinite, (0...100).contains(value) {
                ProgressView(value: value, total: 100).tint(color(value, account: account))
                    .accessibilityLabel(title).accessibilityValue(window?.percent ?? "未知")
            }
            Text("\(localTime(window?.resetAt)) 重置").font(.caption).foregroundStyle(.secondary)
        }
    }
    private func color(_ value: Double, account: QuotaAccount) -> Color {
        guard account.usable, account.supported == true, account.cacheStatus != "error" else { return .gray }
        return value > 50 ? .green : value > 20 ? .yellow : .red
    }
}

struct SettingsView: View {
    @ObservedObject var store: QuotaStore
    @State private var server = ""
    @State private var key = ""
    @State private var note = ""
    @State private var feedback = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("CPAplus 额度助手").font(.title2.bold())
            Text("macOS 0.1.0-beta · 未签名测试构建").font(.caption).foregroundStyle(.secondary)
            Form {
                TextField("HTTPS 服务根地址", text: $server).textFieldStyle(.roundedBorder)
                SecureField("CPA API Key", text: $key).textFieldStyle(.roundedBorder)
                Text("密钥仅存于本机钥匙串。同一服务留空保留密钥；更换服务必须重新输入。")
                    .font(.caption).foregroundStyle(.secondary)
                HStack {
                    Button("保存并连接") {
                        do { try store.configure(server: server, replacementKey: key); key = ""; server = store.origin; feedback = "已保存。" }
                        catch { feedback = error.localizedDescription }
                    }.keyboardShortcut(.defaultAction)
                    Button("删除当前服务密钥") {
                        do { try store.forgetKey(); key = ""; feedback = "已删除。" }
                        catch { feedback = error.localizedDescription }
                    }.disabled(!store.configured)
                }
                Divider()
                Toggle("始终显示菜单栏额度", isOn: $store.alwaysShow)
                Text("关闭时，仅在已验证身份的 Codex 桌面应用运行期间显示。不检测窗口、不需要辅助功能或屏幕录制权限。")
                    .font(.caption).foregroundStyle(.secondary)
                HStack {
                    Text("随登录启动 · \(store.loginStatus)")
                    Spacer()
                    Button("开启") { changeLogin(true) }
                    Button("关闭") { changeLogin(false) }
                }
                if store.loginStatus.contains("批准") {
                    Button("打开系统登录项设置") { SMAppService.openSystemSettingsLoginItems() }
                }
                Divider()
                if let account = store.account {
                    Picker("主显示账号", selection: Binding(get: { account.alias }, set: store.select)) {
                        ForEach(store.result?.accounts ?? []) { Text(store.label($0)).tag($0.alias) }
                    }
                    TextField("当前账号本地备注（最多40字）", text: $note).textFieldStyle(.roundedBorder)
                    Button("保存备注") { store.saveNote(note); feedback = "备注已保存。" }
                }
            }
            Text(feedback.isEmpty ? store.message : feedback).font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Text("每15秒读取服务端缓存；仅手动刷新请求服务端查询。额度更新于表示真实上游成功时间。菜单栏隐藏后，可从 Finder 再次打开本 App 进入设置。")
                .font(.caption).foregroundStyle(.secondary)
            HStack { Spacer(); Button("退出程序") { NSApp.terminate(nil) } }
        }
        .padding(24).frame(width: 500, height: 600)
        .onAppear { server = store.origin; note = store.account.flatMap { store.notes[$0.alias] } ?? "" }
        .onChange(of: store.selected) { _ in note = store.account.flatMap { store.notes[$0.alias] } ?? "" }
    }
    private func changeLogin(_ value: Bool) {
        do { try store.setLogin(value); feedback = "登录项状态：\(store.loginStatus)" }
        catch { feedback = "登录项设置失败，请将 App 移入应用程序目录后，在系统设置中检查登录项。" }
    }
}
