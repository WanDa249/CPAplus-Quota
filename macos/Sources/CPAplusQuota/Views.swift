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
                ProgressView().controlSize(.small).frame(width: 16, height: 16)
                    .opacity(store.acting ? 1 : 0).accessibilityHidden(!store.acting)
                Button(action: openSettings) { Image(systemName: "gearshape") }
                    .buttonStyle(.borderless).help("设置")
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    if let account = store.account {
                        Text(store.label(account)).font(.headline).lineLimit(2).textSelection(.enabled)
                        if (store.result?.accounts.count ?? 0) > 1 {
                            Picker("查看账号", selection: Binding(get: { store.account?.alias ?? "" }, set: store.select)) {
                                ForEach(store.result?.accounts ?? []) { Text(store.label($0)).tag($0.alias) }
                            }.controlSize(.small)
                        }
                        if store.result?.selectionSupported == true {
                            VStack(alignment: .leading, spacing: 6) {
                                Text(store.selectionText).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                                HStack {
                                    Button(store.activity == .selecting ? "切换中…" : "优先使用并主显示") { store.preferAccount(account.alias) }.disabled(!store.canPreferAccount)
                                        .help("更改当前 API Key 的优先调用账号；同一 Key 的其他设备也会看到此选择。临时失败仍可在授权池内回退。")
                                    Button("恢复自动排序") { store.preferAccount(nil) }
                                        .disabled(!store.canChangeSelection || store.result?.selection?.mode != "manual")
                                }.controlSize(.small)
                            }
                        }
                        if let window = account.fiveHour { quotaRow("五小时剩余", window: window, account: account) }
                        if let window = account.weekly { quotaRow("周剩余", window: window, account: account) }
                        if account.fiveHour == nil && account.weekly == nil {
                            Text("额度待更新").foregroundStyle(.secondary)
                        }
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
                }.frame(maxWidth: .infinity, alignment: .leading)
            }
            if !store.message.isEmpty { Text(store.message).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            HStack {
                Button(store.activity == .refreshing ? "查询中…" : "刷新额度") { store.refresh() }.disabled(!store.canRefresh)
                if Date() < store.gate.refreshAfter { Text("冷却中").font(.caption).foregroundStyle(.secondary) }
                Spacer()
                Button("退出") { NSApp.terminate(nil) }
            }.controlSize(.small)
        }
        // Stable viewport keeps controls in place during cache updates; AppKit anchors it to the menu bar.
        .padding(18).frame(width: 350, height: 440)
    }
    private func quotaRow(_ title: String, window: QuotaWindow, account: QuotaAccount) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack { Text(title); Spacer(); Text(window.percent).monospacedDigit().fontWeight(.semibold) }
            if let value = window.remainingPercent, value.isFinite, (0...100).contains(value) {
                ProgressView(value: value, total: 100).tint(color(value, account: account))
                    .accessibilityLabel(title).accessibilityValue(window.percent)
            }
            Text("\(localTime(window.resetAt)) 重置").font(.caption).foregroundStyle(.secondary)
        }
    }
    private func color(_ value: Double, account: QuotaAccount) -> Color {
        guard account.usable, account.supported == true, account.cacheStatus != "error" else { return .gray }
        return value > 50 ? .green : value > 20 ? .yellow : .red
    }
}

struct SettingsView: View {
    @ObservedObject var store: QuotaStore
    let closeSettings: () -> Void
    @State private var server = ""
    @State private var key = ""
    @State private var note = ""
    @State private var noteAlias = ""
    @State private var noteOrigin = ""
    @State private var noteName = ""
    @State private var feedback = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("CPAplus 额度助手").font(.title2.bold())
            Text("macOS 0.1.2-beta · 未签名测试构建").font(.caption).foregroundStyle(.secondary)
            ScrollView {
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
                        Picker("主显示账号", selection: Binding(get: { account.alias }, set: { store.select($0); editNote($0) })) {
                            ForEach(store.result?.accounts ?? []) { Text(store.label($0)).tag($0.alias) }
                        }
                        Text("此选择仅切换显示。优先调用及恢复自动排序请在菜单栏详情中操作。")
                            .font(.caption).foregroundStyle(.secondary)
                        Text("备注对象：" + noteName).font(.caption).lineLimit(2)
                        if noteAlias != account.alias {
                            Button("改为编辑当前显示账号") { editNote(account.alias) }.controlSize(.small)
                        }
                        TextField("本地备注（最多40字）", text: $note).textFieldStyle(.roundedBorder).onSubmit(saveNote)
                        Button("保存备注", action: saveNote)
                            .disabled(!store.canSaveNote(alias: noteAlias, origin: noteOrigin))
                    }
                }.frame(maxWidth: .infinity, alignment: .leading)
            }
            Text(feedback.isEmpty ? store.message : feedback).font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Text("每15秒读取服务端缓存；仅手动刷新请求服务端查询。额度更新于表示真实上游成功时间。菜单栏隐藏后，可从 Finder 再次打开本 App 进入设置。")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("关闭", action: closeSettings).keyboardShortcut(.cancelAction)
                Spacer()
                Button("退出程序") { NSApp.terminate(nil) }
            }
        }
        .padding(24).frame(width: 500, height: 600)
        .onAppear { server = store.origin; editNote(store.account?.alias ?? "") }
        .onChange(of: store.origin) { _ in editNote(store.account?.alias ?? "") }
        .onChange(of: store.account?.alias) { alias in
            // Initial connection can finish after settings open. Later server changes must not retarget a draft.
            if noteAlias.isEmpty, let alias { editNote(alias) }
        }
    }
    private func editNote(_ alias: String) {
        noteAlias = alias
        noteOrigin = store.origin
        noteName = store.result?.accounts.first(where: { $0.alias == alias }).map(store.label) ?? "待连接"
        note = store.notes[alias] ?? ""
    }
    private func saveNote() {
        feedback = store.saveNote(note, alias: noteAlias, origin: noteOrigin) ? "备注已保存。" : "备注对象已不可用，请重新选择账号。"
    }
    private func changeLogin(_ value: Bool) {
        do { try store.setLogin(value); feedback = "登录项状态：\(store.loginStatus)" }
        catch { feedback = "登录项设置失败，请将 App 移入应用程序目录后，在系统设置中检查登录项。" }
    }
}
