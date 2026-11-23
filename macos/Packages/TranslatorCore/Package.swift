// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "TranslatorCore",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "TranslatorCore", targets: ["TranslatorCore"]),
    ],
    targets: [
        .target(name: "TranslatorCore"),
        .testTarget(name: "TranslatorCoreTests", dependencies: ["TranslatorCore"]),
    ]
)
