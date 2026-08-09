import re, struct, collections, uuid, shutil, math

SRC = "Assets/Resources/Animations/Player/JumpUp.anim"
DST = "Assets/Resources/Animations/Player/JumpDown.anim"
FPS = 30
SPLIT = 25
T_SPLIT = SPLIT / FPS

# ---------- float formatting (shortest string that round-trips to float32, like Unity's "R") ----------
def fmt(v):
    v32 = struct.unpack('f', struct.pack('f', float(v)))[0]
    if v32 == 0.0:
        return "0"
    for prec in range(1, 10):
        s = f"{v32:.{prec}g}"
        try:
            if struct.unpack('f', struct.pack('f', float(s)))[0] == v32:
                return s
        except ValueError:
            pass
    return f"{v32:.9g}"

COMP_RE = re.compile(r'([xyzw]):\s*([-0-9.eE]+)')
def parse_vec(s):
    return {m.group(1): float(m.group(2)) for m in COMP_RE.finditer(s)}
def vec_keys(vec):
    return [k for k in ('x','y','z','w') if k in vec]
def fmt_vec(vec, keys):
    return "{" + ", ".join(f"{k}: {fmt(vec[k])}" for k in keys) + "}"

IND_KF = "      "       # 6 spaces (list item)
IND_F  = "        "     # 8 spaces (field)
def make_kf_block(time_str, value, slope, keys):
    w = {k: 0.33333334 for k in keys}
    return [
        f"{IND_KF}- serializedVersion: 3",
        f"{IND_F}time: {time_str}",
        f"{IND_F}value: {fmt_vec(value, keys)}",
        f"{IND_F}inSlope: {fmt_vec(slope, keys)}",
        f"{IND_F}outSlope: {fmt_vec(slope, keys)}",
        f"{IND_F}tangentMode: 0",
        f"{IND_F}weightedMode: 0",
        f"{IND_F}inWeight: {fmt_vec(w, keys)}",
        f"{IND_F}outWeight: {fmt_vec(w, keys)}",
    ]

def set_block_time(block, new_time_str):
    out = []
    for bl in block:
        s = bl.strip()
        if s.startswith("time:"):
            indent = bl[:len(bl) - len(bl.lstrip())]
            out.append(f"{indent}time: {new_time_str}")
        else:
            out.append(bl)
    return out

# ---------- Hermite evaluation (Unity AnimationCurve semantics) ----------
def eval_curve(kfs, tt):
    # returns (value_dict, slope_dict) at time tt; slope = dv/dt
    if tt <= kfs[0]['t']:
        v = dict(kfs[0]['value']); return v, {k: 0.0 for k in v}
    if tt >= kfs[-1]['t']:
        v = dict(kfs[-1]['value']); return v, {k: 0.0 for k in v}
    for i in range(len(kfs) - 1):
        a, b = kfs[i], kfs[i+1]
        if a['t'] <= tt <= b['t']:
            dx = b['t'] - a['t']
            keys = vec_keys(a['value'])
            if dx <= 0:
                v = dict(a['value']); return v, {k: 0.0 for k in v}
            tn = (tt - a['t']) / dx
            val, slope = {}, {}
            finite = all(math.isfinite(a['value'][k]) and math.isfinite(b['value'][k])
                         and math.isfinite(a['outSlope'][k]) and math.isfinite(b['inSlope'][k]) for k in keys)
            for k in keys:
                p0 = a['value'][k]; p1 = b['value'][k]
                if not finite:
                    val[k] = p0 + (p1 - p0) * tn; slope[k] = 0.0; continue
                m0 = a['outSlope'][k] * dx   # normalized tangent at a
                m1 = b['inSlope'][k] * dx    # normalized tangent at b
                h00 = 2*tn**3 - 3*tn**2 + 1;   h10 = tn**3 - 2*tn**2 + tn
                h01 = -2*tn**3 + 3*tn**2;      h11 = tn**3 - tn**2
                val[k] = h00*p0 + h10*m0 + h01*p1 + h11*m1
                d00 = 6*tn**2 - 6*tn;          d10 = 3*tn**2 - 4*tn + 1
                d01 = -6*tn**2 + 6*tn;         d11 = 3*tn**2 - 2*tn
                slope[k] = (d00*p0 + d10*m0 + d01*p1 + d11*m1) / dx
            return val, slope
    v = dict(kfs[-1]['value']); return v, {k: 0.0 for k in v}

# ---------- parse ----------
with open(SRC, encoding="utf-8") as f:
    content = f.read()
lines = content.split("\n")
if lines and lines[-1] == "":
    lines = lines[:-1]

i_rot = next(i for i, l in enumerate(lines) if l.startswith("  m_RotationCurves:"))
i_sample = next(i for i, l in enumerate(lines) if l.startswith("  m_SampleRate:"))
prefix = lines[:i_rot]
curves_lines = lines[i_rot:i_sample]
suffix = lines[i_sample:]

tokens = []
i, n = 0, len(curves_lines)
while i < n:
    l = curves_lines[i]
    if l.startswith("  - curve:"):
        entry = {"head": [], "keyframes": [], "tail": []}
        while i < n and not curves_lines[i].startswith("      m_Curve:"):
            entry["head"].append(curves_lines[i]); i += 1
        if i < n:
            entry["head"].append(curves_lines[i]); i += 1   # "      m_Curve:"
        while i < n and curves_lines[i].startswith("      - serializedVersion: 3"):
            block = [curves_lines[i]]; i += 1
            while i < n and curves_lines[i].startswith("        "):
                block.append(curves_lines[i]); i += 1
            ts = val = inS = outS = None
            for bl in block:
                s = bl.strip()
                if s.startswith("time:"):      ts = s[len("time:"):].strip()
                elif s.startswith("value:"):   val = parse_vec(s)
                elif s.startswith("inSlope:"): inS = parse_vec(s)
                elif s.startswith("outSlope:"):outS = parse_vec(s)
            t = float(ts)
            entry["keyframes"].append({"ts": ts, "t": t, "frame": int(round(t*FPS)),
                                       "value": val, "inSlope": inS, "outSlope": outS, "block": block})
        while i < n and not curves_lines[i].startswith("  - curve:") and not curves_lines[i].startswith("  m_"):
            entry["tail"].append(curves_lines[i]); i += 1
        tokens.append(("entry", entry))
    else:
        tokens.append(("header", l)); i += 1

# canonical time string per frame (most common original representation)
fc = collections.defaultdict(collections.Counter)
for typ, data in tokens:
    if typ == "entry":
        for kf in data["keyframes"]:
            fc[kf["frame"]][kf["ts"]] += 1
T = {f: c.most_common(1)[0][0] for f, c in fc.items()}

stats = {"up_insert": 0, "down_insert": 0, "down_held": 0}

def build_entry(entry, mode):
    kfs = entry["keyframes"]
    out = list(entry["head"])
    has_at = any(kf["frame"] == SPLIT for kf in kfs)
    has_after = any(kf["frame"] > SPLIT for kf in kfs)
    if mode == "up":
        for kf in kfs:
            if kf["frame"] <= SPLIT:
                out.extend(kf["block"])           # original time preserved
        if has_after and not has_at:
            val, slope = eval_curve(kfs, T_SPLIT)
            out.extend(make_kf_block(T[SPLIT], val, slope, vec_keys(val)))
            stats["up_insert"] += 1
    else:  # down
        if not has_at:
            val, slope = eval_curve(kfs, T_SPLIT)
            out.extend(make_kf_block(T[0], val, slope, vec_keys(val)))   # time 0, first
            stats["down_insert"] += 1
            if not has_after:
                stats["down_held"] += 1
        for kf in kfs:
            if kf["frame"] >= SPLIT:
                out.extend(set_block_time(kf["block"], T[kf["frame"] - SPLIT]))
    out.extend(entry["tail"])
    return out

def replace_line(lines, match, new_line):
    return [new_line if l.startswith(match) else l for l in lines]

# JumpUp
up_prefix = prefix
up_suffix = replace_line(suffix, "    m_StopTime:", f"    m_StopTime: {T[SPLIT]}")
up_curves = []
for typ, data in tokens:
    if typ == "header": up_curves.append(data)
    else: up_curves.extend(build_entry(data, "up"))
up_out = up_prefix + up_curves + up_suffix

# JumpDown
down_prefix = replace_line(prefix, "  m_Name:", "  m_Name: JumpDown")
down_suffix = replace_line(suffix, "    m_StopTime:", f"    m_StopTime: {T[57 - SPLIT]}")
down_curves = []
for typ, data in tokens:
    if typ == "header": down_curves.append(data)
    else: down_curves.extend(build_entry(data, "down"))
down_out = down_prefix + down_curves + down_suffix

# backup original, then write
shutil.copy(SRC, "_JumpUp_anim.bak")
with open(SRC, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(up_out) + "\n")
with open(DST, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(down_out) + "\n")

# meta for JumpDown (new GUID)
with open(SRC + ".meta", encoding="utf-8") as f:
    meta = f.read()
new_guid = uuid.uuid4().hex
meta_out = re.sub(r"^guid: [0-9a-f]+", f"guid: {new_guid}", meta, flags=re.MULTILINE)
with open(DST + ".meta", "w", encoding="utf-8", newline="\n") as f:
    f.write(meta_out)

# verify round-trip parse of written files
def count_kf(path):
    with open(path, encoding="utf-8") as f:
        return f.read().count("- serializedVersion: 3")
def stop_time(path):
    with open(path, encoding="utf-8") as f:
        m = re.search(r"m_StopTime: ([0-9.]+)", f.read())
    return m.group(1) if m else "?"

print(f"JumpUp : keyframes={count_kf(SRC):4d}  stop={stop_time(SRC)}  inserted_at_25={stats['up_insert']}")
print(f"JumpDown: keyframes={count_kf(DST):4d}  stop={stop_time(DST)}  inserted_at_0={stats['down_insert']} (held={stats['down_held']})")
print(f"new guid: {new_guid}")

# sanity: show an inserted keyframe value vs neighbours for one spanning curve
for typ, data in tokens:
    if typ != "entry": continue
    kfs = data["keyframes"]
    if any(kf["frame"] > SPLIT for kf in kfs) and not any(kf["frame"] == SPLIT for kf in kfs):
        v, s = eval_curve(kfs, T_SPLIT)
        prev = max((kf for kf in kfs if kf["frame"] < SPLIT), key=lambda k: k["frame"])
        nxt  = min((kf for kf in kfs if kf["frame"] > SPLIT), key=lambda k: k["frame"])
        path = next((l.strip()[len("path: "):] for l in data["tail"] if l.strip().startswith("path:")), "?")
        k = vec_keys(v)[0]
        print(f"sample '{path}' {k}: prev(f{prev['frame']})={fmt(prev['value'][k])} -> eval25={fmt(v[k])} -> next(f{nxt['frame']})={fmt(nxt['value'][k])}")
        break
