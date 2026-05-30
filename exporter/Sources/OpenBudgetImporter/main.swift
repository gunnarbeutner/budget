// OpenBudgetImporter: signed launch agent + interactive config UI that exports the user's
// MoneyMoney database to a budget server.
//
// Two run modes selected by argv:
//   - no args  → SwiftUI config window (set URL/source/token, install or uninstall the
//                launchd agent, run a one-shot export).
//   - --run-once → headless export driven by launchd.
//
// Decryption runs in-process via SQLCipher.swift (binary xcframework); upload uses
// URLSession. There is no shell helper and no external sqlcipher binary in the chain — the
// whole pipeline is part of the signed .app, so the Full Disk Access grant only flows to
// code we sealed at build time.

import AppKit
import Foundation
import SQLCipher
import Security
import SwiftUI

// MARK: - Mode dispatch
//
// main.swift runs as top-level code in script mode, which lets us `await` directly. The
// headless branch awaits the export inline and exits with an explicit status; the UI branch
// hands control to NSApplication.run() and never returns.

if CommandLine.arguments.contains("--run-once") {
    LogFile.append("Starting headless export")
    do {
        try await Exporter.runOnce(logger: LogFile.append)
        LogFile.append("Export+upload succeeded")
        exit(0)
    } catch {
        LogFile.append("Export failed: \(error)")
        exit(1)
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = AppDelegate()
app.delegate = delegate
app.run()

// MARK: - AppDelegate / window

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var window: NSWindow!

    func applicationDidFinishLaunching(_ notification: Notification) {
        installMainMenu()

        let vm = ConfigViewModel()
        let content = ConfigView().environmentObject(vm)
        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 520, height: 440),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.title = "OpenBudget Export"
        let hosting = NSHostingView(rootView: content)
        window.contentView = hosting
        // Size the window to the view's natural height instead of the fixed contentRect.
        // The multi-line URLs editor makes the content taller than 440pt; without this the
        // top-anchored VStack overflows and clips the title off the top edge.
        window.setContentSize(hosting.fittingSize)
        window.center()
        window.isReleasedWhenClosed = false
        window.makeKeyAndOrderFront(nil)

        // LSUIElement starts the process as a background agent — Launch Services won't
        // activate it for us. setActivationPolicy(.regular) at startup elevates it to a
        // foreground app, but the window still has to be explicitly activated after the run
        // loop is up; otherwise it opens behind the frontmost app.
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    /// Bare Swift executables launched via NSApplication don't get the standard main menu, so
    /// Cmd-X/C/V/A reach no responder and the text fields ignore them. Install just enough of
    /// the system menu (App + Edit + Window) to route those shortcuts to the first responder.
    @MainActor private func installMainMenu() {
        let mainMenu = NSMenu()

        // Application menu (the bold leftmost item — name comes from CFBundleName).
        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(
            withTitle: "Hide OpenBudgetImporter",
            action: #selector(NSApplication.hide(_:)),
            keyEquivalent: "h")
        let hideOthers = NSMenuItem(
            title: "Hide Others",
            action: #selector(NSApplication.hideOtherApplications(_:)),
            keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(hideOthers)
        appMenu.addItem(
            withTitle: "Show All",
            action: #selector(NSApplication.unhideAllApplications(_:)),
            keyEquivalent: "")
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(
            withTitle: "Quit OpenBudgetImporter",
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q")
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        // Edit menu — actions resolve against the first responder via the responder chain, so
        // SwiftUI TextField / SecureField get Cut/Copy/Paste/Select All for free once these
        // menu items exist.
        let editMenuItem = NSMenuItem()
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(
            withTitle: "Undo",
            action: Selector(("undo:")),
            keyEquivalent: "z")
        let redo = NSMenuItem(
            title: "Redo",
            action: Selector(("redo:")),
            keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        editMenu.addItem(redo)
        editMenu.addItem(NSMenuItem.separator())
        editMenu.addItem(
            withTitle: "Cut",
            action: #selector(NSText.cut(_:)),
            keyEquivalent: "x")
        editMenu.addItem(
            withTitle: "Copy",
            action: #selector(NSText.copy(_:)),
            keyEquivalent: "c")
        editMenu.addItem(
            withTitle: "Paste",
            action: #selector(NSText.paste(_:)),
            keyEquivalent: "v")
        editMenu.addItem(
            withTitle: "Select All",
            action: #selector(NSText.selectAll(_:)),
            keyEquivalent: "a")
        editMenuItem.submenu = editMenu
        mainMenu.addItem(editMenuItem)

        // Window menu so Cmd-W closes, Cmd-M minimizes.
        let windowMenuItem = NSMenuItem()
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(
            withTitle: "Minimize",
            action: #selector(NSWindow.performMiniaturize(_:)),
            keyEquivalent: "m")
        windowMenu.addItem(
            withTitle: "Close",
            action: #selector(NSWindow.performClose(_:)),
            keyEquivalent: "w")
        windowMenuItem.submenu = windowMenu
        mainMenu.addItem(windowMenuItem)
        NSApp.windowsMenu = windowMenu

        NSApplication.shared.mainMenu = mainMenu
    }
}

// MARK: - SwiftUI config view

@MainActor
final class ConfigViewModel: ObservableObject {
    // Multi-URL: edited as newline-separated text in the UI; persisted as a string array.
    // Each non-blank line becomes one upload target on every export.
    @Published var baseURLsText: String = Config.baseURLs.joined(separator: "\n")
    @Published var sourceName: String = Config.sourceName ?? ""
    @Published var token: String = Keychain.token ?? ""
    @Published var dbPassword: String = Keychain.dbPassword ?? ""
    @Published var status: String = ""
    @Published var working: Bool = false
    @Published var isInstalled: Bool = LaunchAgent.isInstalled

    func saveAndInstall() async {
        await runWorking {
            self.status = "Saving and installing..."
            let urls = Config.parseURLs(self.baseURLsText)
            try Config.validate(baseURLs: urls, sourceName: self.sourceName, token: self.token)
            Config.save(baseURLs: urls, sourceName: self.sourceName)
            try Keychain.setToken(self.token)
            try Keychain.setDBPassword(self.dbPassword)
            try LaunchAgent.install()
            self.isInstalled = true
            self.status = "Installed launchd agent at hourly schedule. First run triggered."
        }
    }

    func runNow() async {
        await runWorking {
            self.status = "Exporting and uploading..."
            // The logger hops back to main from whatever queue Exporter happens to use. Capturing
            // self in a @Sendable closure is OK because @MainActor classes are implicitly
            // Sendable in Swift 6 (state is only mutable on main).
            let updateStatus: @Sendable (String) -> Void = { [weak self] line in
                Task { @MainActor in self?.status = line }
            }
            // Heavy work (sqlcipher decrypt, ~seconds) on a detached task so the UI thread
            // stays responsive while the spinner shows.
            try await Task.detached(priority: .userInitiated) {
                try await Exporter.runOnce(logger: updateStatus)
            }.value
            self.status = "✓ Upload succeeded."
        }
    }

    func uninstall() async {
        await runWorking {
            try LaunchAgent.uninstall()
            self.isInstalled = false
            self.status = "Uninstalled launchd agent. Credentials kept; click Save & install to redeploy."
        }
    }

    private func runWorking(_ body: () async throws -> Void) async {
        working = true
        do {
            try await body()
        } catch {
            status = "✗ \(error.localizedDescription)"
        }
        working = false
    }
}

struct ConfigView: View {
    @EnvironmentObject var vm: ConfigViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("OpenBudget Export").font(.title2).bold()
            Text("Periodically exports your MoneyMoney database to a budget server.")
                .foregroundColor(.secondary).font(.callout)

            Form {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Server base URLs")
                        .font(.callout)
                    TextEditor(text: $vm.baseURLsText)
                        .font(.system(.body, design: .monospaced))
                        .frame(minHeight: 60, maxHeight: 100)
                        .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.secondary.opacity(0.4)))
                        .help(
                            "One URL per line, e.g. https://budget.example.com. Every export uploads to each in turn.")
                }
                TextField("Source name (slug)", text: $vm.sourceName)
                    .textFieldStyle(.roundedBorder)
                    .help(
                        "Lowercase letters/digits/hyphens. The first upload with a new name registers it on the server."
                    )
                SecureField("API token", text: $vm.token)
                    .textFieldStyle(.roundedBorder)
                    .help("Matches the server's MONEYMONEY_DB_UPLOAD_TOKEN env var. Stored in macOS Keychain.")
                SecureField("MoneyMoney password", text: $vm.dbPassword)
                    .textFieldStyle(.roundedBorder)
                    .help("Leave empty if MoneyMoney has no database password set. Stored in macOS Keychain.")
            }

            HStack {
                Button(vm.isInstalled ? "Save & restart" : "Save & install") {
                    Task { await vm.saveAndInstall() }
                }
                .disabled(
                    vm.working || Config.parseURLs(vm.baseURLsText).isEmpty || vm.sourceName.isEmpty || vm.token.isEmpty
                )
                Button("Run now") { Task { await vm.runNow() } }
                    .disabled(vm.working)
                Button("Uninstall") { Task { await vm.uninstall() } }
                    .disabled(vm.working || !vm.isInstalled)
                Spacer()
                if vm.working { ProgressView().controlSize(.small) }
            }

            Divider()
            Text("Status").font(.headline)
            ScrollView {
                Text(vm.status.isEmpty ? "Ready." : vm.status)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
                    .padding(8)
                    .background(Color(NSColor.textBackgroundColor))
                    .cornerRadius(6)
            }
            .frame(minHeight: 120)

            Text("Logs: \(LogFile.path)")
                .font(.caption).foregroundColor(.secondary)
        }
        .padding(16)
        .frame(minWidth: 480, minHeight: 420)
    }
}

// MARK: - Config (UserDefaults)

enum Config {
    private static let baseURLsKey = "baseURLs"
    // Legacy single-URL key. Kept readable for one-shot migration so an existing user's saved
    // URL doesn't disappear when they upgrade to the multi-URL build.
    private static let legacyBaseURLKey = "baseURL"
    private static let sourceNameKey = "sourceName"

    /// Returns the configured URLs. Reads the multi-URL array if present; otherwise falls
    /// back to (and migrates) the legacy single-URL key.
    static var baseURLs: [String] {
        if let array = UserDefaults.standard.stringArray(forKey: baseURLsKey), !array.isEmpty {
            return array
        }
        if let legacy = UserDefaults.standard.string(forKey: legacyBaseURLKey), !legacy.isEmpty {
            return [legacy]
        }
        return []
    }

    static var sourceName: String? { UserDefaults.standard.string(forKey: sourceNameKey) }

    static func save(baseURLs: [String], sourceName: String) {
        UserDefaults.standard.set(baseURLs, forKey: baseURLsKey)
        UserDefaults.standard.set(sourceName, forKey: sourceNameKey)
        // Once the multi-URL key is populated, drop the legacy scalar so the two can't drift.
        UserDefaults.standard.removeObject(forKey: legacyBaseURLKey)
    }

    /// Split a newline-separated text blob into individual URLs, trimming whitespace and
    /// dropping empty lines. Used by the UI to convert TextEditor content to the storage
    /// shape — and to gate the "Save" button on whether at least one URL is present.
    static func parseURLs(_ text: String) -> [String] {
        text
            .split(whereSeparator: { $0.isNewline })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    /// Validate that all fields look usable before persisting. Server-side slug rules are the
    /// same as the import endpoint's: lowercase alnum + hyphens, ≤ 100 chars.
    static func validate(baseURLs: [String], sourceName: String, token: String) throws {
        if baseURLs.isEmpty {
            throw AppError.invalidConfig("At least one server base URL is required.")
        }
        for raw in baseURLs {
            guard let url = URL(string: raw), url.scheme == "https" || url.scheme == "http", url.host != nil else {
                throw AppError.invalidConfig("Base URL '\(raw)' must be http(s)://host[/path]")
            }
        }
        let slugRange = sourceName.range(of: "^[a-z0-9][a-z0-9-]{0,99}$", options: .regularExpression)
        guard slugRange != nil else {
            throw AppError.invalidConfig(
                "Source name must be a lowercase slug (a-z, 0-9, -). Start with a letter or digit.")
        }
        if token.isEmpty {
            throw AppError.invalidConfig("API token is required.")
        }
    }
}

// MARK: - Keychain helpers

enum Keychain {
    private static let service = "de.beutner.OpenBudgetImporter"
    private static let tokenAccount = "api-token"
    private static let dbPasswordAccount = "db-password"

    static var token: String? { read(account: tokenAccount) }
    static func setToken(_ token: String) throws { try write(account: tokenAccount, value: token) }

    /// MoneyMoney's SQLCipher password. nil / empty means use MoneyMoney's
    /// built-in "nopasswd" key (i.e. the user hasn't set a password).
    static var dbPassword: String? { read(account: dbPasswordAccount) }
    static func setDBPassword(_ password: String) throws {
        // Empty input means "back to the default key" — wipe the entry so a stale
        // value can't shadow the default on subsequent runs.
        if password.isEmpty {
            try delete(account: dbPasswordAccount)
        } else {
            try write(account: dbPasswordAccount, value: password)
        }
    }

    private static func read(account: String) -> String? {
        var result: AnyObject?
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        let status = SecItemCopyMatching(q as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data, let s = String(data: data, encoding: .utf8) else {
            return nil
        }
        return s
    }

    private static func write(account: String, value: String) throws {
        let data = value.data(using: .utf8)!
        let common: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        // Try update first, then add — handles both "first run" and "user changed value".
        let updateStatus = SecItemUpdate(
            common as CFDictionary,
            [kSecValueData as String: data] as CFDictionary)
        if updateStatus == errSecItemNotFound {
            var attrs = common
            attrs[kSecValueData as String] = data
            attrs[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            let addStatus = SecItemAdd(attrs as CFDictionary, nil)
            if addStatus != errSecSuccess {
                throw AppError.keychain("SecItemAdd(\(account)) failed (status \(addStatus))")
            }
        } else if updateStatus != errSecSuccess {
            throw AppError.keychain("SecItemUpdate(\(account)) failed (status \(updateStatus))")
        }
    }

    private static func delete(account: String) throws {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        let status = SecItemDelete(q as CFDictionary)
        if status != errSecSuccess && status != errSecItemNotFound {
            throw AppError.keychain("SecItemDelete(\(account)) failed (status \(status))")
        }
    }
}

// MARK: - Launchd agent installer

enum LaunchAgent {
    private static let label = "de.beutner.OpenBudgetImporter"
    private static let intervalSeconds = 3600

    static var plistPath: String {
        "\(NSHomeDirectory())/Library/LaunchAgents/\(label).plist"
    }

    static var isInstalled: Bool {
        FileManager.default.fileExists(atPath: plistPath)
    }

    static func install() throws {
        let bundlePath = Bundle.main.bundlePath
        let logPath = LogFile.path
        let plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>\(label)</string>
                <key>ProgramArguments</key>
                <array>
                    <string>/usr/bin/open</string>
                    <string>-W</string>
                    <string>-a</string>
                    <string>\(escapeXML(bundlePath))</string>
                    <string>--args</string>
                    <string>--run-once</string>
                </array>
                <key>StartInterval</key>
                <integer>\(intervalSeconds)</integer>
                <key>RunAtLoad</key>
                <true/>
                <key>StandardErrorPath</key>
                <string>\(escapeXML(logPath))</string>
                <key>StandardOutPath</key>
                <string>\(escapeXML(logPath))</string>
            </dict>
            </plist>
            """
        let dir = (plistPath as NSString).deletingLastPathComponent
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        try plist.write(toFile: plistPath, atomically: true, encoding: .utf8)
        // Bootout an existing entry (ignore failure) then bootstrap the new one. Kickstart
        // forces a run right away so the user sees the first upload immediately.
        let uid = getuid()
        let domain = "gui/\(uid)"
        _ = try? runLaunchctl(["bootout", "\(domain)/\(label)"])
        _ = try runLaunchctl(["bootstrap", domain, plistPath])
        _ = try? runLaunchctl(["kickstart", "-k", "\(domain)/\(label)"])
    }

    static func uninstall() throws {
        let uid = getuid()
        let domain = "gui/\(uid)"
        _ = try? runLaunchctl(["bootout", "\(domain)/\(label)"])
        if FileManager.default.fileExists(atPath: plistPath) {
            try FileManager.default.removeItem(atPath: plistPath)
        }
    }

    @discardableResult
    private static func runLaunchctl(_ args: [String]) throws -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = args
        try p.run()
        p.waitUntilExit()
        if p.terminationStatus != 0 {
            throw AppError.launchctl("launchctl \(args.joined(separator: " ")) exited \(p.terminationStatus)")
        }
        return p.terminationStatus
    }

    /// Minimal XML escape for the few characters that can appear in a macOS bundle path
    /// (`&`, `<`). Paths can't contain quotes meaningfully on macOS.
    private static func escapeXML(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
    }
}

// MARK: - Log file

enum LogFile {
    static let path: String = {
        "\(NSHomeDirectory())/Library/Logs/de.beutner.OpenBudgetImporter.log"
    }()

    static func append(_ message: String) {
        let line = "[\(isoNow())] \(message)\n"
        let url = URL(fileURLWithPath: path)
        let dir = url.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: path) {
            FileManager.default.createFile(atPath: path, contents: line.data(using: .utf8))
            return
        }
        if let handle = try? FileHandle(forWritingTo: url) {
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: Data(line.utf8))
            try? handle.close()
        }
    }

    private static func isoNow() -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.string(from: Date())
    }
}

// MARK: - Exporter: decrypts MoneyMoney DB in-process, uploads to budget server

enum Exporter {
    private static let moneyMoneyDBPath =
        "\(NSHomeDirectory())/Library/Containers/com.moneymoney-app.retail/Data/Library/Application Support/MoneyMoney/Database/MoneyMoney.sqlite"
    // MoneyMoney's cipher key when the user hasn't set a password. The user-supplied password
    // (from Keychain) overrides this when present; cipher compatibility is the same either way.
    private static let defaultCipherKey = "nopasswd"
    private static let cipherCompatibility = 4

    static func runOnce(logger: @escaping @Sendable (String) -> Void) async throws {
        let baseURLs = Config.baseURLs
        guard !baseURLs.isEmpty,
            let sourceName = Config.sourceName, !sourceName.isEmpty,
            let token = Keychain.token, !token.isEmpty
        else {
            throw AppError.invalidConfig(
                "Missing config — open OpenBudgetImporter.app to set base URLs, source name, and token.")
        }

        guard FileManager.default.fileExists(atPath: moneyMoneyDBPath) else {
            throw AppError.fileMissing(
                "MoneyMoney database not found at \(moneyMoneyDBPath). Is MoneyMoney installed and has it ever been opened?"
            )
        }

        let scratch = try TempDirectory()
        defer { scratch.cleanup() }

        let srcCopy = scratch.url.appendingPathComponent("source.sqlite")
        let outFile = scratch.url.appendingPathComponent("moneymoney.sqlite")

        logger("Copying MoneyMoney DB to scratch (\(scratch.url.path))")
        try copyMoneyMoneyFiles(to: srcCopy)

        let cipherKey = Keychain.dbPassword.flatMap { $0.isEmpty ? nil : $0 } ?? defaultCipherKey
        logger("Decrypting + exporting tables")
        try decryptAndExport(source: srcCopy.path, output: outFile.path, cipherKey: cipherKey)

        let attrs = try FileManager.default.attributesOfItem(atPath: outFile.path)
        let size = (attrs[.size] as? Int64) ?? 0

        // Try every configured URL even if some fail — a stale or down endpoint shouldn't
        // silently prevent the healthy ones from receiving the snapshot. Collect failures and
        // throw a combined error at the end so the launchd-driven run still surfaces them.
        var failures: [(String, Error)] = []
        for baseURL in baseURLs {
            logger("Exported \(size) bytes → uploading to \(baseURL)/api/moneymoney-db/\(sourceName)")
            do {
                try await upload(file: outFile, baseURL: baseURL, sourceName: sourceName, token: token, logger: logger)
            } catch {
                logger("✗ Upload to \(baseURL) failed: \(error.localizedDescription)")
                failures.append((baseURL, error))
            }
        }
        if !failures.isEmpty {
            let summary =
                failures
                .map { "\($0.0): \($0.1.localizedDescription)" }
                .joined(separator: "; ")
            throw AppError.upload("\(failures.count)/\(baseURLs.count) target(s) failed — \(summary)")
        }
    }

    /// MoneyMoney keeps a lock on its live DB. Copy the encrypted file + WAL/SHM sidecars so
    /// SQLCipher gets exclusive access.
    private static func copyMoneyMoneyFiles(to destURL: URL) throws {
        let src = URL(fileURLWithPath: moneyMoneyDBPath)
        try FileManager.default.copyItem(at: src, to: destURL)
        for suffix in ["-wal", "-shm"] {
            let sidecarSrc = URL(fileURLWithPath: src.path + suffix)
            if FileManager.default.fileExists(atPath: sidecarSrc.path) {
                let sidecarDest = URL(fileURLWithPath: destURL.path + suffix)
                try FileManager.default.copyItem(at: sidecarSrc, to: sidecarDest)
            }
        }
    }

    /// Drives the SQLCipher C API: open the encrypted source, apply key + compat pragma via
    /// `PRAGMA key`, ATTACH a fresh empty plain-SQLite db, CREATE … AS SELECT each table over,
    /// DETACH. Using `PRAGMA key` instead of `sqlite3_key()` keeps us on the public API —
    /// `sqlite3_key` is only exposed in the sqlcipher.swift package behind `SQLITE_HAS_CODEC`,
    /// which the binary distribution doesn't surface to consumers.
    private static func decryptAndExport(source: String, output: String, cipherKey: String) throws {
        var db: OpaquePointer?
        let openResult = sqlite3_open(source, &db)
        guard openResult == SQLITE_OK, let db else {
            sqlite3_close(db)
            throw AppError.sqlcipher("sqlite3_open(\(source)) failed: \(openResult)")
        }
        defer { sqlite3_close(db) }

        try exec(db, "PRAGMA key = \(quote(cipherKey));")
        try exec(db, "PRAGMA cipher_compatibility = \(cipherCompatibility);")
        // Touch the schema so the key + compat actually decrypt the header before we ATTACH.
        // SQLITE_NOTADB (26) on this probe almost always means the cipher key is wrong —
        // surface that with a config-pointing message rather than the raw sqlite error.
        do {
            try exec(db, "SELECT count(*) FROM sqlite_master;")
        } catch AppError.sqlcipher(let raw) where raw.contains("file is not a database") {
            let hint =
                cipherKey == defaultCipherKey
                ? "MoneyMoney password is set but no password is configured here — open OpenBudgetImporter.app and fill in the MoneyMoney password field."
                : "The configured MoneyMoney password does not match — re-open OpenBudgetImporter.app and re-enter it."
            throw AppError.sqlcipher(hint)
        }

        // MoneyMoney declares securities.ident COLLATE LOCALIZED, a collation that only exists
        // inside the MoneyMoney process. Any query against that table — including the
        // CREATE TABLE … AS SELECT copy below — fails with "no such collation sequence: LOCALIZED"
        // until we register a stand-in. The copy drops the collation on the destination table, so
        // a plain byte comparison is enough; we never sort on it here.
        let collationResult = sqlite3_create_collation(db, "LOCALIZED", SQLITE_UTF8, nil) {
            _, len1, p1, len2, p2 in
            let n = Int(min(len1, len2))
            let cmp = n > 0 ? memcmp(p1, p2, n) : 0
            if cmp != 0 { return cmp < 0 ? -1 : 1 }
            if len1 == len2 { return 0 }
            return len1 < len2 ? -1 : 1
        }
        guard collationResult == SQLITE_OK else {
            throw AppError.sqlcipher("sqlite3_create_collation(LOCALIZED) failed: \(collationResult)")
        }

        // Empty output (existing file would cause ATTACH to refuse if it has a different schema).
        try? FileManager.default.removeItem(atPath: output)
        let attachSQL = "ATTACH DATABASE \(quote(output)) AS export KEY '';"
        try exec(db, attachSQL)

        // securities + portfolio_chart back the portfolio holdings view; both are absent on very
        // old MoneyMoney schemas, but every shipping version that has depot accounts has them.
        let tables = [
            "account_groups", "category_groups", "categories", "accounts", "transactions",
            "securities", "portfolio_chart",
        ]
        for table in tables {
            try exec(db, "CREATE TABLE export.\(table) AS SELECT * FROM \(table);")
        }
        try exec(db, "DETACH DATABASE export;")
    }

    private static func exec(_ db: OpaquePointer, _ sql: String) throws {
        var err: UnsafeMutablePointer<CChar>?
        let rc = sqlite3_exec(db, sql, nil, nil, &err)
        if rc != SQLITE_OK {
            let message = err.flatMap { String(cString: $0) } ?? "(no message)"
            sqlite3_free(err)
            throw AppError.sqlcipher("sqlite3_exec failed (\(rc)): \(message). SQL: \(sql)")
        }
    }

    private static func quote(_ s: String) -> String {
        // SQLite string literals double single quotes; paths can contain spaces but not NULs.
        "'\(s.replacingOccurrences(of: "'", with: "''"))'"
    }

    private static func upload(
        file: URL, baseURL: String, sourceName: String, token: String, logger: @escaping (String) -> Void
    ) async throws {
        guard
            let url = URL(
                string:
                    "\(baseURL.trimmingCharacters(in: CharacterSet(charactersIn: "/")))/api/moneymoney-db/\(sourceName)"
            )
        else {
            throw AppError.invalidConfig("Could not assemble upload URL")
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

        let (data, response) = try await URLSession.shared.upload(for: request, fromFile: file)
        guard let http = response as? HTTPURLResponse else {
            throw AppError.upload("No HTTPURLResponse")
        }
        let body = String(data: data, encoding: .utf8) ?? ""
        if !(200..<300).contains(http.statusCode) {
            throw AppError.upload("HTTP \(http.statusCode): \(body)")
        }
        logger("Server response: \(body)")
    }
}

// MARK: - Temp dir RAII

final class TempDirectory {
    let url: URL

    init() throws {
        let parent = URL(fileURLWithPath: NSTemporaryDirectory())
        let candidate = parent.appendingPathComponent("moneymoney-export-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: candidate, withIntermediateDirectories: true)
        self.url = candidate
    }

    func cleanup() {
        try? FileManager.default.removeItem(at: url)
    }

    deinit { cleanup() }
}

// MARK: - Errors

enum AppError: LocalizedError {
    case invalidConfig(String)
    case fileMissing(String)
    case sqlcipher(String)
    case upload(String)
    case keychain(String)
    case launchctl(String)

    var errorDescription: String? {
        switch self {
        case .invalidConfig(let m): return "Config: \(m)"
        case .fileMissing(let m): return "File: \(m)"
        case .sqlcipher(let m): return "SQLCipher: \(m)"
        case .upload(let m): return "Upload: \(m)"
        case .keychain(let m): return "Keychain: \(m)"
        case .launchctl(let m): return "launchctl: \(m)"
        }
    }
}
