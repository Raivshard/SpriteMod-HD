r"""Read-only dump of a battle background prefab (UnityPy): hierarchy, positions, sprite renderers,
sprite rects/pivots/PPU, animation clip curves, particle systems.
Usage: python bg_prefab_dump.py <bundle path>
"""
import sys
import UnityPy

env = UnityPy.load(sys.argv[1])
objs = {o.path_id: o for o in env.objects}


def rd(pptr):
    try:
        return pptr.read() if pptr and pptr.path_id else None
    except Exception:
        return None


def name_of(pptr):
    d = rd(pptr)
    return getattr(d, 'm_Name', '?') if d else None


# sprites
print('== Sprites')
for o in env.objects:
    if o.type.name == 'Sprite':
        s = o.read()
        r = s.m_Rect
        print(f'  {s.m_Name}: rect x{r.x:g} y{r.y:g} w{r.width:g} h{r.height:g}  pivot {s.m_Pivot.x:g},{s.m_Pivot.y:g}  ppu {s.m_PixelsToUnits:g}  tex {name_of(s.m_RD.texture)}')
print('== Textures')
for o in env.objects:
    if o.type.name == 'Texture2D':
        t = o.read()
        print(f'  {t.m_Name}: {t.m_Width}x{t.m_Height} format {t.m_TextureFormat} filter {t.m_TextureSettings.m_FilterMode}')

# hierarchy
print('== Hierarchy (local position / scale)')
trs = [o.read() for o in env.objects if o.type.name == 'Transform']
roots = [t for t in trs if not t.m_Father or not t.m_Father.path_id]


def walk(t, depth):
    go = t.m_GameObject.read()
    p, s = t.m_LocalPosition, t.m_LocalScale
    comps = []
    for c in go.m_Component:
        cp = c.component if hasattr(c, 'component') else c
        co = objs.get(cp.path_id)
        if not co:
            continue
        tn = co.type.name
        if tn == 'Transform':
            continue
        extra = ''
        try:
            d = co.read()
            if tn == 'SpriteRenderer':
                extra = f"(sprite {name_of(d.m_Sprite)}, order {d.m_SortingOrder}, color {d.m_Color.r:.2f},{d.m_Color.g:.2f},{d.m_Color.b:.2f},{d.m_Color.a:.2f}, mat {[name_of(m) for m in d.m_Materials]})"
            elif tn == 'Animator':
                extra = f"(controller {name_of(d.m_Controller)})"
            elif tn == 'MonoBehaviour':
                extra = f"(script {name_of(d.m_Script)})"
            elif tn == 'ParticleSystemRenderer':
                extra = f"(mat {[name_of(m) for m in d.m_Materials]}, order {d.m_SortingOrder})"
        except Exception as e:
            extra = f'(read error {e})'
        comps.append(tn + extra)
    print(f"{'  ' * depth}{go.m_Name}  active={go.m_IsActive}  pos {p.x:g},{p.y:g},{p.z:g}  scale {s.x:g},{s.y:g}  {'; '.join(comps)}")
    for ch in t.m_Children:
        walk(ch.read(), depth + 1)


for r in roots:
    walk(r, 0)

# clips
print('== Animation clips')
for o in env.objects:
    if o.type.name != 'AnimationClip':
        continue
    c = o.read()
    print(f'  {c.m_Name}: sampleRate {c.m_SampleRate}  wrap {c.m_WrapMode}  loop {getattr(c.m_AnimationClipSettings, "m_LoopTime", "?")}  stop {c.m_AnimationClipSettings.m_StopTime}')
    for k in ('m_PositionCurves', 'm_ScaleCurves', 'm_EulerCurves', 'm_FloatCurves'):
        for cv in getattr(c, k, []) or []:
            keys = cv.curve.m_Curve
            print(f'   {k} path={cv.path!r} attr={getattr(cv, "attribute", "")!r}: ' +
                  ', '.join(f't{kf.time:.3f}={kf.value}' for kf in keys[:24]) + (' ...' if len(keys) > 24 else ''))
    for cv in getattr(c, 'm_PPtrCurves', []) or []:
        print(f'   PPtr path={cv.path!r} attr={cv.attribute!r}: ' + ', '.join(f't{kf.time:.3f}={name_of(kf.value)}' for kf in cv.curve))
    # editor curves may be stripped; show the binding count of the muscle clip as a hint
    try:
        b = c.m_ClipBindingConstant.genericBindings
        print(f'   bindings: {len(b)}')
    except Exception:
        pass

# particle systems (texture sheet animation, shape, emission)
print('== Particle systems')
for o in env.objects:
    if o.type.name != 'ParticleSystem':
        continue
    raw = o.read_typetree()
    go = objs[raw['m_GameObject']['m_PathID']].read().m_Name
    m = raw.get('moveWithTransform')
    ts = raw.get('UVModule', {})
    sh = raw.get('ShapeModule', {})
    em = raw.get('EmissionModule', {})
    ini = raw.get('InitialModule', {})
    print(f"  {go}: duration {raw.get('lengthInSec')} loop {raw.get('looping')} simSpace {m}")
    print(f"    UV enabled {ts.get('enabled')} mode {ts.get('mode')} tiles {ts.get('tilesX')}x{ts.get('tilesY')} sprites {[s.get('sprite', {}).get('m_PathID') for s in ts.get('sprites', [])]}")
    print(f"    shape enabled {sh.get('enabled')} type {sh.get('type')} radius {sh.get('radius', {}).get('value') if isinstance(sh.get('radius'), dict) else sh.get('radius')} pos {sh.get('m_Position')} scale {sh.get('m_Scale')}")
    print(f"    emission rate {em.get('rateOverTime', {}).get('scalar')} startSize {ini.get('startSize', {}).get('scalar')} startLifetime {ini.get('startLifetime', {}).get('scalar')} maxParticles {ini.get('maxNumParticles')}")
