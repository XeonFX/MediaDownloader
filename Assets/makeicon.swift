import AppKit
import CoreGraphics

// MediaDownloader icon: indigo gradient rounded square, white download arrow into a tray.
// Renders the macOS iconset, a web favicon, and monochrome menu-bar template images.

let args = CommandLine.arguments
let outDir = args.count > 1 ? args[1] : "."

func makeContext(_ size: Int) -> CGContext {
    let cs = CGColorSpace(name: CGColorSpace.sRGB)!
    return CGContext(data: nil, width: size, height: size, bitsPerComponent: 8,
                     bytesPerRow: 0, space: cs,
                     bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
}

func savePNG(_ ctx: CGContext, _ path: String) {
    let img = ctx.makeImage()!
    let rep = NSBitmapImageRep(cgImage: img)
    let data = rep.representation(using: .png, properties: [:])!
    try! data.write(to: URL(fileURLWithPath: path))
}

/// Draws the arrow + tray glyph, sized relative to a 1024 canvas.
func drawGlyph(_ c: CGContext, scale s: CGFloat, color: CGColor) {
    c.setStrokeColor(color)
    c.setLineCap(.round)
    c.setLineJoin(.round)

    // Arrow shaft (top -> down). Canvas origin is bottom-left, so flip y: y' = 1024 - y.
    c.setLineWidth(118 * s)
    c.move(to: CGPoint(x: 512 * s, y: (1024 - 268) * s))
    c.addLine(to: CGPoint(x: 512 * s, y: (1024 - 620) * s))
    c.strokePath()

    // Arrowhead: two strokes meeting at the tip.
    c.setLineWidth(118 * s)
    c.move(to: CGPoint(x: 330 * s, y: (1024 - 468) * s))
    c.addLine(to: CGPoint(x: 512 * s, y: (1024 - 652) * s))
    c.addLine(to: CGPoint(x: 694 * s, y: (1024 - 468) * s))
    c.strokePath()

    // Tray line under the arrow.
    c.setLineWidth(96 * s)
    c.move(to: CGPoint(x: 300 * s, y: (1024 - 806) * s))
    c.addLine(to: CGPoint(x: 724 * s, y: (1024 - 806) * s))
    c.strokePath()
}

/// Full app icon: gradient rounded square + white glyph.
func drawIcon(_ size: Int) -> CGContext {
    let c = makeContext(size)
    let s = CGFloat(size) / 1024.0

    // macOS icons keep a margin inside the canvas (~10%), like system app icons.
    let inset = 76.0 * s
    let rect = CGRect(x: inset, y: inset,
                      width: CGFloat(size) - inset * 2, height: CGFloat(size) - inset * 2)
    let radius = 196.0 * s
    let path = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)

    // Soft shadow behind the tile.
    c.saveGState()
    c.setShadow(offset: CGSize(width: 0, height: -8 * s), blur: 24 * s,
                color: CGColor(red: 0, green: 0, blue: 0, alpha: 0.35))
    c.addPath(path)
    c.setFillColor(CGColor(red: 0.35, green: 0.29, blue: 0.89, alpha: 1))
    c.fillPath()
    c.restoreGState()

    // Vertical gradient: light indigo -> deep indigo (MudBlazor primary family).
    c.saveGState()
    c.addPath(path)
    c.clip()
    let cs = CGColorSpace(name: CGColorSpace.sRGB)!
    let colors = [
        CGColor(red: 0.55, green: 0.47, blue: 1.00, alpha: 1),  // #8C78FF
        CGColor(red: 0.30, green: 0.22, blue: 0.82, alpha: 1),  // #4D38D1
    ] as CFArray
    let grad = CGGradient(colorsSpace: cs, colors: colors, locations: [0, 1])!
    c.drawLinearGradient(grad,
                         start: CGPoint(x: rect.midX, y: rect.maxY),
                         end: CGPoint(x: rect.midX, y: rect.minY), options: [])

    // Subtle top sheen.
    let sheen = [
        CGColor(red: 1, green: 1, blue: 1, alpha: 0.18),
        CGColor(red: 1, green: 1, blue: 1, alpha: 0.0),
    ] as CFArray
    let sheenGrad = CGGradient(colorsSpace: cs, colors: sheen, locations: [0, 1])!
    c.drawLinearGradient(sheenGrad,
                         start: CGPoint(x: rect.midX, y: rect.maxY),
                         end: CGPoint(x: rect.midX, y: rect.midY), options: [])

    drawGlyph(c, scale: s, color: CGColor(red: 1, green: 1, blue: 1, alpha: 1))
    c.restoreGState()
    return c
}

/// Menu-bar template image: black glyph on transparency, drawn edge-to-edge (no tile).
func drawTemplate(_ size: Int) -> CGContext {
    let c = makeContext(size)
    // Scale the 1024-space glyph (which spans roughly y 200..870) to fill the small canvas.
    let s = CGFloat(size) / 1024.0 * 1.30
    c.translateBy(x: CGFloat(size) * (1 - 1.30) / 2, y: CGFloat(size) * (1 - 1.30) / 2 + 30 * s)
    drawGlyph(c, scale: s, color: CGColor(red: 0, green: 0, blue: 0, alpha: 1))
    return c
}

let fm = FileManager.default
let iconset = "\(outDir)/AppIcon.iconset"
try? fm.createDirectory(atPath: iconset, withIntermediateDirectories: true)

for (name, size) in [("icon_16x16", 16), ("icon_16x16@2x", 32),
                     ("icon_32x32", 32), ("icon_32x32@2x", 64),
                     ("icon_128x128", 128), ("icon_128x128@2x", 256),
                     ("icon_256x256", 256), ("icon_256x256@2x", 512),
                     ("icon_512x512", 512), ("icon_512x512@2x", 1024)] {
    savePNG(drawIcon(size), "\(iconset)/\(name).png")
}
savePNG(drawIcon(256), "\(outDir)/favicon.png")
savePNG(drawTemplate(18), "\(outDir)/MenuBarIcon.png")
savePNG(drawTemplate(36), "\(outDir)/MenuBarIcon@2x.png")
print("done")
