// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "LedVisual",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .library(
            name: "LedVisualCore",
            targets: ["LedVisualCore"]
        ),
        .executable(
            name: "LedVisualApp",
            targets: ["LedVisualApp"]
        )
    ],
    targets: [
        .target(
            name: "LedVisualCore",
            linkerSettings: [
                .linkedFramework("IOKit"),
                .linkedFramework("Security"),
                .linkedFramework("CoreGraphics"),
                .linkedFramework("ImageIO"),
                .linkedFramework("UniformTypeIdentifiers")
            ]
        ),
        .executableTarget(
            name: "LedVisualApp",
            dependencies: ["LedVisualCore"],
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("SwiftUI"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("Carbon")
            ]
        ),
        .testTarget(
            name: "LedVisualCoreTests",
            dependencies: ["LedVisualCore"]
        )
    ]
)
