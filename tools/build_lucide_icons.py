"""Flatten the vendored Lucide 24px SVGs for the native ImGui renderer.

Run with: python tools/build_lucide_icons.py
The generated resource is embedded in KITN.dll; no font atlas or GPU texture needed.
"""
import math
import re
import struct
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "KITN" / "Assets" / "Icons"

def arc(p, end, rx, ry, rotation, large, sweep):
    rx, ry = abs(rx), abs(ry)
    if not rx or not ry or p == end:
        return [end]
    angle = math.radians(rotation)
    co, si = math.cos(angle), math.sin(angle)
    dx, dy = (p[0] - end[0]) / 2, (p[1] - end[1]) / 2
    x, y = co * dx + si * dy, -si * dx + co * dy
    factor = x*x/(rx*rx) + y*y/(ry*ry)
    if factor > 1:
        rx *= math.sqrt(factor)
        ry *= math.sqrt(factor)
    value = max(0, (rx*rx*ry*ry-rx*rx*y*y-ry*ry*x*x)/(rx*rx*y*y+ry*ry*x*x))
    k = (-1 if large == sweep else 1) * math.sqrt(value)
    cx, cy = k * rx * y / ry, -k * ry * x / rx
    center = (co*cx-si*cy+(p[0]+end[0])/2, si*cx+co*cy+(p[1]+end[1])/2)
    start = math.atan2((y-cy)/ry, (x-cx)/rx)
    finish = math.atan2((-y-cy)/ry, (-x-cx)/rx)
    delta = (finish - start) % (2 * math.pi)
    if not sweep:
        delta -= 2 * math.pi
    count = max(4, math.ceil(abs(delta) * max(rx, ry) * 2))
    result = []
    for i in range(1, count+1):
        t = start + delta * i / count
        result.append((center[0]+co*rx*math.cos(t)-si*ry*math.sin(t), center[1]+si*rx*math.cos(t)+co*ry*math.sin(t)))
    result[-1] = end
    return result

def path(d):
    tokens = re.findall(r"[a-zA-Z]|[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?", d)
    i, command, previous = 0, None, None
    p, start, control = (0, 0), (0, 0), (0, 0)
    lines, line = [], []
    while i < len(tokens):
        if tokens[i].isalpha():
            command = tokens[i]
            i += 1
        kind, relative = command.upper(), command.islower()
        if kind == "Z":
            line.append(start)
            p = start
            previous = kind
            command = None
            continue
        sizes = {"M":2,"L":2,"H":1,"V":1,"C":6,"S":4,"Q":4,"T":2,"A":7}
        n = sizes[kind]
        v = list(map(float, tokens[i:i+n]))
        if len(v) != n:
            raise ValueError(d)
        i += n
        def point(x, y):
            return (x+p[0], y+p[1]) if relative else (x, y)
        if kind in ("M", "L"):
            end = point(*v)
            if kind == "M":
                if line:
                    lines.append(line)
                line, start = [end], end
                command = "l" if relative else "L"
            else:
                line.append(end)
        elif kind in ("H", "V"):
            end = ((p[0]+v[0] if relative else v[0]), p[1]) if kind == "H" else (p[0], (p[1]+v[0] if relative else v[0]))
            line.append(end)
        elif kind == "A":
            end = point(v[5], v[6])
            line.extend(arc(p, end, *v[:5]))
        else:
            if kind == "C":
                c1, c2, end = point(*v[:2]), point(*v[2:4]), point(*v[4:6])
            elif kind == "S":
                c1 = (2*p[0]-control[0], 2*p[1]-control[1]) if previous in ("C", "S") else p
                c2, end = point(*v[:2]), point(*v[2:4])
            else:
                q = point(*v[:2]) if kind == "Q" else ((2*p[0]-control[0], 2*p[1]-control[1]) if previous in ("Q", "T") else p)
                end = point(*v[-2:])
                c1 = (p[0]+(q[0]-p[0])*2/3, p[1]+(q[1]-p[1])*2/3)
                c2 = (end[0]+(q[0]-end[0])*2/3, end[1]+(q[1]-end[1])*2/3)
            for step in range(1, 17):
                t, u = step/16, 1-step/16
                line.append(tuple(u**3*p[a]+3*u*u*t*c1[a]+3*u*t*t*c2[a]+t**3*end[a] for a in (0,1)))
            control = c2 if kind in ("C", "S") else q
        p, previous = end, kind
    if line:
        lines.append(line)
    return lines

icons = {}
for source in sorted(ROOT.glob("*.svg")):
    lines = []
    for e in ET.parse(source).getroot():
        tag = e.tag.split("}")[-1]
        def a(name, default=0):
            return float(e.attrib.get(name, default))
        if tag == "path":
            lines += path(e.attrib["d"])
        elif tag == "circle":
            lines.append([(a("cx")+a("r")*math.cos(i*math.pi/32), a("cy")+a("r")*math.sin(i*math.pi/32)) for i in range(65)])
        elif tag == "line":
            lines.append([(a("x1"), a("y1")), (a("x2"), a("y2"))])
        elif tag in ("polyline", "polygon"):
            values = list(map(float, re.findall(r"[-\d.]+", e.attrib["points"])))
            line = list(zip(values[::2], values[1::2]))
            lines.append(line + [line[0]] if tag == "polygon" else line)
        elif tag == "rect":
            x,y,w,h,r = a("x"),a("y"),a("width"),a("height"),a("rx")
            r = min(r,w/2,h/2)
            if r:
                lines += path(f"M{x+r} {y}H{x+w-r}A{r} {r} 0 0 1 {x+w} {y+r}V{y+h-r}A{r} {r} 0 0 1 {x+w-r} {y+h}H{x+r}A{r} {r} 0 0 1 {x} {y+h-r}V{y+r}A{r} {r} 0 0 1 {x+r} {y}Z")
            else:
                lines.append([(x,y),(x+w,y),(x+w,y+h),(x,y+h),(x,y)])
        else:
            raise ValueError(f"Unsupported {tag} in {source}")
    icons[source.stem] = lines

with (ROOT / "lucide.bin").open("wb") as f:
    f.write(struct.pack("<i", len(icons)))
    for name, lines in icons.items():
        encoded = name.encode("ascii")
        f.write(bytes([len(encoded)]))  # BinaryReader string length, names <128 bytes.
        f.write(encoded)
        f.write(struct.pack("<i", len(lines)))
        for line in lines:
            f.write(struct.pack("<i", len(line)))
            for x,y in line:
                f.write(struct.pack("<ff", x,y))
print(f"Generated {len(icons)} Lucide icons")
