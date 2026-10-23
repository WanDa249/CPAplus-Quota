import AppKit
import SwiftUI
import Combine
import QuotaCore

@main
struct CPAplusQuotaApp {
    @MainActor static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(delegate) { app.run() }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let store = QuotaStore()
    private var item: NSStatusItem!
    private let popover = NSPopover()
    private var settings: NSWindow?
    private var timer: Timer?
    private var changes: AnyCancellable?
    private var workspaceObservation: NSKeyValueObservation?
    // Cache signature checks for the lifetime of a running process, not every 15-second tick.
    private var identities: [pid_t: (Date?, Bool)] = [:]

    func applicationDidFinishLaunching(_ notification: Notification) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.target = self
        item.button?.action = #selector(togglePopover)
        item.button?.font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        popover.behavior = .transient
        popover.animates = false
        popover.contentViewController = NSHostingController(rootView: QuotaDetail(store: store, openSettings: { [weak self] in self?.showSettings() }))
        changes = store.objectWillChange.sink { [weak self] _ in
            DispatchQueue.main.async { self?.updateItem() }
        }
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didTerminateApplicationNotification, NSWorkspace.didWakeNotification] {
            center.addObserver(self, selector: #selector(workspaceChanged), name: name, object: nil)
        }
        workspaceObservation = NSWorkspace.shared.observe(\.runningApplications, options: [.new]) { [weak self] _, _ in
            DispatchQueue.main.async { self?.scanCodex() }
        }
        timer = Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.scanCodex()
                self?.store.read()
                self?.store.objectWillChange.send()
            }
        }
        scanCodex()
        updateItem()
        store.read()
        if !store.configured { showSettings() }
        if CommandLine.arguments.contains("--smoke-test") {
            // CI verifies launch/exit only. This is not desktop interaction acceptance.
            DispatchQueue.main.asyncAfter(deadline: .now() + 4) { NSApp.terminate(nil) }
        }
    }
    @objc private func workspaceChanged() { scanCodex() }
    private func scanCodex() {
        let candidates = NSWorkspace.shared.runningApplications.filter { $0.bundleIdentifier == CodexIdentity.bundleID && !$0.isTerminated }
        let pids = Set(candidates.map(\.processIdentifier))
        identities = identities.filter { pids.contains($0.key) }
        let running = candidates.contains { app in
            if let cached = identities[app.processIdentifier], cached.0 == app.launchDate { return cached.1 }
            let verified = CodexIdentity.verified(app)
            identities[app.processIdentifier] = (app.launchDate, verified)
            return verified
        }
        let becameVisible = running && !store.codexRunning
        if running != store.codexRunning { store.codexRunning = running }
        updateItem()
        if becameVisible { store.read() }
    }
    private func updateItem() {
        guard item != nil else { return }
        item.isVisible = store.shouldShow
        item.button?.title = store.title
        item.button?.toolTip = "CPAplus · " + (store.account.map(store.label) ?? "设置")
        item.button?.setAccessibilityLabel("CPAplus 额度，" + store.title)
        if !store.shouldShow { popover.performClose(nil) }
    }
    @objc private func togglePopover() {
        if let settings, settings.isVisible {
            NSApp.activate(ignoringOtherApps: true)
            settings.makeKeyAndOrderFront(nil)
            return
        }
        guard let button = item.button else { return }
        if popover.isShown { popover.performClose(nil) }
        else { popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY) }
    }
    private func showSettings() {
        popover.performClose(nil)
        if settings == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 500, height: 600),
                                  styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
            window.title = "CPAplus 额度助手设置"
            window.isReleasedWhenClosed = false
            window.contentViewController = NSHostingController(rootView: SettingsView(store: store, closeSettings: { [weak window] in window?.performClose(nil) }))
            window.center()
            settings = window
        }
        NSApp.activate(ignoringOtherApps: true)
        settings?.makeKeyAndOrderFront(nil)
        store.read(force: true)
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showSettings()
        return true
    }
    func applicationWillTerminate(_ notification: Notification) {
        timer?.invalidate()
        NSWorkspace.shared.notificationCenter.removeObserver(self)
        workspaceObservation?.invalidate()
    }
}
