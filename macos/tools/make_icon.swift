#!/usr/bin/env swift
// Sinh Resources/AppIcon.icns: nền gradient + chữ "GT". Dùng: swift tools/make_icon.swift Resources/AppIcon.icns
import AppKit

let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Resources/AppIcon.icns"
let iconset = NSTemporaryDirectory() + "GameTranslator.iconset"
try? FileManager.default.removeItem(atPath: iconset)
try FileManager.default.createDirectory(atPath: iconset, withIntermediateDirectories: true)

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = ctx
    let s = CGFloat(px)
    let rect = NSRect(x: s * 0.06, y: s * 0.06, width: s * 0.88, height: s * 0.88)
    let path = NSBezierPath(roundedRect: rect, xRadius: s * 0.2, yRadius: s * 0.2)
    NSGradient(starting: NSColor(red: 0.20, green: 0.45, blue: 0.95, alpha: 1),
               ending: NSColor(red: 0.45, green: 0.15, blue: 0.75, alpha: 1))!.draw(in: path, angle: -90)
    let font = NSFont.systemFont(ofSize: s * 0.42, weight: .heavy)
    let str = NSAttributedString(string: "GT", attributes: [.font: font, .foregroundColor: NSColor.white])
    let size = str.size()
    str.draw(at: NSPoint(x: (s - size.width) / 2, y: (s - size.height) / 2 + s * 0.04))
    let bar = NSRect(x: s * 0.24, y: s * 0.20, width: s * 0.52, height: s * 0.07)
    NSColor(white: 1, alpha: 0.85).setFill()
    NSBezierPath(roundedRect: bar, xRadius: s * 0.035, yRadius: s * 0.035).fill()
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for point in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let suffix = scale == 2 ? "@2x" : ""
        try render(point * scale).write(to: URL(fileURLWithPath: "\(iconset)/icon_\(point)x\(point)\(suffix).png"))
    }
}

let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", iconset, "-o", output]
try p.run()
p.waitUntilExit()
exit(p.terminationStatus)
