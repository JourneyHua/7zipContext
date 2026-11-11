# 7zipContext 图标生成器（纯标准库）：蓝色渐变圆角方块 + 白色拉链
# 输出 assets/7zipContext.ico（PNG 内嵌 ICO）与 assets/7zipContext.png 预览
import struct, zlib, math
from pathlib import Path

S = 512
OUT = Path(__file__).resolve().parent.parent / "assets"
OUT.mkdir(exist_ok=True)

def clamp(v, a, b):
    return a if v < a else (b if v > b else v)

def rr_cov(x, y, x0, y0, x1, y1, r):
    """圆角矩形覆盖率（0..1，边缘抗锯齿）"""
    if x < x0 - 1 or x > x1 + 1 or y < y0 - 1 or y > y1 + 1:
        return 0.0
    cx = clamp(x, x0 + r, x1 - r)
    cy = clamp(y, y0 + r, y1 - r)
    d = math.hypot(x - cx, y - cy)
    if d <= r - 0.5:
        return 1.0
    if d >= r + 0.5:
        return 0.0
    return r + 0.5 - d

def over(dst, src, a):
    return tuple(dst[i] * (1 - a) + src[i] * a for i in range(4))

WHITE = (255.0, 255.0, 255.0, 255.0)

buf = []
for y in range(S):
    row = []
    t = y / (S - 1)
    # 蓝色竖向渐变：#4A90FF → #2563EB
    bg = (74 + (37 - 74) * t, 144 + (99 - 144) * t, 255 + (235 - 255) * t, 255.0)
    for x in range(S):
        cov = rr_cov(x + 0.5, y + 0.5, 2, 2, S - 2, S - 2, 104)
        if cov <= 0:
            row.append((0.0, 0.0, 0.0, 0.0))
            continue
        c = (bg[0], bg[1], bg[2], 255.0 * cov)

        # 拉链齿：左右两列白色圆角块，交错咬合
        for k in range(11):
            yy = 84 + k * 32
            a = rr_cov(x + 0.5, y + 0.5, 176, yy, 252, yy + 20, 8)
            if a > 0:
                c = over(c, WHITE, a)
            a = rr_cov(x + 0.5, y + 0.5, 260, yy + 16, 336, yy + 36, 8)
            if a > 0:
                c = over(c, WHITE, a)

        # 滑块 + 拉头
        for (x0, y0, x1, y1, r) in [
            (228, 226, 284, 282, 12),   # slider
            (246, 282, 266, 336, 10),   # pull bar
            (236, 330, 276, 370, 20),   # pull ring
        ]:
            a = rr_cov(x + 0.5, y + 0.5, x0, y0, x1, y1, r)
            if a > 0:
                c = over(c, WHITE, a)
        # 拉环中间的孔（挖回蓝底近似：小圆角块用渐变色）
        a = rr_cov(x + 0.5, y + 0.5, 248, 342, 264, 358, 8)
        if a > 0:
            c = over(c, bg, a)

        row.append(c)
    buf.append(row)

def to_png(px, w, h):
    raw = b"".join(
        b"\x00" + b"".join(struct.pack("4B", *(
            int(max(0, min(255, round(v)))) for v in px[y][x]
        )) for x in range(w))
        for y in range(h)
    )
    def chunk(t, d):
        c = t + d
        return struct.pack(">I", len(d)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))

png256 = to_png(buf, S, S)
# 缩放到 48/32/16 供 ICO 多尺寸（盒式平均）
def shrink(px, tw, th):
    sw, sh = S, S
    out = []
    for ty in range(th):
        row = []
        for tx in range(tw):
            x0, x1 = tx * sw // tw, max(tx * sw // tw + 1, (tx + 1) * sw // tw)
            y0, y1 = ty * sh // th, max(ty * sh // th + 1, (ty + 1) * sh // th)
            acc = [0.0] * 4
            n = 0
            for yy in range(y0, y1):
                for xx in range(x0, x1):
                    for i in range(4):
                        acc[i] += px[yy][xx][i]
                    n += 1
            row.append(tuple(v / n for v in acc))
        out.append(row)
    return out

sizes = [(256, shrink(buf, 256, 256)), (48, shrink(buf, 48, 48)), (32, shrink(buf, 32, 32)), (16, shrink(buf, 16, 16))]
pngs = [(sz, to_png(p, sz, sz)) for sz, p in sizes]

# ICO：每项内嵌 PNG
entries = b""
blobs = b""
offset = 6 + 16 * len(pngs)
for sz, data in pngs:
    entries += struct.pack("<BBBBHHII", sz if sz < 256 else 0, sz if sz < 256 else 0,
                           0, 0, 1, 32, len(data), offset)
    blobs += data
    offset += len(data)
ico = struct.pack("<HHH", 0, 1, len(pngs)) + entries + blobs

(OUT / "7zipContext.png").write_bytes(pngs[0][1])
(OUT / "7zipContext.ico").write_bytes(ico)
print("icon ok:", OUT / "7zipContext.ico", len(ico), "bytes")
