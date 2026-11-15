r"""Reformat hand-edited JSON so Notepad++ shows each item's identifying line when folded.

1. A line that is only "{" (inside the file, not the root) is joined with the next key line:
       {                         { "name": "battle menu",
         "name": "battle menu",    "path": ...
2. Section header blocks
       { "________________________": "", "_   BATTLE   _": "", ..., "________________________": "" }
   get a titled first key:  "______ B A T T L E _____"  (letters spaced, words 3 spaces apart,
   underscores pad to at least 24 characters; text in (...) is left out of the title).

Only whitespace and the header's first key change. The script checks that the parsed data is
identical apart from those header keys and refuses to write otherwise.

Usage:  python json_fold_format.py [--write] file1.json [file2.json ...]
Without --write it is a dry run and prints what would change.
"""
import json, re, sys

HDR_RULE = re.compile(r'^"_{6,}"\s*:\s*""\s*,?\s*$')          # "________": ""
HDR_NAME = re.compile(r'^"_\s+([^\s_].*?)\s+_"\s*:\s*""\s*,?\s*$')  # "_   NAME   _": ""
HDR_TITLED = re.compile(r'^"_+ \S.* _+"\s*:\s*""\s*,?\s*$')       # already titled


def title(name):
    name = re.sub(r'\s*\(.*?\)\s*', ' ', name).strip()
    spaced = '   '.join(' '.join(w) for w in name.upper().split())
    width = max(24, len(spaced) + 8)
    pad = width - len(spaced) - 2
    left = (pad + 1) // 2
    return '_' * left + ' ' + spaced + ' ' + '_' * (pad - left)


MAX_LINE = 67   # user's split-screen width: header box lines stay within this (2026-11-09)
BOX_ROW = re.compile(r'^(\s*)"(_+|_ +_|_ +([^\s_].*?) +_)"(\s*:\s*""\s*,?\s*)$')


def fit_boxes(lines):
    """Header box rows ("____", "_    _", "_  TITLE  _") longer than MAX_LINE are narrowed so the line
    (indent + key + ': "",') fits; the title is centred (extra space on the right)."""
    fitted = 0
    for i, ln in enumerate(lines):
        m = BOX_ROW.match(ln)
        if not m or len(ln) <= MAX_LINE:
            continue
        w = MAX_LINE - len(m.group(1)) - 2 - len(': "",')
        key, t = m.group(2), m.group(3)
        if t:
            pad = w - 2 - len(t)
            if pad < 2:
                continue   # title too long to fit: leave it
            key = '_' + ' ' * (pad // 2) + t + ' ' * (pad - pad // 2) + '_'
        elif key.startswith('_ '):
            key = '_' + ' ' * (w - 2) + '_'
        else:
            key = '_' * w
        lines[i] = m.group(1) + '"' + key + '"' + m.group(4)
        fitted += 1
    return fitted


def reformat(text):
    nl = '\r\n' if '\r\n' in text else '\n'
    lines = text.split(nl)
    fit_boxes(lines)
    out, i, joined, titled = [], 0, 0, 0
    while i < len(lines):
        ln = lines[i]
        m = re.match(r'^(\s*)\{\s*$', ln)
        if m and i > 0 and i + 1 < len(lines) and lines[i + 1].lstrip().startswith('"'):
            nxt = lines[i + 1].strip()
            if HDR_RULE.match(nxt):
                # find the header's name line inside this block
                j = i + 2
                while j < len(lines) and not lines[j].strip().startswith('}'):
                    hm = HDR_NAME.match(lines[j].strip())
                    if hm:
                        comma = ',' if nxt.rstrip().endswith(',') else ''
                        nxt = '"' + title(hm.group(1)) + '": ""' + comma
                        titled += 1
                        break
                    j += 1
            out.append(m.group(1) + '{ ' + nxt)
            joined += 1
            i += 2
            continue
        # already joined header with the plain rule as first key: add the title
        m2 = re.match(r'^(\s*)\{ (".*)$', ln)
        if m2 and HDR_RULE.match(m2.group(2)):
            j = i + 1
            while j < len(lines) and not lines[j].strip().startswith('}'):
                hm = HDR_NAME.match(lines[j].strip())
                if hm:
                    comma = ',' if m2.group(2).rstrip().endswith(',') else ''
                    ln = m2.group(1) + '{ "' + title(hm.group(1)) + '": ""' + comma
                    titled += 1
                    break
                j += 1
        out.append(ln)
        i += 1
    return nl.join(out), joined, titled


def pairs(o):
    return [(k, v) for k, v in o]


def normal(text):
    """Parsed data without header keys (keys of only underscores/spaces/caps decoration, value "")."""
    def hook(ps):
        return [(k, v) for k, v in ps if not (v == '' and re.match(r'^_.*_$', k))]
    return json.loads(text, object_pairs_hook=hook)


def main():
    args = sys.argv[1:]
    write = '--write' in args
    files = [a for a in args if a != '--write']
    changed = 0
    for f in files:
        raw = open(f, 'rb').read()
        bom = raw.startswith(b'\xef\xbb\xbf')
        text = raw.decode('utf-8-sig')
        new, joined, titled = reformat(text)
        if new == text:
            print(f'unchanged  {f}')
            continue
        if normal(new) != normal(text):
            print(f'REFUSED (data would change)  {f}')
            continue
        changed += 1
        print(f'{"written" if write else "would change"}  {f}  ({joined} lines joined, {titled} headers titled)')
        if write:
            open(f, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + new.encode('utf-8'))
    print(f'{changed} file(s) {"changed" if write else "to change"}')


if __name__ == '__main__':
    main()
