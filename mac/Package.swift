// swift-tools-version:5.9
// DictaMute for macOS (experimental). Built on GitHub Actions: see .github/workflows/mac.yml.
import PackageDescription

let package = Package(
    name: "DictaMute",
    platforms: [.macOS(.v14)], // per-process microphone state needs macOS 14 Core Audio
    targets: [
        .executableTarget(name: "DictaMute", path: "Sources/DictaMute")
    ]
)
