// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "GameTranslator",
    platforms: [.macOS("15.0")],
    dependencies: [
        .package(path: "Packages/TranslatorCore"),
    ],
    targets: [
        .executableTarget(
            name: "GameTranslator",
            dependencies: [.product(name: "TranslatorCore", package: "TranslatorCore")],
            path: "App"
        ),
    ]
)
