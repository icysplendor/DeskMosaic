#!/usr/bin/env python3
"""Create Xcode-ready PNG and ICNS assets using only the Python standard library."""

from __future__ import annotations

import binascii
import math
import pathlib
import struct
import zlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
GENERATED = ROOT / "MACAssistant" / "Resources" / "Generated"
APPICON_SET = ROOT / "MACAssistant" / "Assets.xcassets" / "AppIcon.appiconset"
EMBLEM_SET = ROOT / "MACAssistant" / "Assets.xcassets" / "AppEmblem.imageset"
MENUBAR_SET = ROOT / "MACAssistant" / "Assets.xcassets" / "MenuBarIcon.imageset"
WINDOWS_RESOURCES = ROOT / "WinAssistant" / "Resources"


def read_png(path: pathlib.Path) -> tuple[int, int, list[tuple[int, int, int, int]]]:
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} is not a PNG")
    offset = 8
    compressed = bytearray()
    width = height = color_type = 0
    while offset < len(data):
        length = struct.unpack(">I", data[offset : offset + 4])[0]
        kind = data[offset + 4 : offset + 8]
        payload = data[offset + 8 : offset + 8 + length]
        offset += 12 + length
        if kind == b"IHDR":
            width, height, depth, color_type, compression, filtering, interlace = struct.unpack(
                ">IIBBBBB", payload
            )
            if depth != 8 or color_type not in (2, 6) or interlace != 0:
                raise ValueError("Only non-interlaced 8-bit RGB/RGBA PNGs are supported")
        elif kind == b"IDAT":
            compressed.extend(payload)
        elif kind == b"IEND":
            break

    channels = 3 if color_type == 2 else 4
    stride = width * channels
    raw = zlib.decompress(bytes(compressed))
    rows: list[bytearray] = []
    cursor = 0
    previous = bytearray(stride)
    for _ in range(height):
        filter_type = raw[cursor]
        cursor += 1
        scanline = bytearray(raw[cursor : cursor + stride])
        cursor += stride
        reconstructed = bytearray(stride)
        for x, value in enumerate(scanline):
            left = reconstructed[x - channels] if x >= channels else 0
            up = previous[x]
            upper_left = previous[x - channels] if x >= channels else 0
            if filter_type == 0:
                predictor = 0
            elif filter_type == 1:
                predictor = left
            elif filter_type == 2:
                predictor = up
            elif filter_type == 3:
                predictor = (left + up) // 2
            elif filter_type == 4:
                p = left + up - upper_left
                pa, pb, pc = abs(p - left), abs(p - up), abs(p - upper_left)
                predictor = left if pa <= pb and pa <= pc else up if pb <= pc else upper_left
            else:
                raise ValueError(f"Unsupported PNG filter {filter_type}")
            reconstructed[x] = (value + predictor) & 0xFF
        rows.append(reconstructed)
        previous = reconstructed

    pixels = []
    for row in rows:
        for x in range(width):
            sample = row[x * channels : (x + 1) * channels]
            if channels == 3:
                pixels.append((sample[0], sample[1], sample[2], 255))
            else:
                pixels.append(tuple(sample))
    return width, height, pixels


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    return (
        struct.pack(">I", len(payload))
        + kind
        + payload
        + struct.pack(">I", binascii.crc32(kind + payload) & 0xFFFFFFFF)
    )


def png_bytes(width: int, height: int, pixels: list[tuple[int, int, int, int]]) -> bytes:
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for pixel in pixels[y * width : (y + 1) * width]:
            rows.extend(pixel)
    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", header)
        + png_chunk(b"IDAT", zlib.compress(bytes(rows), 9))
        + png_chunk(b"IEND", b"")
    )


def resize(
    width: int,
    height: int,
    pixels: list[tuple[int, int, int, int]],
    target_width: int,
    target_height: int,
) -> list[tuple[int, int, int, int]]:
    if (width, height) == (target_width, target_height):
        return pixels.copy()
    # Area sampling preserves thin edges at tray sizes. Weight RGB by alpha
    # so transparent source pixels cannot leave a dark fringe on light themes.
    result = []
    for ty in range(target_height):
        top, bottom = ty * height / target_height, (ty + 1) * height / target_height
        for tx in range(target_width):
            left, right = tx * width / target_width, (tx + 1) * width / target_width
            alpha = red = green = blue = 0.0
            for sy in range(math.floor(top), min(height, math.ceil(bottom))):
                wy = min(bottom, sy + 1) - max(top, sy)
                for sx in range(math.floor(left), min(width, math.ceil(right))):
                    weight = wy * (min(right, sx + 1) - max(left, sx))
                    r, g, b, a = pixels[sy * width + sx]
                    coverage = a * weight
                    alpha += coverage
                    red += r * coverage
                    green += g * coverage
                    blue += b * coverage
            area = (right - left) * (bottom - top)
            result.append((round(red / alpha), round(green / alpha), round(blue / alpha),
                           round(alpha / area)) if alpha else (0, 0, 0, 0))
    return result


def crop(
    width: int,
    pixels: list[tuple[int, int, int, int]],
    box: tuple[int, int, int, int],
) -> tuple[int, int, list[tuple[int, int, int, int]]]:
    left, top, right, bottom = box
    output = []
    for y in range(top, bottom):
        output.extend(pixels[y * width + left : y * width + right])
    return right - left, bottom - top, output


def write_resized(
    source: tuple[int, int, list[tuple[int, int, int, int]]],
    size: int,
    destination: pathlib.Path,
) -> bytes:
    width, height, pixels = source
    data = png_bytes(size, size, resize(width, height, pixels, size, size))
    destination.write_bytes(data)
    return data


def write_ico(destination: pathlib.Path, frames: dict[int, bytes]) -> None:
    ico_header_size = 6 + 16 * len(frames)
    ico_entries = bytearray()
    ico_payload = bytearray()
    for size, data in frames.items():
        size_byte = 0 if size == 256 else size
        ico_entries.extend(
            struct.pack(
                "<BBBBHHII",
                size_byte,
                size_byte,
                0,
                0,
                1,
                32,
                len(data),
                ico_header_size + len(ico_payload),
            )
        )
        ico_payload.extend(data)
    destination.write_bytes(
        struct.pack("<HHH", 0, 1, len(frames)) + ico_entries + ico_payload
    )


def main() -> None:
    APPICON_SET.mkdir(parents=True, exist_ok=True)
    EMBLEM_SET.mkdir(parents=True, exist_ok=True)
    MENUBAR_SET.mkdir(parents=True, exist_ok=True)
    WINDOWS_RESOURCES.mkdir(parents=True, exist_ok=True)

    app_source = read_png(GENERATED / "AppIcon-source.png")
    icon_pngs = {
        size: write_resized(app_source, size, APPICON_SET / f"AppIcon-{size}.png")
        for size in (16, 32, 64, 128, 256, 512, 1024)
    }
    icns_types = {16: b"icp4", 32: b"icp5", 64: b"icp6", 128: b"ic07", 256: b"ic08", 512: b"ic09", 1024: b"ic10"}
    body = b"".join(
        kind + struct.pack(">I", len(icon_pngs[size]) + 8) + icon_pngs[size]
        for size, kind in icns_types.items()
    )
    (GENERATED.parent / "AppIcon.icns").write_bytes(b"icns" + struct.pack(">I", len(body) + 8) + body)
    write_ico(WINDOWS_RESOURCES / "AppIcon.ico", {
        size: icon_pngs[size] for size in (16, 32, 64, 128, 256)
    })

    # macOS template tinting uses the same emblem's genuine alpha channel.
    # Do not chroma-key the artwork or bake a theme-specific background into it.
    width, height, pixels = read_png(GENERATED / "AppEmblem-source.png")
    transparent = [(0, 0, 0, alpha) for _, _, _, alpha in pixels]
    dark_points = [(index % width, index // width)
                   for index, pixel in enumerate(pixels) if pixel[3] > 180]
    if not dark_points or not any(pixel[3] == 0 for pixel in pixels):
        raise ValueError("App icon requires a visible emblem and transparent background")
    padding = max(24, int(max(width, height) * 0.04))
    left = max(0, min(point[0] for point in dark_points) - padding)
    top = max(0, min(point[1] for point in dark_points) - padding)
    right = min(width, max(point[0] for point in dark_points) + padding + 1)
    bottom = min(height, max(point[1] for point in dark_points) + padding + 1)
    side = max(right - left, bottom - top)
    center_x, center_y = (left + right) // 2, (top + bottom) // 2
    left, top = max(0, center_x - side // 2), max(0, center_y - side // 2)
    right, bottom = min(width, left + side), min(height, top + side)
    bounds = (left, top, right, bottom)
    menu_source = crop(width, transparent, bounds)
    emblem_source = crop(width, pixels, bounds)
    write_resized(emblem_source, 128, EMBLEM_SET / "AppEmblem.png")
    emblem_width, emblem_height, emblem_pixels = emblem_source
    write_ico(WINDOWS_RESOURCES / "TrayIcon.ico", {
        size: png_bytes(size, size, resize(emblem_width, emblem_height, emblem_pixels, size, size))
        for size in (16, 24, 32, 48)
    })

    (GENERATED / "MenuBarIcon-transparent.png").write_bytes(
        png_bytes(width, height, transparent)
    )
    write_resized(menu_source, 16, MENUBAR_SET / "MenuBarIcon-16.png")
    write_resized(menu_source, 32, MENUBAR_SET / "MenuBarIcon-32.png")


if __name__ == "__main__":
    main()
