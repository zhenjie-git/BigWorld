# -*- coding: utf-8 -*-
import json, os, struct, subprocess

ROOT = r"D:\UnityStudy\BigWorld"
os.chdir(ROOT)

def varint(n):
    out = bytearray()
    n &= (1 << 64) - 1
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)

def tag(field, wire):
    return varint((field << 3) | wire)

def enc_float(field, v):
    if v == 0:
        return b""
    return tag(field, 5) + struct.pack('<f', v)

def enc_double(field, v):
    return tag(field, 1) + struct.pack('<d', v)

def enc_int64(field, v):
    if v == 0:
        return b""
    return tag(field, 0) + varint(v)

def enc_string(field, s):
    if not s:
        return b""
    data = s.encode('utf-8')
    return tag(field, 2) + varint(len(data)) + data

def enc_msg(field, body):
    return tag(field, 2) + varint(len(body)) + body

def enc_packed_double(field, values):
    body = b"".join(struct.pack('<d', v) for v in values)
    return tag(field, 2) + varint(len(body)) + body

def enc_repeated_string(field, values):
    return b"".join(enc_string(field, v) for v in values if v)

def enc_repeated_msg(field, bodies):
    return b"".join(enc_msg(field, b) for b in bodies)

# ---------- PlayerConfig ----------
cam = lambda e: (enc_float(1, e["minimum_angle"]) + enc_float(2, e["maximum_angle"])
                 + enc_float(3, e["wait_time"]) + enc_float(4, e["recentering_time"]))
pc = json.load(open('GameConfig/Player/player_config.json', encoding='utf-8'))
client_pc = json.load(open('BigWorldClient/Assets/Resources/Config/PlayerConfig.json', encoding='utf-8'))

SERVER_ONLY_KEYS = {"collider_radius", "step_height_percentage", "camera_backwards", "camera_sideways"}

def build_player(src, server):
    body = b""
    for i, key in enumerate(["sprint_to_run_time","fall_speed_limit","gravity","collider_height",
                             "collider_center_y","collider_radius","step_height_percentage","voxel_max_step_height"], start=1):
        if server and key in SERVER_ONLY_KEYS:
            continue
        if not server and key not in src:
            continue
        body += enc_float(i, float(src[key]))
    if not server:
        body += enc_repeated_msg(9, [cam(e) for e in src["camera_backwards"]])
        body += enc_repeated_msg(10, [cam(e) for e in src["camera_sideways"]])
    return body

open('GameConfig/Player/player_config.bytes', 'wb').write(build_player(pc, server=True))
open('BigWorldClient/Assets/Resources/Config/PlayerConfig.bytes', 'wb').write(build_player(client_pc, server=False))

# ---------- StateConfig ----------
sc = json.load(open('BigConfig_tmp'.replace('BigConfig_tmp', 'GameConfig/StateConfig/state_config.json'), encoding='utf-8'))["entries"]
client_sc = json.load(open('BigWorldClient/Assets/Resources/Config/StateConfig.json', encoding='utf-8'))["entries"]

def build_state_entry(e, server):
    body = enc_string(1, e["state"])
    if not server:
        body += enc_string(2, e["animation_name"])
    body += enc_string(3, e["curve_x"]) + enc_string(4, e["curve_y"]) + enc_string(5, e["curve_z"])
    body += enc_float(6, float(e["duration_seconds"]))
    if not server:
        body += enc_int64(7, int(e["total_ticks"]))
        body += enc_int64(8, int(e["loop"]))
    else:
        body += enc_float(9, float(e["max_per_frame"]))
    return body

server_body = enc_repeated_msg(1, [build_state_entry(e, True) for e in sc])
client_body = enc_repeated_msg(1, [build_state_entry(e, False) for e in client_sc])
open('GameConfig/StateConfig/state_config.bytes', 'wb').write(server_body)
open('BigWorldClient/Assets/Resources/Config/StateConfig.bytes', 'wb').write(client_body)

# ---------- TransitionTable ----------
tt = json.load(open('GameConfig/StateTransitionTable/state_transition_table.json', encoding='utf-8'))["entries"]
def build_trans_entry(e):
    return enc_string(1, e["source"]) + enc_repeated_string(2, e["allowed_targets"])
tt_body = enc_repeated_msg(1, [build_trans_entry(e) for e in tt])
open('GameConfig/StateTransitionTable/state_transition_table.bytes', 'wb').write(tt_body)
open('BigWorldClient/Assets/Resources/Config/StateTransitionTable.bytes', 'wb').write(tt_body)

# ---------- Curves ----------
count = 0
for root, dirnames, filenames in os.walk('GameConfig/Curves'):
    for fn in filenames:
        if not fn.endswith('.json'):
            continue
        p = os.path.join(root, fn)
        d = json.load(open(p, encoding='utf-8'))["keys"]
        body = enc_packed_double(1, [k["t"] for k in d]) + enc_packed_double(2, [k["v"] for k in d])
        open(p[:-5] + '.bytes', 'wb').write(body)
        rel = os.path.relpath(p[:-5] + '.bytes', 'GameConfig').replace('\\', '/')
        dst = os.path.join('BigWorldClient/Assets/Resources', rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        open(dst, 'wb').write(body)
        count += 1
print("bytes written: player_config, state_config, transition_table, curves x%d" % count)

# ---------- 验证: protoc --decode ----------
PROTO = ["-I", "BigWorldServer/Common/proto", "--decode=config.PlayerConfigMsg", "config.proto"]
def decode(msg, path):
    r = subprocess.run(["BigWorldServer/.tools/protoc/bin/protoc.exe", "-I", "BigWorldServer/Common/proto",
                        "--decode=" + msg, "config.proto"],
                       stdin=open(path, 'rb'), capture_output=True)
    assert r.returncode == 0, r.stderr.decode()[:300]
    return r.stdout.decode()

out = decode("config.PlayerConfigMsg", "GameConfig/Player/player_config.bytes")
assert "sprint_to_run_time: 1" in out and "camera_backwards" not in out
out2 = decode("config.PlayerConfigMsg", "BigWorldClient/Assets/Resources/Config/PlayerConfig.bytes")
assert "camera_backwards" in out2 and "wait_time: -1" in out2
out3 = decode("config.StateConfigMsg", "GameConfig/StateConfig/state_config.bytes")
assert "state: \"Walking\"" in out3 and "curve_x: \"Curves/Walk/Walk_x\"" in out3 and "animation_name" not in out3
out4 = decode("config.CurveMsg", "GameConfig/Curves/Walk/Walk_x.bytes")
assert "t: 0" in out4 and "v:" in out4
print("protoc --decode round-trip verified OK")
