r"""Fit a battle background's animated pieces to an HD background (no resampling of any frame).

1. Optional: cut the game's frames out of an upscaled _Animtex sheet (any uniform scale; crop only, pixel values
   unchanged) into <out dir>/<sprite name>.png. Skip with --no-cut when the frames are already there (artist frames).
2. For every animated renderer in the background's prefab, find where its first frame sits on the HD background
   (best colour match, coarse-to-fine search), and write <out dir>/<BG>_place.json:
     "pieces": { renderer: [x, y] }  top-left in HD-background px (what the plugin uses for the frames)
     "source": [x, y, w, h]           part of the original background the HD image shows, fitted from the pieces
                                      (the plugin uses it for everything else, e.g. sparks).
   Frames must be at the HD background's pixel scale (frameScale 1).

Usage: python animtex_fit.py <bundle> <HD background png> <out dir> [--sheet <upscaled sheet png>] [--feather N] [--overwrite]
--feather: alpha fade at frame edges in HD px (default 16; per piece: edit "featherPieces" in the json).
Prints each piece's match score (mean colour difference 0-255; under ~5 = clean match).
"""
import json
import os
import sys
import UnityPy
from PIL import Image, ImageChops, ImageStat

args = sys.argv[1:]
overwrite = '--overwrite' in args
sheet_path = args[args.index('--sheet') + 1] if '--sheet' in args else None
feather = int(args[args.index('--feather') + 1]) if '--feather' in args else 16
pos = [a for i, a in enumerate(args) if not a.startswith('--') and (i == 0 or args[i - 1] not in ('--sheet', '--feather'))]
bundle, bg_path, out = pos[0], pos[1], pos[2]
os.makedirs(out, exist_ok=True)

env = UnityPy.load(bundle)
objs = {o.path_id: o for o in env.objects}
bg_hd = Image.open(bg_path).convert('RGB')
W, H = bg_hd.size

# original background size, and the animated renderers with their default sprite + local position
orig_w = orig_h = None
pieces = []   # (renderer name, sprite, local pos)
sprites = []
for o in env.objects:
    if o.type.name == 'Sprite':
        sprites.append(o.read())
for o in env.objects:
    if o.type.name != 'SpriteRenderer':
        continue
    d = o.read()
    sp = d.m_Sprite.read() if d.m_Sprite.path_id else None
    go = d.m_GameObject.read()
    tr = [objs[c.component.path_id if hasattr(c, 'component') else c.path_id] for c in go.m_Component]
    tr = [t.read() for t in tr if t.type.name == 'Transform'][0]
    if sp is None:
        continue
    tex = sp.m_RD.texture.read()
    if sp.m_Name == tex.m_Name and 'Animtex' not in tex.m_Name and not tex.m_Name.endswith('_A'):
        orig_w, orig_h = tex.m_Width, tex.m_Height          # the background itself
    else:
        pieces.append((go.m_Name, sp, tr.m_LocalPosition))
if orig_w is None:
    sys.exit('background sprite not found in bundle')
bg_name = os.path.basename(bundle).split('_assets')[0].upper()
print(f'{bg_name}: original {orig_w}x{orig_h}, HD background {W}x{H}, {len(pieces)} animated renderer(s)')

# 1. cut frames from the upscaled sheet (crop only)
if sheet_path:
    sh = Image.open(sheet_path).convert('RGBA')
    sheet_names = {s.m_RD.texture.read().m_Name for s in sprites}
    sheet_orig = [s.m_RD.texture.read() for s in sprites if 'Animtex' in s.m_RD.texture.read().m_Name or s.m_RD.texture.read().m_Name.endswith('_A')][0]
    k = sh.height / sheet_orig.m_Height
    kx = sh.width / sheet_orig.m_Width
    if abs(k - kx) > 0.01:
        print(f'warning: sheet scale x{kx:.4f} vs y{k:.4f} not uniform; using y')
    for s in sprites:
        t = s.m_RD.texture.read()
        if t.m_Name != sheet_orig.m_Name:
            continue
        r = s.m_Rect
        box = (round(r.x * k), round((t.m_Height - r.y - r.height) * k), round((r.x + r.width) * k), round((t.m_Height - r.y) * k))
        dst = os.path.join(out, s.m_Name + '.png')
        if os.path.exists(dst) and not overwrite:
            print('exists, skipped', dst)
            continue
        sh.crop(box).save(dst)
        print(f'  cut {s.m_Name}: sheet px {box} -> {box[2] - box[0]}x{box[3] - box[1]}')


def diff(f, x, y):
    return sum(ImageStat.Stat(ImageChops.difference(bg_hd.crop((x, y, x + f.width, y + f.height)), f)).mean) / 3


# 2. match each renderer's first frame
place = {}
fits = []   # (orig top-left x, y, hd x, y, scale)
for rname, sp, p in pieces:
    fp = os.path.join(out, sp.m_Name + '.png')
    if not os.path.exists(fp):
        print(f'  {rname}: no frame {sp.m_Name}.png, skipped')
        continue
    f = Image.open(fp).convert('RGB')
    r = sp.m_Rect
    scale = f.height / r.height
    # original top-left of the piece (px from the background's top-left); prefab units = original px, centre 0,0
    ox = p.x + orig_w / 2 - sp.m_Pivot.x * r.width
    oy = orig_h / 2 - p.y - (1 - sp.m_Pivot.y) * r.height
    gx, gy = ox * scale, oy * scale
    step = 8
    cands = [(diff(f, x, y), x, y) for x in range(max(0, int(gx - W * 0.15)), min(W - f.width, int(gx + W * 0.15)) + 1, step)
             for y in range(max(0, int(gy - H * 0.15)), min(H - f.height, int(gy + H * 0.15)) + 1, step)]
    if not cands:
        print(f'  {rname}: frame does not fit inside the background')
        continue
    best = min(cands)
    while step > 1:
        step //= 2
        best = min((diff(f, x, y), x, y) for x in range(max(0, best[1] - step * 2), min(W - f.width, best[1] + step * 2) + 1, step)
                   for y in range(max(0, best[2] - step * 2), min(H - f.height, best[2] + step * 2) + 1, step))
    place[rname] = [best[1], best[2]]
    fits.append((ox, oy, best[1], best[2], scale))
    print(f'  {rname} ({sp.m_Name}, {f.width}x{f.height}): top-left {best[1]},{best[2]}  match {best[0]:.2f}')

src = None
if fits:
    k = sum(f[4] for f in fits) / len(fits)
    bx = sum(f[2] - k * f[0] for f in fits) / len(fits)
    by = sum(f[3] - k * f[1] for f in fits) / len(fits)
    src = [round(-bx / k, 2), round(-by / k, 2), round(W / k, 2), round(H / k, 2)]
    print(f'  fitted source {src} (scale x{k:.4f}); residuals: ' +
          ', '.join(f'{f[2] - (k * f[0] + bx):+.1f},{f[3] - (k * f[1] + by):+.1f}' for f in fits))
dst = os.path.join(out, bg_name + '_place.json')
if os.path.exists(dst) and not overwrite:
    sys.exit('exists, not written: ' + dst)
body = {'_comment': f'Written by tools/animtex_fit.py for {os.path.basename(bg_path)} ({W}x{H}).', 'source': src, 'pieces': place}
with open(dst, 'w', encoding='utf-8') as fh:
    fh.write('{ "_comment": ' + json.dumps(body['_comment']) + ',\n  "source": ' + json.dumps(src) + ',\n  "feather": ' + str(feather) + ',\n  "featherPieces": {},\n  "pieces": {\n' +
             ',\n'.join(f'    "{k2}": {json.dumps(v)}' for k2, v in place.items()) + '\n  }\n}\n')
print('written', dst)
