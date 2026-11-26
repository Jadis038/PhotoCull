// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "PhotoCull",
    platforms: [.macOS(.v12)],
    products: [
        .executable(name: "PhotoCull", targets: ["PhotoCull"])
    ],
    targets: [
        .executableTarget(
            name: "PhotoCull",
            path: "Sources/PhotoCull"
        )
    ]
)
