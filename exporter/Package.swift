// swift-tools-version: 6.0
import PackageDescription

// SwiftPM project for the OpenBudget upload helper. Builds a single executable that
// drives sqlcipher in-process (via the sqlcipher.swift binary xcframework) and uploads the
// decrypted SQLite to a configured budget server. build.sh wraps the binary into a .app
// bundle so it gets a TCC identity for Full Disk Access on the MoneyMoney container.
let package = Package(
    name: "OpenBudgetImporter",
    platforms: [
        .macOS(.v13)
    ],
    dependencies: [
        .package(url: "https://github.com/sqlcipher/sqlcipher.swift", from: "4.16.0")
    ],
    targets: [
        .executableTarget(
            name: "OpenBudgetImporter",
            dependencies: [
                .product(name: "SQLCipher", package: "sqlcipher.swift")
            ]
        )
    ]
)
