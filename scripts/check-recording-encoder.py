"""Verify both recording bitrate modes produce fragmented H.264 MP4 over pipes."""

from pathlib import Path
import struct
import subprocess
import sys


def mp4_boxes(data):
    offset = 0
    boxes = []
    while offset + 8 <= len(data):
        size, kind = struct.unpack_from(">I4s", data, offset)
        if size < 8 or offset + size > len(data):
            raise ValueError(f"Invalid MP4 box: {kind!r}, size {size}")
        boxes.append(kind)
        offset += size
    if offset != len(data):
        raise ValueError("Truncated MP4 box header")
    return boxes


def verify(encoder):
    frames = b"".join(bytes([i * 25, 80, 160, 255]) * 64 * 48 for i in range(8))
    for preset, bitrate in (
        ("veryfast", ["-crf", "18"]),
        ("superfast", ["-crf", "18"]),
        ("veryfast", ["-b:v", "100M"]),
    ):
        result = subprocess.run(
            [
                encoder, "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", "bgra",
                "-video_size", "64x48", "-framerate", "30", "-i", "pipe:0", "-an", "-vsync", "vfr",
                "-c:v", "libx264", "-preset", preset, "-tune", "zerolatency", "-pix_fmt", "yuv420p",
                "-g", "60", *bitrate, "-movflags", "+empty_moov+frag_keyframe+default_base_moof",
                "-f", "mp4", "pipe:1",
            ],
            input=frames, stdout=subprocess.PIPE, check=True, timeout=30,
        )
        boxes = mp4_boxes(result.stdout)
        if not {b"ftyp", b"moov", b"moof", b"mdat"}.issubset(boxes) or b"avcC" not in result.stdout:
            raise ValueError(f"Missing H.264 MP4 headers or fragments: {boxes}")


if __name__ == "__main__":
    verify(str(Path(sys.argv[1]).resolve()))
