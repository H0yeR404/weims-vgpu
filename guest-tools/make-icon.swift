// SPDX-License-Identifier: GPL-2.0-or-later
// Draws the app's icon, the Weims mark in black on white, in the sizes an .icns file holds.
//   make-icon <folder.iconset>      then: iconutil -c icns <folder.iconset>
// The icon is drawn here and not kept as a picture, so the shape has one description: the menu bar icon in
// Sources/main.swift is the same square and the same W.
import AppKit

func picture(_ pixels: Int) -> Data {
    let image = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8, samplesPerPixel: 4,
                                 hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: image)
    let side = CGFloat(pixels)
    // The proportions of a macOS app icon: the shape takes 824 of 1024 points and the rest is left clear.
    let square = NSRect(x: 0, y: 0, width: side, height: side).insetBy(dx: side * 100 / 1024, dy: side * 100 / 1024)
    let shape = NSBezierPath(roundedRect: square, xRadius: square.width * 0.225, yRadius: square.width * 0.225)
    NSColor.white.setFill()
    shape.fill()
    // A faint edge, drawn inside the shape: without it a white square has no outline in a light window.
    NSGraphicsContext.current?.saveGraphicsState()
    shape.addClip()
    shape.lineWidth = max(square.width * 0.012, 1) * 2
    NSColor(white: 0, alpha: 0.22).setStroke()
    shape.stroke()
    NSGraphicsContext.current?.restoreGraphicsState()
    let letter = NSBezierPath()
    // Measured from the top, as in the menu bar icon; this drawing has its origin at the bottom.
    let strokes: [(x: CGFloat, y: CGFloat)] = [(0.22, 0.33), (0.35, 0.72), (0.5, 0.45), (0.65, 0.72), (0.78, 0.33)]
    for (index, point) in strokes.enumerated() {
        let at = NSPoint(x: square.minX + point.x * square.width, y: square.maxY - point.y * square.height)
        if index == 0 { letter.move(to: at) } else { letter.line(to: at) }
    }
    // Never thinner than a good pixel: at 16 pixels the line would otherwise be 0.8 of one.
    letter.lineWidth = max(square.width * 0.06, 1.3)
    letter.lineCapStyle = .round
    letter.lineJoinStyle = .round
    NSColor.black.setStroke()
    letter.stroke()
    NSGraphicsContext.restoreGraphicsState()
    return image.representation(using: .png, properties: [:])!
}

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write(Data("usage: make-icon <folder.iconset>\n".utf8))
    exit(64)
}
let folder = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
do {
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    for (points, scale) in [(16, 1), (16, 2), (32, 1), (32, 2), (128, 1), (128, 2), (256, 1), (256, 2), (512, 1), (512, 2)] {
        let name = "icon_\(points)x\(points)" + (scale == 2 ? "@2x" : "") + ".png"
        try picture(points * scale).write(to: folder.appendingPathComponent(name))
    }
} catch {
    FileHandle.standardError.write(Data("make-icon: \(error.localizedDescription)\n".utf8))
    exit(1)
}
