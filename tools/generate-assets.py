from PIL import Image, ImageDraw, ImageFont
from pathlib import Path

root = Path(__file__).resolve().parent.parent
assets = root / "src" / "PixelForge.Presentation.WinUI" / "Assets"
assets.mkdir(parents=True, exist_ok=True)
fonts = r"C:\Windows\Fonts\segoeuib.ttf"

def rounded(draw, box, radius, fill, outline=None, width=0):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)

img = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
# Rounded app tile
rounded(d, (32, 32, 992, 992), 210, (18, 26, 34, 255), outline=(255, 255, 255, 26), width=6)
# Faint inner accent glow / layers
rounded(d, (110, 110, 914, 914), 160, (22, 33, 43, 255), outline=(255, 255, 255, 14), width=4)
# Forge stack: three offset panels
rounded(d, (238, 218, 782, 806), 140, (214, 105, 58, 255))
rounded(d, (286, 266, 734, 758), 112, (24, 38, 49, 255))
# teal bar / P shape
rounded(d, (386, 330, 520, 690), 60, (42, 167, 160, 255))
rounded(d, (472, 330, 700, 494), 78, (42, 167, 160, 255))
rounded(d, (552, 386, 654, 452), 32, (24, 38, 49, 255))
# sparks / pixels
for (x, y, s, c) in [(690, 640, 34, (232, 131, 58, 255)), (742, 586, 24, (232, 131, 58, 220)),
                     (666, 704, 20, (232, 131, 58, 180)), (786, 654, 16, (42, 167, 160, 220))]:
    rounded(d, (x, y, x + s, y + s), 8, c)
img.save(assets / "PixelForge.png")
img.save(assets / "PixelForge.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

# Transparent wordmark tile for in-app logo
logo = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
ld = ImageDraw.Draw(logo)
rounded(ld, (8, 8, 248, 248), 56, (214, 105, 58, 255))
rounded(ld, (24, 24, 232, 232), 44, (24, 38, 49, 255))
rounded(ld, (80, 62, 112, 194), 16, (42, 167, 160, 255))
rounded(ld, (104, 62, 178, 110), 22, (42, 167, 160, 255))
rounded(ld, (130, 78, 158, 98), 9, (24, 38, 49, 255))
for (x, y, s) in [(172, 164, 10), (186, 150, 8), (164, 180, 7)]:
    rounded(ld, (x, y, x + s, y + s), 2, (232, 131, 58, 255))
logo.save(assets / "Logo.png")

# Checkerboard for transparency previews
check = Image.new("RGB", (512, 512))
cd = ImageDraw.Draw(check)
for y in range(0, 512, 32):
    for x in range(0, 512, 32):
        color = (30, 36, 43) if (x // 32 + y // 32) % 2 == 0 else (39, 46, 54)
        cd.rectangle((x, y, x + 31, y + 31), fill=color)
check.save(assets / "Checker.png")

print("assets written", assets)
