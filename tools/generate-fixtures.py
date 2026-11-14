#!/usr/bin/env python
"""Generate the PixelForge test fixture library locally.

All media is synthetic. No personal media or network downloads are used.
"""
from __future__ import annotations

import math
import os
import random
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
import pillow_heif

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "testdata" / "fixtures"
FFMPEG = ROOT / "third_party" / "ffmpeg" / "bin" / "ffmpeg.exe"
FFPROBE = ROOT / "third_party" / "ffmpeg" / "bin" / "ffprobe.exe"

OUT.mkdir(parents=True, exist_ok=True)
(OUT / "upscale-folder").mkdir(exist_ok=True)
(OUT / "upscale-folder" / "nested").mkdir(exist_ok=True)


def font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    for candidate in [
        r"C:\Windows\Fonts\segoeui.ttf",
        r"C:\Windows\Fonts\arial.ttf",
    ]:
        if Path(candidate).exists():
            try:
                return ImageFont.truetype(candidate, size)
            except Exception:
                pass
    return ImageFont.load_default()


def gradient(width: int, height: int) -> Image.Image:
    img = Image.new("RGB", (width, height))
    px = img.load()
    for y in range(height):
        for x in range(width):
            px[x, y] = (
                int(40 + 180 * x / max(1, width - 1)),
                int(60 + 130 * y / max(1, height - 1)),
                int(180 - 100 * x / max(1, width - 1)),
            )
    return img


def sRGB_profile() -> bytes | None:
    for candidate in [
        r"C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm",
        r"C:\Windows\System32\spool\drivers\color\sRGB.icc",
    ]:
        if Path(candidate).exists():
            return Path(candidate).read_bytes()
    return None


ICC = sRGB_profile()


def make_photo() -> None:
    img = gradient(1200, 800)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((80, 80, 420, 260), radius=28, fill=(245, 180, 90), outline=(255, 255, 255), width=6)
    d.ellipse((700, 280, 1050, 630), fill=(45, 165, 160), outline=(255, 255, 255), width=8)
    d.text((110, 115), "PixelForge", font=font(54), fill=(25, 30, 40))
    d.text((110, 190), "photo fixture", font=font(28), fill=(25, 30, 40))

    exif = Image.Exif()
    exif[274] = 6  # Rotate 90 CW
    exif[305] = "PixelForge Test Generator"
    exif[315] = "PixelForge QA"
    exif[271] = "PixelForge Camera"
    exif[272] = "PF-1"
    kwargs = {"exif": exif, "quality": 92, "subsampling": 1}
    if ICC:
        kwargs["icc_profile"] = ICC
    img.save(OUT / "photo-exif-orientation.jpg", **kwargs)


def make_transparent() -> None:
    img = Image.new("RGBA", (640, 420), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for i in range(28):
        alpha = int(8 + 9 * i)
        d.ellipse((30 + i * 9, 30 + i * 4, 300 + i * 9, 300 + i * 4),
                  fill=(32, 190, 175, alpha))
    d.rounded_rectangle((300, 140, 600, 360), radius=40, fill=(240, 125, 70, 255),
                        outline=(255, 255, 255, 230), width=5)
    d.text((340, 210), "alpha", font=font(52), fill=(255, 255, 255, 255))
    img.save(OUT / "transparent.png", icc_profile=ICC) if ICC else img.save(OUT / "transparent.png")


def make_webp() -> None:
    img = gradient(900, 600)
    d = ImageDraw.Draw(img)
    d.polygon([(100, 450), (450, 100), (800, 450)], fill=(255, 220, 120), outline=(30, 30, 40))
    img.save(OUT / "illustration.webp", quality=88, method=4)


def make_tiff() -> None:
    base = gradient(700, 500)
    d = ImageDraw.Draw(base)
    d.text((30, 30), "TIFF page 1", font=font(44), fill=(255, 255, 255))
    page2 = gradient(700, 500).transpose(Image.Transpose.ROTATE_180)
    d2 = ImageDraw.Draw(page2)
    d2.text((30, 30), "TIFF page 2", font=font(44), fill=(255, 255, 255))
    page3 = gradient(700, 500).transpose(Image.Transpose.FLIP_LEFT_RIGHT)
    d3 = ImageDraw.Draw(page3)
    d3.text((30, 30), "TIFF page 3", font=font(44), fill=(255, 255, 255))
    base.save(OUT / "multipage.tiff", save_all=True, append_images=[page2, page3],
              compression="tiff_lzw", icc_profile=ICC) if ICC else base.save(
        OUT / "multipage.tiff", save_all=True, append_images=[page2, page3], compression="tiff_lzw")


def make_bmp() -> None:
    img = gradient(480, 320)
    d = ImageDraw.Draw(img)
    d.text((20, 20), "BMP", font=font(72), fill=(255, 255, 255))
    img.save(OUT / "bitmap.bmp")


def make_gif() -> None:
    frames = []
    for i in range(8):
        frame = gradient(400, 260)
        d = ImageDraw.Draw(frame)
        x = 30 + i * 42
        d.rounded_rectangle((x, 90, x + 80, 170), radius=16, fill=(255, 210, 80))
        d.text((20, 15), f"frame {i + 1}", font=font(30), fill=(255, 255, 255))
        frames.append(frame.convert("P", palette=Image.Palette.ADAPTIVE))
    frames[0].save(OUT / "animated.gif", save_all=True, append_images=frames[1:], duration=130,
                   loop=0, disposal=2)


def make_ico() -> None:
    img = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((12, 12, 244, 244), radius=52, fill=(210, 105, 58, 255))
    d.text((58, 78), "PF", font=font(120), fill=(255, 255, 255, 255))
    img.save(OUT / "app-icon.ico", sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


def make_heic() -> None:
    pillow_heif.register_heif_opener()
    img = gradient(800, 600)
    d = ImageDraw.Draw(img)
    d.text((30, 30), "HEIC", font=font(70), fill=(255, 255, 255))
    img.save(OUT / "sample.heic", quality=82)


def make_large() -> None:
    img = gradient(6000, 4000)
    d = ImageDraw.Draw(img)
    for i in range(90):
        x = random.Random(i).randint(0, 5800)
        y = random.Random(i + 1000).randint(0, 3800)
        d.ellipse((x, y, x + 160, y + 160), fill=(120, 220, 200))
    img.save(OUT / "large-6000x4000.jpg", quality=85)


def make_corrupt() -> None:
    data = (OUT / "illustration.webp").read_bytes()
    (OUT / "corrupted.jpg").write_bytes(data[: len(data) // 3])
    (OUT / "wrong-extension.jpg").write_bytes((OUT / "transparent.png").read_bytes())
    (OUT / "not-an-image.png").write_text("This is not an image.\n", encoding="utf-8")


def make_svg() -> None:
    (OUT / "vector.svg").write_text(
        """<svg xmlns="http://www.w3.org/2000/svg" width="400" height="240" viewBox="0 0 400 240">
  <rect width="400" height="240" fill="#13212b"/>
  <circle cx="120" cy="120" r="72" fill="#2aa7a0"/>
  <rect x="210" y="58" width="130" height="124" rx="18" fill="#e8833a"/>
  <text x="24" y="222" font-family="Segoe UI" font-size="24" fill="#ffffff">Vector fixture</text>
</svg>""",
        encoding="utf-8",
    )


def run(args: list[str], allow_failure: bool = False) -> bool:
    result = subprocess.run([str(FFMPEG), "-hide_banner", "-loglevel", "error", "-y", *args],
                            capture_output=True, text=True)
    if result.returncode != 0:
        if allow_failure:
            print(f"SKIP: ffmpeg {' '.join(args[:8])}...\n  {result.stderr.strip()[:500]}")
            return False
        raise RuntimeError(f"ffmpeg failed: {result.stderr}")
    return True


def make_avif_jxl() -> None:
    run(["-i", str(OUT / "transparent.png"), "-c:v", "libaom-av1", "-still-picture", "1",
         "-crf", "28", str(OUT / "alpha.avif")], allow_failure=True)
    run(["-i", str(OUT / "illustration.webp"), "-c:v", "libjxl", "-quality", "88",
         str(OUT / "illustration.jxl")], allow_failure=True)


def make_audio() -> None:
    meta = ["-metadata", "title=PixelForge Tone", "-metadata", "artist=PixelForge QA",
            "-metadata", "album=Fixture Album", "-metadata", "comment=Synthetic test signal"]
    run(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
         "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000:duration=3",
         "-filter_complex", "[0:a][1:a]amerge=inputs=2[a]", "-map", "[a]",
         "-c:a", "pcm_s16le", *meta, str(OUT / "tone.wav")])
    run(["-i", str(OUT / "tone.wav"), "-c:a", "libmp3lame", "-b:a", "192k", *meta,
         "-id3v2_version", "3", str(OUT / "tone.mp3")])
    run(["-i", str(OUT / "tone.wav"), "-c:a", "flac", "-compression_level", "5", *meta,
         str(OUT / "tone.flac")])
    run(["-i", str(OUT / "tone.wav"), "-c:a", "aac", "-b:a", "192k", *meta,
         "-movflags", "+faststart", str(OUT / "tone.m4a")])
    run(["-i", str(OUT / "tone.wav"), "-c:a", "libvorbis", "-q:a", "6", *meta,
         str(OUT / "tone.ogg")])
    run(["-i", str(OUT / "tone.wav"), "-c:a", "libopus", "-b:a", "128k", *meta,
         str(OUT / "tone.opus")])


def make_video() -> None:
    chapters = OUT / "chapters.txt"
    chapters.write_text(
        """;FFMETADATA1
title=PixelForge Test Clip
artist=PixelForge QA
comment=Synthetic fixture
[CHAPTER]
TIMEBASE=1/1000
START=0
END=2000
title=Opening
[CHAPTER]
TIMEBASE=1/1000
START=2000
END=4000
title=Middle
[CHAPTER]
TIMEBASE=1/1000
START=4000
END=5000
title=Ending
""",
        encoding="utf-8")
    subs = OUT / "subtitles.srt"
    subs.write_text(
        """1
00:00:00,300 --> 00:00:01,800
PixelForge subtitle test

2
00:00:02,000 --> 00:00:03,700
This stream should survive a compatible remux.
""",
        encoding="utf-8")

    run(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=5",
         "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=5",
         "-map", "0:v", "-map", "1:a", "-c:v", "libopenh264", "-b:v", "4500k",
         "-c:a", "aac", "-b:a", "160k", "-pix_fmt", "yuv420p",
         "-metadata", "title=PixelForge Test Clip", "-metadata", "artist=PixelForge QA",
         str(OUT / "clip-h264.mp4")])
    run(["-i", str(OUT / "clip-h264.mp4"), "-f", "ffmetadata", "-i", str(chapters),
         "-i", str(subs), "-map", "0:v", "-map", "0:a", "-map", "2:s",
         "-map_metadata", "1", "-map_chapters", "1", "-c:v", "copy", "-c:a", "copy",
         "-c:s", "srt", str(OUT / "clip-with-subs-chapters.mkv")])
    run(["-f", "lavfi", "-i", "testsrc2=size=960x544:rate=25:duration=4",
         "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:duration=4",
         "-map", "0:v", "-map", "1:a", "-c:v", "hevc_amf", "-b:v", "2500k",
         "-c:a", "aac", "-b:a", "160k", str(OUT / "clip-hevc.mkv")], allow_failure=True)
    run(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=4",
         "-f", "lavfi", "-i", "sine=frequency=550:sample_rate=48000:duration=4",
         "-map", "0:v", "-map", "1:a", "-c:v", "libvpx-vp9", "-b:v", "900k",
         "-c:a", "libopus", "-b:a", "96k", str(OUT / "clip-vp9.webm")])
    run(["-f", "lavfi", "-i", "testsrc2=size=640x480:rate=25:duration=3",
         "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=44100:duration=3",
         "-map", "0:v", "-map", "1:a", "-c:v", "mpeg4", "-q:v", "4",
         "-c:a", "libmp3lame", "-b:a", "128k", str(OUT / "clip-mpeg4.avi")])
    run(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24:duration=2",
         "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2",
         "-map", "0:v", "-map", "1:a", "-c:v", "prores_ks", "-profile:v", "standard",
         "-pix_fmt", "yuv422p10le", "-c:a", "pcm_s16le", str(OUT / "clip-prores.mov")])
    run(["-f", "lavfi", "-i", "testsrc2=size=480x854:rate=30:duration=2",
         "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
         "-map", "0:v", "-map", "1:a", "-c:v", "libopenh264", "-b:v", "2200k",
         "-c:a", "aac", "-b:a", "128k", "-pix_fmt", "yuv420p",
         str(OUT / "clip-vertical.mp4")])
    run(["-i", str(OUT / "clip-h264.mp4"), "-vf", "fps=10,scale=400:-1:flags=lanczos",
         "-t", "2", "-an", str(OUT / "clip-preview.gif")], allow_failure=True)


def make_upscale_folder() -> None:
    folder = OUT / "upscale-folder"
    shutil.copy(OUT / "photo-exif-orientation.jpg", folder / "photo.jpg")
    shutil.copy(OUT / "transparent.png", folder / "illustration.png")
    shutil.copy(OUT / "illustration.webp", folder / "texture.webp")
    shutil.copy(OUT / "bitmap.bmp", folder / "bitmap.bmp")
    shutil.copy(OUT / "multipage.tiff", folder / "scan.tiff")
    shutil.copy(OUT / "corrupted.jpg", folder / "corrupted.jpg")
    shutil.copy(OUT / "wrong-extension.jpg", folder / "wrong-extension.jpg")
    (folder / "readme.txt").write_text("This should be ignored.\n", encoding="utf-8")
    (folder / "document.pdf").write_bytes(b"%PDF-1.4\n% synthetic placeholder\n")
    shutil.copy(OUT / "tone.mp3", folder / "audio.mp3")
    shutil.copy(OUT / "clip-h264.mp4", folder / "video.mp4")
    shutil.copy(OUT / "animated.gif", folder / "animation.gif")
    (folder / "nested" / "deep-photo.jpg").write_bytes((OUT / "photo-exif-orientation.jpg").read_bytes())
    (folder / "nested" / "notes.md").write_text("ignored nested text\n", encoding="utf-8")


def main() -> None:
    random.seed(20261111)
    if not FFMPEG.exists():
        raise SystemExit(f"FFmpeg not found at {FFMPEG}")
    print(f"Writing fixtures to {OUT}")
    make_photo()
    make_transparent()
    make_webp()
    make_tiff()
    make_bmp()
    make_gif()
    make_ico()
    make_large()
    make_corrupt()
    make_svg()
    make_heic()
    make_avif_jxl()
    make_audio()
    make_video()
    make_upscale_folder()
    for file in sorted(OUT.rglob("*")):
        if file.is_file():
            print(f"  {file.relative_to(OUT)}  {file.stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
