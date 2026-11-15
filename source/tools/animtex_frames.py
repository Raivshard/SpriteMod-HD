r"""Cut a battle background's animated pieces (one PNG per game sprite) out of its _Animtex sheet.

The piece rects come from the background's bundle (UnityPy, read-only). Output names = the game's sprite names
(BG_FF5_12_A_0.png ...), which is what the plugin looks for in battle_backgrounds/animtex/.
Use: template frames for the artist (scale 1 = exact game size) or test frames (scale 4, nearest).

Usage: python animtex_frames.py <bundle> <out dir> [scale | sx,sy] [--overwrite]
scale: whole number = nearest-neighbour; sx,sy (e.g. 4.339,4.414 for a stretched test background) = Lanczos.
Refuses to overwrite existing files unless --overwrite. Prints each piece's rect and output size.
"""
import os
import sys
import UnityPy
from PIL import Image

args = [a for a in sys.argv[1:] if a != '--overwrite']
overwrite = '--overwrite' in sys.argv
bundle, out = args[0], args[1]
sc = args[2] if len(args) > 2 else '1'
sx, sy = (float(v) for v in sc.split(',')) if ',' in sc else (float(sc), float(sc))
env = UnityPy.load(bundle)
os.makedirs(out, exist_ok=True)
sheets = {}
n = 0
for o in env.objects:
    if o.type.name != 'Sprite':
        continue
    s = o.read()
    tex = s.m_RD.texture.read()
    if 'Animtex' not in tex.m_Name:
        continue
    if tex.m_Name not in sheets:
        sheets[tex.m_Name] = tex.image.convert('RGBA')   # PIL image, top row first
    img = sheets[tex.m_Name]
    r = s.m_Rect
    x, w, h = int(r.x), int(r.width), int(r.height)
    top = img.height - int(r.y) - h                        # Unity rect y is from the bottom
    piece = img.crop((x, top, x + w, top + h))
    if (sx, sy) != (1, 1):
        exact = sx == sy and sx == int(sx)
        piece = piece.resize((round(w * sx), round(h * sy)), Image.NEAREST if exact else Image.LANCZOS)
    dst = os.path.join(out, s.m_Name + '.png')
    if os.path.exists(dst) and not overwrite:
        print('exists, skipped', dst)
        continue
    piece.save(dst)
    n += 1
    print(f'{s.m_Name}: rect x{x} y{int(r.y)} {w}x{h} (sheet {tex.m_Name} {img.width}x{img.height}) -> {piece.width}x{piece.height}')
print(f'{n} file(s) written to {out}')
