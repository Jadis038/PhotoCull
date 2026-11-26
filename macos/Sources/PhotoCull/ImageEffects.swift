// 影·筛 / PhotoCull — 图像小特效（已标记缩略图降饱和）
import Foundation
import CoreImage
import CoreGraphics

enum ImageEffects {
    private static let context = CIContext(options: [.useSoftwareRenderer: false])

    static func desaturated(_ image: CGImage, saturation: Double = 0.15) -> CGImage? {
        let input = CIImage(cgImage: image)
        guard let filter = CIFilter(name: "CIColorControls") else { return image }
        filter.setValue(input, forKey: kCIInputImageKey)
        filter.setValue(saturation, forKey: kCIInputSaturationKey)
        filter.setValue(-0.04, forKey: kCIInputBrightnessKey)
        guard let output = filter.outputImage,
              let cgImage = context.createCGImage(output, from: output.extent) else {
            return image
        }
        return cgImage
    }
}
