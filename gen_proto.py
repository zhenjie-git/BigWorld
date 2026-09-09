# -*- coding: utf-8 -*-
import json, os, subprocess, zipfile

ROOT = r"D:\UnityStudy\BigWorld"
os.chdir(os.path.join(ROOT, "BigWorldServer"))

TYPE_MAP = {"float": "float", "int": "int64", "string": "string", "bool": "bool",
            "string_array": "repeated string", "recentering": "repeated CameraRecenteringEntry"}

def read_sheet(xlsx, sheet):
    z = zipfile.ZipFile(xlsx)
    wb = z.read('xl/workbook.xml').decode('utf-8')
    rels = z.read('xl/_rels/workbook.xml.rels').decode('utf-8')
    import re
    sheets = re.findall(r'<sheet name="([^"]+)"[^>]*r:id="(rId\d+)"', wb)
    rel_map = dict(re.findall(r'Id="(rId\d+)"[^>]*Target="(worksheets/[^"]+)"', rels))
    target = dict(sheets)[sheet]
    xml = z.read('xl/' + rel_map[target]).decode('utf-8')
    rows = {}
    for m in re.finditer(r'<row r="(\d+)">(.*?)</row>', xml):
        cells = {}
        for c in re.finditer(r'<c r="([A-Z]+)(\d+)" t="inlineStr"[^>]*><is><t>(.*?)</t></is></c>', m.group(2)):
            cells[c.group(1)] = c.group(3)
        rows[int(m.group(1))] = cells
    return rows

def table_columns(xlsx, sheet):
    rows = read_sheet(xlsx, sheet)
    names, types = rows.get(1, {}), rows.get(2, {})
    cols = []
    for col in sorted(names, key=lambda c: (len(c), c)):
        if names.get(col):
            cols.append((names[col], types.get(col, "")))
    return cols

def col_letter(n):
    letters = ""
    while n > 0:
        n -= 1
        letters = chr(ord('A') + n % 26) + letters
        n //= 26
    return letters

gc = os.path.join(ROOT, "GameConfig")
player_cols = table_columns(os.path.join(gc, "Player/player_config.xlsx"), "server_params")
state_cols = table_columns(os.path.join(gc, "StateConfig/state_config.xlsx"), "state_config")
trans_cols = table_columns(os.path.join(gc, "StateTransitionTable/state_transition_table.xlsx"), "state_transition_table")

is_matrix = (len(trans_cols) > 0 and trans_cols[0][0] == "source"
             and all(t == "bool" for _, t in trans_cols[1:]))

def pascal(name):
    return ''.join(p.capitalize() for p in name.split('_'))

def emit_message(name, cols, base_no=1):
    lines = ["message %s {" % name]
    for i, (cname, ctype) in enumerate(cols):
        lines.append("  %s %s = %d;" % (TYPE_MAP[ctype], cname, base_no + i))
    lines.append("}")
    return lines

proto = []
proto.append('syntax = "proto3";')
proto.append('')
proto.append('package config;')
proto.append('')
proto.append('option go_package = "bigworld/common/pb";')
proto.append('option csharp_namespace = "BigWorldClient.Network.Protocol";')
proto.append('')
proto.append('message CameraRecenteringEntry {')
proto.append('  float minimum_angle = 1;')
proto.append('  float maximum_angle = 2;')
proto.append('  float wait_time = 3;')
proto.append('  float recentering_time = 4;')
proto.append('}')
proto.append('')
proto += emit_message("PlayerConfigMsg", player_cols)
proto.append('')
proto += emit_message("StateConfigEntry", state_cols)
proto.append('message StateConfigMsg {')
proto.append('  repeated StateConfigEntry entries = 1;')
proto.append('}')
proto.append('')
if is_matrix:
    proto.append('message TransitionTableMsg {')
    proto.append('  repeated TransitionEntry entries = 1;')
    proto.append('}')
    proto.append('')
    proto.append('message TransitionEntry {')
    proto.append('  string source = 1;')
    proto.append('  repeated string allowed_targets = 2;')
    proto.append('}')
proto.append('message CurveMsg {')
proto.append('  repeated double t = 1;')
proto.append('  repeated double v = 2;')
proto.append('}')
proto.append('')

text = "\n".join(proto)
out = "Common/proto/config.proto"
open(out, "w", encoding="utf-8", newline="\n").write(text)
print(text)
print("written:", out)
