"""Offline ground-shadow simulator (2026-11-14).

Same model as the game's GPU shadow (tools/gpu_shadow/UnityProject/Assets/Shaders: ShadowSil, ShadowDepth, ShadowCast),
run on a sprite + its depth map, so the look can be judged and tuned without restarting the game.

Model (world units = sprite pixels at the chosen --scale; y up; orthographic view, ground seen at tilt t):
  - body = sprite pixels with alpha >= 128
  - front depth   s = 0.5 - v   (v = depth map 0..1, bright = near; the map's cut rim is filled from 2 px inward)
  - thickness     h = R (1 - (1 - min(d/R, 1))^2), d = distance to the outline, R = thickness x texture width
  - each pixel is a slab of points at depth z in [s D, s D + 2h]; its ground point is at screen y = c + z t
    and its height H = y - (c + z t); D (depth scale) and c (ground) come from the sole fit
  - light: azimuth a (screen direction the shadow falls), elevation e: a point of height H casts onto the ground at
    x + L cos a, (c + z t) + L sin a t, with L = H / tan e
The game marches from each ground pixel toward the light; this splats every body point forward onto the ground.
Both give the same set of shadowed ground points (up to sampling), but splatting is far faster in plain Python.

Sole fits (--fit):
  legacy  exactly what the game does now (plugin a97c087bcd28): lowest pixel of the texture's bottom quarter = front
          sole; lowest pixel at least 20 % of the TEXTURE width away = rear sole; depth read at one pixel each;
          D = dy / (ds t), clamped to 3 x texture width; fallback depthRange x width; c = min over the bottom quarter.
  robust  proposal: soles from the figure's own outline (20 % of the FIGURE width apart), depth = mean over each
          foot's lowest band away from the outline, D solved with the slab middles, c through the front sole;
          D must lie within 0.1-2 x figure width, else fallback.
  fixed   D = depthRange x texture width, c = min over the bottom quarter (no sole solving).

Usage (from anywhere; reads the game folder, writes only to data/shadow_sim/<run>):
  python shadow_sim.py                      all party sprites with a depth map, robust fit, azimuth 0,90,180,270
  python shadow_sim.py --fit legacy         what the game does now
  python shadow_sim.py --az 0,300 --elevation 45 --tilt 0.5 --thickness 0.3 path/to/Default_00.png
Defaults for elevation, groundTilt, opacity, thickness, depthRange come from MenuLayout.json's battle.groundShadow.
"""
import argparse
import glob
import json
import math
import os
import statistics
import sys
import time

from PIL import Image, ImageDraw, ImageFilter

# Game folder: environment variable FF5_GAME, else the default below. Output: SHADOW_SIM_OUT, else <project>/data/shadow_sim.
GAME = os.environ.get('FF5_GAME', r'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR')
MOD = os.path.join(GAME, r'Modules\00-Mods\SpriteMod HD\FF5')
PARTY_GLOB = os.path.join(MOD, r'GameAssets\Serial\Res\Chara\Battle\*\*.depth.png')
PROJECT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
OUT_ROOT = os.environ.get('SHADOW_SIM_OUT', os.path.join(PROJECT, 'data', 'shadow_sim'))

GROUND = (128, 140, 104)
GRID = (146, 158, 122)
SHADOW = (0, 0, 0)


# ---------------------------------------------------------------- config

def load_config():
    """battle.groundShadow from MenuLayout.json (read-only); missing keys take the plugin's defaults."""
    cfg = {'elevation': 50.0, 'groundTilt': 0.5, 'opacity': 0.45, 'thickness': 0.25, 'depthRange': 0.6, 'blur': 4.0, 'falloff': 0.6}
    path = os.path.join(MOD, 'MenuLayout.json')
    try:
        with open(path, encoding='utf-8') as f:
            gs = json.load(f).get('battle', {}).get('groundShadow', {})
        for k in cfg:
            if isinstance(gs.get(k), (int, float)):
                cfg[k] = float(gs[k])
    except Exception as e:
        print(f'note: could not read {path} ({e}); plugin defaults used')
    return cfg


# ---------------------------------------------------------------- body

class Body:
    """A sprite at the simulation scale: mask, outline distance, front depth s, thickness h (all y-up, row 0 = bottom)."""

    def __init__(self, sprite_path, depth_path, scale, thickness):
        spr = Image.open(sprite_path).convert('RGBA')
        W0, H0 = spr.size
        self.W, self.H = max(8, round(W0 * scale)), max(8, round(H0 * scale))
        spr = spr.resize((self.W, self.H), Image.LANCZOS)
        self.sprite = spr
        W, H = self.W, self.H
        a = spr.getchannel('A').load()
        self.mask = bytearray(W * H)
        for y in range(H):
            row = (H - 1 - y)
            for x in range(W):
                if a[x, row] >= 128:
                    self.mask[y * W + x] = 1
        self.has_map = depth_path is not None and os.path.exists(depth_path)
        self.v = [0.0] * (W * H)
        if self.has_map:
            dm = Image.open(depth_path).convert('L')
            if dm.size != (W0, H0):
                print(f'  WARNING: depth map {dm.size} differs from sprite {(W0, H0)}: resized, may not line up')
            dm = dm.resize((W, H), Image.BILINEAR).load()
            for y in range(H):
                row = H - 1 - y
                for x in range(W):
                    self.v[y * W + x] = dm[x, row] / 255.0
        self.dist = chamfer(self.mask, W, H)
        self.R = max(0.5, thickness * W)                      # the game: thickness x renderer (texture) width
        self.s = [0.0] * (W * H)
        self.h = [0.0] * (W * H)
        for i in range(W * H):
            if not self.mask[i]:
                continue
            q = 1.0 - min(self.dist[i] / self.R, 1.0)
            self.h[i] = self.R * (1.0 - q * q)
            if self.has_map:
                self.s[i] = 0.5 - self.front_v(i % W, i // W)
        xs = [i % W for i in range(W * H) if self.mask[i]]
        ys = [i // W for i in range(W * H) if self.mask[i]]
        if not xs:
            raise ValueError('sprite has no pixel with alpha >= 128')
        self.bx0, self.bx1, self.by0, self.by1 = min(xs), max(xs), min(ys), max(ys)
        self.pixels = len(xs)

    def front_v(self, x, y):
        """Depth map value with the shader's rim fill (ShadowSil FrontZ): < 0.04 -> brightest of 4 neighbours 2 px in, else 0.5."""
        W, H = self.W, self.H
        v = self.v[y * W + x]
        if v >= 0.04:
            return v
        best = 0.0
        for dx, dy in ((2, 0), (-2, 0), (0, 2), (0, -2)):
            xx, yy = x + dx, y + dy
            if 0 <= xx < W and 0 <= yy < H:
                best = max(best, self.v[yy * W + xx])
        return best if best >= 0.04 else 0.5

    def lowest_in_column(self, x, ytop):
        """Lowest body pixel of column x at or below ytop, or None."""
        W = self.W
        for y in range(0, min(ytop, self.H - 1) + 1):
            if self.mask[y * W + x]:
                return y
        return None


def chamfer(mask, W, H):
    """Distance (px) from each body pixel to the nearest non-body pixel; 2-pass chamfer (1, sqrt 2). Outside the image counts as outside."""
    INF = 1e9
    d = [0.0 if not mask[i] else INF for i in range(W * H)]
    r2 = math.sqrt(2.0)
    for y in range(H):
        for x in range(W):
            i = y * W + x
            if d[i] == 0.0:
                continue
            best = d[i]
            for dx, dy, w in ((-1, 0, 1.0), (0, -1, 1.0), (-1, -1, r2), (1, -1, r2)):
                xx, yy = x + dx, y + dy
                nb = d[yy * W + xx] if 0 <= xx < W and 0 <= yy < H else 0.0
                best = min(best, nb + w)
            d[i] = best
    for y in range(H - 1, -1, -1):
        for x in range(W - 1, -1, -1):
            i = y * W + x
            if d[i] == 0.0:
                continue
            best = d[i]
            for dx, dy, w in ((1, 0, 1.0), (0, 1, 1.0), (1, 1, r2), (-1, 1, r2)):
                xx, yy = x + dx, y + dy
                nb = d[yy * W + xx] if 0 <= xx < W and 0 <= yy < H else 0.0
                best = min(best, nb + w)
            d[i] = best
    return d


# ---------------------------------------------------------------- sole fits

class Fit:
    def __init__(self, D, c, solved, note, front=None, rear=None):
        self.D, self.c, self.solved, self.note, self.front, self.rear = D, c, solved, note, front, rear
        self.ground = None                                    # flyers: the default shadow's base (x, y), drawn as a ring
        self.gap = 0.0                                        # flyers: the gap (px), counted in the body's height for falloff


def ground_c(body, D, t, ytop):
    """c = min over body pixels at or below ytop of y - (s D + h) t  (the body slid down until its lowest 3D point touches)."""
    W = body.W
    best = 1e18
    for y in range(0, min(ytop, body.H - 1) + 1):
        for x in range(W):
            i = y * W + x
            if body.mask[i]:
                best = min(best, y - (body.s[i] * D + body.h[i]) * t)
    return best


def fit_lifted(body, cfg, lift_px, centred):
    """A flyer: the ground is a flat line lift_px below the figure's lowest pixel (the default shadow's base); the gap
    counts as part of the sprite. Depth scale = depthRange x texture width (no soles). centred=False reproduces the game
    (plugin 5ec5f68d3947): each point's ground spot moves back by its slab middle z, uncentred, so every point loses
    height; centred=True measures z from the body's mean slab middle, so the line is the body's middle depth."""
    t, W = cfg['groundTilt'], body.W
    D = cfg['depthRange'] * W
    line = body.by0 - lift_px
    if centred:
        # The gap is empty space under the sprite (user, 2026-11-14): like a standing body, which is slid down until its
        # lowest 3D point touches the ground, a flyer is placed so its lowest 3D point is exactly the gap above it. No
        # point is lower than the gap, so no shadow starts closer than gap / tan(elevation).
        low = min(y - (body.s[y * W + x] * D + body.h[y * W + x]) * t for y in range(body.H) for x in range(W) if body.mask[y * W + x])
        c = low - lift_px
        note = f'flyer: lowest 3D point {lift_px:.0f} px above the ground'
    else:
        c = line
        note = f'flyer: gap {lift_px:.0f} px, depth uncentred (game)'
    f = Fit(D, c, False, note)
    f.ground = ((body.bx0 + body.bx1) // 2, line)
    f.gap = lift_px
    return f


def fit_fixed(body, cfg):
    D = cfg['depthRange'] * body.W
    return Fit(D, ground_c(body, D, cfg['groundTilt'], body.H // 4), False, 'fixed: depthRange x texture width')


def fit_legacy(body, cfg):
    """Mirror of ShadowCast pass 2 (plugin a97c087bcd28)."""
    W, H, t = body.W, body.H, cfg['groundTilt']
    ytop = H // 4                                             # bottom quarter of the renderer box = texture
    s1 = s2 = None
    for x in range(W):
        y = body.lowest_in_column(x, ytop)
        if y is not None and (s1 is None or y < s1[1]):
            s1 = (x, y)
    if s1 is None:
        return fit_fixed(body, cfg)
    sep = 0.2 * W                                             # 20 % of the TEXTURE width
    for x in range(W):
        if abs(x - s1[0]) < sep:
            continue
        y = body.lowest_in_column(x, ytop)
        if y is not None and (s2 is None or y < s2[1]):
            s2 = (x, y)
    D = cfg['depthRange'] * W
    solved, note = False, 'fallback: no second sole'
    if s2 is not None:
        sa, sb = body.s[s1[1] * W + s1[0]], body.s[s2[1] * W + s2[0]]
        dy, ds = s2[1] - s1[1], sb - sa
        if ds > 0.02 and dy >= 0:
            D = min(max(dy / (ds * t), 0.0), 3.0 * W)
            solved, note = True, f'solved from 1 px each: dy {dy}, ds {ds:.3f}'
        else:
            note = f'fallback: ds {ds:.3f} (map does not show the rear sole further back)'
    return Fit(D, ground_c(body, D, t, ytop), solved, note, s1, s2)


def fit_robust(body, cfg):
    """Soles from the figure's outline, depth = mean over each foot's lowest band (same as the shader), slab middles."""
    W, t = body.W, cfg['groundTilt']
    Wa, Ha = body.bx1 - body.bx0 + 1, body.by1 - body.by0 + 1
    ytop = body.by0 + Ha // 4
    prof = {}
    for x in range(body.bx0, body.bx1 + 1):
        y = body.lowest_in_column(x, ytop)
        if y is not None:
            prof[x] = y
    if not prof:
        return fit_fixed(body, cfg)
    fx = min(prof, key=lambda x: (prof[x], x))
    rear = [x for x in prof if abs(x - fx) >= 0.2 * Wa]
    fallback = fit_fixed(body, cfg)
    if not rear:
        fallback.note = 'fallback: no second sole'
        return fallback
    rx = min(rear, key=lambda x: (prof[x], x))
    front, back = (fx, prof[fx]), (rx, prof[rx])

    def foot(x0, y0):
        ss, hh = [], []
        for need in (2.0, 1.0):
            for y in range(y0, min(body.H, y0 + max(2, round(0.06 * Ha)) + 1)):
                for x in range(max(0, x0 - round(0.04 * Wa)), min(W, x0 + round(0.04 * Wa) + 1)):
                    i = y * W + x
                    if body.mask[i] and body.dist[i] >= need:
                        ss.append(body.s[i]); hh.append(body.h[i])
            if ss:
                break
        return (statistics.fmean(ss), statistics.fmean(hh)) if ss else (0.0, 0.0)

    sF, hF = foot(*front)
    sR, hR = foot(*back)
    dy, ds = back[1] - front[1], sR - sF
    if ds < 0.03:
        fallback.note = f'fallback: ds {ds:.3f} (map does not show the rear foot further back)'
        fallback.front, fallback.rear = front, back
        return fallback
    D = (dy / t - (hR - hF)) / ds
    if not (0.1 * Wa <= D <= 2.0 * Wa):
        fallback.note = f'fallback: solved depth {D:.0f} px outside 0.1-2 x figure width {Wa}'
        fallback.front, fallback.rear = front, back
        return fallback
    c = front[1] - (sF * D + hF) * t
    return Fit(D, c, True, f'solved from foot bands: dy {dy}, ds {ds:.3f}', front, back)


# ---------------------------------------------------------------- voxels, shadow, views

def voxels(body, fit, t):
    """Points of every body slab: (x, y, z, H) with H = height above the ground (underground points dropped, counted)."""
    W = body.W
    out, under = [], 0
    dz_max = 0.75 / max(t, 1e-3)                              # <= 0.75 px of screen per depth step
    for i in range(W * body.H):
        if not body.mask[i]:
            continue
        x, y = i % W, i // W
        z0 = body.s[i] * fit.D
        span = 2.0 * body.h[i]
        n = max(1, math.ceil(span / dz_max))
        for k in range(n + 1):
            z = z0 + span * k / n
            Hh = y - (fit.c + z * t)
            if Hh < -0.5:
                under += 1
                continue
            out.append((x, y, z, max(Hh, 0.0)))
    return out, under


def shadow_mask(vox, fit, cfg, az, canvas, origin, body_h, scale):
    """Forward-splat every point onto the ground; returns an L image of shadow strength (255 = full).
    Step 5, as the game: strength = 1 - falloff x (height / body height incl. a flyer's gap), the strongest point
    wins (the game's march stops at the lowest blocking point); then a 3x3 closing and a Gaussian blur of
    blur px at 1440p (converted with ~1.5 sprite px per 1440p px, the party's on-screen size [approximate])."""
    cw, ch = canvas
    ox, oy = origin
    t = cfg['groundTilt']
    tanE = math.tan(math.radians(min(max(cfg['elevation'], 5.0), 89.0)))
    ca, sa = math.cos(math.radians(az)), math.sin(math.radians(az))
    buf = bytearray(cw * ch)
    Hb = max(1.0, body_h + fit.gap)
    fo = min(max(cfg['falloff'], 0.0), 1.0)
    for x, y, z, Hh in vox:
        L = Hh / tanE
        gx = x + L * ca
        gy = fit.c + z * t + L * sa * t
        px = int(round(gx)) + ox
        py = ch - 1 - (int(round(gy)) + oy)
        if 0 <= px < cw and 0 <= py < ch:
            v = int(255 * (1.0 - fo * min(Hh / Hb, 1.0)))
            i = py * cw + px
            if v > buf[i]:
                buf[i] = v
    img = Image.frombytes('L', (cw, ch), bytes(buf))
    img = img.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.MinFilter(3))
    r = cfg['blur'] * scale * 1.5
    return img.filter(ImageFilter.GaussianBlur(r)) if r >= 0.5 else img


def scene(body, fit, cfg, az, vox):
    t = cfg['groundTilt']
    tanE = math.tan(math.radians(min(max(cfg['elevation'], 5.0), 89.0)))
    Hmax = max((v[3] for v in vox), default=body.H)
    Lmax = Hmax / tanE
    mx = int(Lmax) + 12
    zr = max(abs(fit.D) * 0.6, 4.0) + 2 * body.R
    my = int(Lmax * t + zr * t + max(0.0, -fit.c)) + 12           # a flyer's ground line lies below the sprite
    cw, ch = body.W + 2 * mx, body.H + 2 * my
    img = Image.new('RGB', (cw, ch), GROUND)
    dr = ImageDraw.Draw(img)
    step = max(8, body.W // 10)
    for k in range(-50, 51):                                  # lines of constant ground depth z (screen y = c + z t)
        yy = fit.c + k * step * t
        py = ch - 1 - (int(round(yy)) + my)
        if 0 <= py < ch:
            dr.line([(0, py), (cw, py)], fill=GRID)
    for k in range(-50, 51):                                  # lines of constant x
        px = mx + k * step
        if 0 <= px < cw:
            dr.line([(px, 0), (px, ch)], fill=GRID)
    sm = shadow_mask(vox, fit, cfg, az, (cw, ch), (mx, my), body.H, cfg['scale'])
    dark = Image.new('RGB', (cw, ch), SHADOW)
    img = Image.composite(dark, img, sm.point(lambda p: int(p * cfg['opacity'])))
    img.paste(body.sprite, (mx, my), body.sprite)
    dr = ImageDraw.Draw(img)
    for pt, col in ((fit.front, (255, 40, 40)), (fit.rear, (40, 120, 255)), (fit.ground, (255, 230, 40))):
        if pt:
            px, py = pt[0] + mx, ch - 1 - (int(round(pt[1])) + my)
            dr.ellipse([px - 3, py - 3, px + 3, py + 3], outline=col, width=2)
    hist = sm.histogram()
    sb = sm.getbbox()
    content = (mx, my, mx + body.W, my + body.H)              # sprite box, then grown by the shadow's box
    if sb:
        content = (min(content[0], sb[0]), min(content[1], sb[1]), max(content[2], sb[2]), max(content[3], sb[3]))
    return img, sum(hist) - hist[0], content


def side_view(body, fit, cfg, vox):
    """The reconstruction seen from the side: depth z across (camera on the left), height up; ground line in white."""
    zs = [v[2] for v in vox] or [0.0]
    Hs = [v[3] for v in vox] or [1.0]
    z0, z1 = min(zs), max(zs)
    pad = 10
    cw = int(z1 - z0) + 2 * pad + 1
    ch = int(max(Hs)) + 2 * pad + 1
    buf = [0] * (cw * ch)
    for x, y, z, Hh in vox:
        px = int(z - z0) + pad
        py = ch - 1 - (int(Hh) + pad)
        buf[py * cw + px] += 1
    m = max(buf) or 1
    img = Image.new('RGB', (cw, ch), (30, 30, 34))
    pix = img.load()
    for i, n in enumerate(buf):
        if n:
            g = int(60 + 195 * min(1.0, math.sqrt(n / m) * 2.0))
            pix[i % cw, i // cw] = (g, g, g)
    dr = ImageDraw.Draw(img)
    gy = ch - 1 - pad
    dr.line([(0, gy), (cw, gy)], fill=(255, 255, 255))
    for pt, col in ((fit.front, (255, 40, 40)), (fit.rear, (40, 120, 255))):
        if pt:
            i = pt[1] * body.W + pt[0]
            zc = body.s[i] * fit.D + body.h[i]
            px = int(zc - z0) + pad
            dr.ellipse([px - 3, gy - 3, px + 3, gy + 3], outline=col, width=2)
    dr.text((4, 2), 'side view: near <- depth -> far', fill=(255, 255, 255))
    return img


# ---------------------------------------------------------------- main

def find_inputs(paths):
    out = []
    if not paths:
        for dp in sorted(glob.glob(PARTY_GLOB)):
            out.append((dp[:-len('.depth.png')] + '.png', dp))
        return out
    for p in paths:
        if p.lower().endswith('.depth.png'):
            out.append((p[:-len('.depth.png')] + '.png', p))
        else:
            dp = os.path.splitext(p)[0] + '.depth.png'
            out.append((p, dp if os.path.exists(dp) else None))
    return out


def main():
    cfg = load_config()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('sprites', nargs='*', help='sprite PNGs (default: every party sprite with a .depth.png)')
    ap.add_argument('--fit', choices=('robust', 'legacy', 'fixed'), default='robust')
    ap.add_argument('--az', default='0,90,180,270', help='azimuths, comma-separated')
    ap.add_argument('--scale', type=float, default=0.5, help='sprite scale for the simulation (speed vs detail)')
    ap.add_argument('--elevation', type=float, default=cfg['elevation'])
    ap.add_argument('--tilt', type=float, default=cfg['groundTilt'])
    ap.add_argument('--opacity', type=float, default=cfg['opacity'])
    ap.add_argument('--thickness', type=float, default=cfg['thickness'])
    ap.add_argument('--depthRange', type=float, default=cfg['depthRange'])
    ap.add_argument('--blur', type=float, default=cfg['blur'])
    ap.add_argument('--falloff', type=float, default=cfg['falloff'])
    ap.add_argument('--label', default='', help='appended to the output folder name')
    ap.add_argument('--lift', type=float, default=None, help='flyer: gap from the figure\'s lowest pixel down to the ground, in sprite px at full size')
    ap.add_argument('--flyer', choices=('game', 'centred'), default='centred', help='flyer depth: as the game does now, or centred on the body')
    a = ap.parse_args()
    cfg.update(elevation=a.elevation, groundTilt=max(0.05, a.tilt), opacity=a.opacity, thickness=a.thickness, depthRange=a.depthRange,
               blur=a.blur, falloff=a.falloff, scale=a.scale)
    azs = [int(x) for x in a.az.split(',') if x.strip()]
    inputs = find_inputs(a.sprites)
    if not inputs:
        sys.exit('no sprites found')
    run = time.strftime('%Y%m%d_%H%M%S') + f'_{a.fit}' + (f'_{a.label}' if a.label else '')
    out = os.path.join(OUT_ROOT, run)
    os.makedirs(out, exist_ok=False)                          # a new folder per run: nothing is overwritten
    fitter = {'robust': fit_robust, 'legacy': fit_legacy, 'fixed': fit_fixed}[a.fit]
    lines = [f'run {run}: fit {a.fit}, scale {a.scale}, elevation {cfg["elevation"]}, groundTilt {cfg["groundTilt"]}, '
             f'thickness {cfg["thickness"]}, depthRange {cfg["depthRange"]}, opacity {cfg["opacity"]}, blur {cfg["blur"]}, falloff {cfg["falloff"]}, azimuths {azs}']
    print(lines[0])
    written = 0
    for sp, dp in inputs:
        name = os.path.basename(os.path.dirname(sp)) + '_' + os.path.splitext(os.path.basename(sp))[0]
        t0 = time.time()
        body = Body(sp, dp, a.scale, cfg['thickness'])
        fit = fit_lifted(body, cfg, a.lift * a.scale, a.flyer == 'centred') if a.lift is not None else fitter(body, cfg)
        vox, under = voxels(body, fit, cfg['groundTilt'])
        panels, shadow_px, box = [], [], None
        for az in azs:
            img, n, b = scene(body, fit, cfg, az, vox)
            panels.append(img); shadow_px.append(n)
            box = b if box is None else (min(box[0], b[0]), min(box[1], b[1]), max(box[2], b[2]), max(box[3], b[3]))
        # one crop for all azimuths of this sprite (sprite + every shadow + margin), so the panels compare 1:1
        pad = 16
        box = (max(0, box[0] - pad), max(0, box[1] - pad), min(panels[0].width, box[2] + pad), min(panels[0].height, box[3] + pad))
        panels = [p.crop(box) for p in panels]
        for az, img in zip(azs, panels):
            ImageDraw.Draw(img).text((6, 4), f'azimuth {az}', fill=(255, 255, 255))
            img.save(os.path.join(out, f'{name}_az{az:03d}.png')); written += 1
        side = side_view(body, fit, cfg, vox)
        side.save(os.path.join(out, f'{name}_side.png')); written += 1
        gap = 6
        sw = sum(p.width for p in panels) + side.width + gap * len(panels)
        sh = max([p.height for p in panels] + [side.height]) + 18
        sheet = Image.new('RGB', (sw, sh), (20, 20, 20))
        xo = 0
        for p in panels + [side]:
            sheet.paste(p, (xo, 18)); xo += p.width + gap
        ImageDraw.Draw(sheet).text((4, 3), f'{name}  fit {a.fit}: {fit.note}  D {fit.D:.1f} px ({fit.D / body.W:.2f} x texture width)', fill=(255, 255, 255))
        sheet.save(os.path.join(out, f'{name}_sheet.png')); written += 1
        Wa = body.bx1 - body.bx0 + 1
        line = (f'{name}: {body.W}x{body.H} px, figure {Wa} px wide, {body.pixels} body px, map {"yes" if body.has_map else "no"}; '
                f'front sole {fit.front}, rear sole {fit.rear}; {fit.note}; D {fit.D:.1f} px = {fit.D / body.W:.2f} x texture width '
                f'= {fit.D / Wa:.2f} x figure width; c {fit.c:.1f}; {len(vox)} points, {under} underground; '
                f'shadow px per azimuth {dict(zip(azs, shadow_px))}; {time.time() - t0:.1f} s')
        print(line); lines.append(line)
    with open(os.path.join(out, 'summary.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')
    print(f'{written} image(s) + summary.txt written to {out}')


if __name__ == '__main__':
    main()
