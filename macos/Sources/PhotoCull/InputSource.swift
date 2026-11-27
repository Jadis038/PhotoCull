// 影·筛 / PhotoCull — 启动时切到英文输入源（最小实现）
import Foundation
import Carbon

enum InputSourceHelper {
    /// 尽量把当前输入源切到 ABC（英文）。失败静默，不影响启动。
    static func selectEnglishInputSource() {
        guard let rawList = TISCreateInputSourceList(nil, false)?.takeRetainedValue() else { return }
        let list = rawList as NSArray
        for element in list {
            let source = element as! TISInputSource
            guard let property = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { continue }
            let identifier = Unmanaged<CFString>.fromOpaque(property).takeUnretainedValue() as String
            if identifier == "com.apple.keylayout.ABC" || identifier == "com.apple.keylayout.US" {
                TISSelectInputSource(source)
                return
            }
        }
    }
}
