// Tiny Mach-O launcher so that TCC associates the running process with the
// surrounding OpenBudgetImporter.app bundle (which holds the FDA grant) rather
// than with /bin/bash — which is what happens when a shell script is the
// bundle's main executable.
//
// Secrets aren't baked in: at run time we read OPENBUDGET_UPLOAD_URL / OPENBUDGET_UPLOAD_TOKEN
// from a .env file two directories above the .app bundle (the repo root).

import Foundation

let bundleURL = Bundle.main.bundleURL                // .../launchd/OpenBudgetImporter.app
let repoRoot = bundleURL.deletingLastPathComponent() // .../launchd
    .deletingLastPathComponent()                     // .../  (repo root)
let envURL = repoRoot.appendingPathComponent(".env")
let scriptURL = repoRoot.appendingPathComponent("export-for-web.sh")

func loadDotEnv(_ url: URL) -> [String: String] {
    guard let contents = try? String(contentsOf: url, encoding: .utf8) else { return [:] }
    var result: [String: String] = [:]
    for rawLine in contents.split(whereSeparator: { $0 == "\n" || $0 == "\r" }) {
        let line = rawLine.trimmingCharacters(in: .whitespaces)
        if line.isEmpty || line.hasPrefix("#") { continue }
        guard let eq = line.firstIndex(of: "=") else { continue }
        let key = String(line[..<eq]).trimmingCharacters(in: .whitespaces)
        var value = String(line[line.index(after: eq)...])
        if (value.hasPrefix("\"") && value.hasSuffix("\""))
            || (value.hasPrefix("'") && value.hasSuffix("'")) {
            value = String(value.dropFirst().dropLast())
        }
        result[key] = value
    }
    return result
}

var env = ProcessInfo.processInfo.environment
for (k, v) in loadDotEnv(envURL) {
    env[k] = v
}
env["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"

let logPath = "/tmp/openbudget-export.log"
if !FileManager.default.fileExists(atPath: logPath) {
    FileManager.default.createFile(atPath: logPath, contents: nil)
}
let log = try FileHandle(forWritingTo: URL(fileURLWithPath: logPath))
log.seekToEndOfFile()

let task = Process()
task.executableURL = scriptURL
task.environment = env
task.standardOutput = log
task.standardError = log

try task.run()
task.waitUntilExit()
exit(task.terminationStatus)
