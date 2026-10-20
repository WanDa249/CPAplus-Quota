// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CPAplusQuota",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "CPAplusQuota", targets: ["CPAplusQuota"])],
    targets: [
        .target(name: "QuotaCore"),
        .executableTarget(name: "CPAplusQuota", dependencies: ["QuotaCore"]),
        .testTarget(name: "QuotaCoreTests", dependencies: ["QuotaCore"])
    ]
)
