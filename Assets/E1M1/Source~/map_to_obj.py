"""Convert a Quake 1 .map (standard format) into a Wavefront OBJ + MTL for Unity.

The geometry follows what Quake's own compiler (qbsp) did: CSG between the brushes of each entity, removal of
faces that face the void, and T-junction fixing, so the output has no z-fighting and no crack-prone edges.
Point entities the Unity scene needs (lights, view points) are written to a JSON file for the scene builder.

Usage: python map_to_obj.py <map> <texture dir> <output dir> <json path>
In this repository: python map_to_obj.py E1M1.MAP ../Textures .. e1m1_entities.json
"""
import collections
import json
import math
import os
import re
import shutil
import struct
import sys

UNITY_UNITS_PER_QUAKE_UNIT = 1.0 / 32.0
EPSILON = 0.01
WELD_STEPS_PER_UNIT = 1024.0
CHUNK_SIZE = 512.0
PROBE_OFFSET = 0.5
SEAM_TOLERANCE = 0.001
GRID_CELL = 64.0
EYE_HEIGHT = 22.0
DEFAULT_LIGHT = 300.0
LIGHT_INTENSITY_UNIT = 300.0

SOLID, LIQUID, SKY = "solid", "liquid", "sky"


def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def mul(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def length(a): return math.sqrt(dot(a, a))
def normalize(a): return mul(a, 1.0 / length(a))
def lerp(a, b, t): return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)
def centroid(points): return mul((sum(p[0] for p in points), sum(p[1] for p in points), sum(p[2] for p in points)), 1.0 / len(points))


def to_unity(p): return (p[0] * UNITY_UNITS_PER_QUAKE_UNIT, p[2] * UNITY_UNITS_PER_QUAKE_UNIT, p[1] * UNITY_UNITS_PER_QUAKE_UNIT)
def to_unity_direction(n): return (n[0], n[2], n[1])


# ---------------------------------------------------------------- parsing

FACE_PATTERN = re.compile(
    r'\(\s*(\S+)\s+(\S+)\s+(\S+)\s*\)\s*\(\s*(\S+)\s+(\S+)\s+(\S+)\s*\)\s*\(\s*(\S+)\s+(\S+)\s+(\S+)\s*\)'
    r'\s*(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)')


class Face:
    def __init__(self, normal, distance, texture, offset, rotation, scale):
        self.normal = normal
        self.distance = distance
        self.texture = texture
        self.offset = offset
        self.rotation = rotation
        self.scale = scale


class Brush:
    def __init__(self, faces, order):
        self.faces = faces
        self.order = order
        first = faces[0].texture
        if first.startswith("*"):
            self.contents = LIQUID
        elif first.startswith("sky"):
            self.contents = SKY
        else:
            self.contents = SOLID
        self.polygons = []
        for face in faces:
            polygon = base_polygon(face.normal, face.distance)
            for other in faces:
                if other is not face and polygon:
                    polygon = clip_behind(polygon, other.normal, other.distance)
            if len(polygon) >= 3:
                self.polygons.append((polygon, face))
        points = [p for polygon, _ in self.polygons for p in polygon]
        self.mins = tuple(min(p[k] for p in points) for k in range(3))
        self.maxs = tuple(max(p[k] for p in points) for k in range(3))
        self.center = mul(add(self.mins, self.maxs), 0.5)


def parse_map(path):
    lines = [line.strip() for line in open(path, encoding="latin-1").read().splitlines()]
    lines = [line for line in lines if line and not line.startswith("//")]
    entities = []
    i = 0
    while i < len(lines):
        assert lines[i] == "{", "Expected an entity at line %d" % i
        keys, brushes = {}, []
        i += 1
        while lines[i] != "}":
            if lines[i].startswith('"'):
                key, value = re.findall(r'"([^"]*)"', lines[i])
                keys[key] = value
                i += 1
                continue
            assert lines[i] == "{"
            faces = []
            i += 1
            while lines[i] != "}":
                g = FACE_PATTERN.match(lines[i]).groups()
                p0, p1, p2 = [tuple(float(x) for x in g[k:k + 3]) for k in (0, 3, 6)]
                normal = normalize(cross(sub(p0, p1), sub(p2, p1)))
                faces.append(Face(normal, dot(p1, normal), g[9].lower(),
                                  (float(g[10]), float(g[11])), float(g[12]), (float(g[13]), float(g[14]))))
                i += 1
            brushes.append(faces)
            i += 1
        entities.append((keys, brushes))
        i += 1
    return entities


# ---------------------------------------------------------------- polygon clipping

def base_polygon(normal, distance):
    axis = max(range(3), key=lambda k: abs(normal[k]))
    up = (0.0, 0.0, 1.0) if axis != 2 else (1.0, 0.0, 0.0)
    up = normalize(sub(up, mul(normal, dot(up, normal))))
    right = cross(up, normal)
    origin = mul(normal, distance)
    up, right = mul(up, 65536.0), mul(right, 65536.0)
    return [add(sub(origin, right), up), add(add(origin, right), up), sub(add(origin, right), up), sub(sub(origin, right), up)]


def clip_behind(polygon, normal, distance):
    front, back, on = split(polygon, normal, distance)
    return [] if on else (back or [])


def split(polygon, normal, distance):
    """Returns (front, back, on_plane). Points within EPSILON of the plane go to both sides."""
    distances = [dot(p, normal) - distance for p in polygon]
    if all(abs(d) < EPSILON for d in distances):
        return None, None, True
    if all(d > -EPSILON for d in distances):
        return polygon, None, False
    if all(d < EPSILON for d in distances):
        return None, polygon, False
    front, back = [], []
    count = len(polygon)
    for i in range(count):
        p, dp = polygon[i], distances[i]
        q, dq = polygon[(i + 1) % count], distances[(i + 1) % count]
        if dp >= -EPSILON:
            front.append(p)
        if dp <= EPSILON:
            back.append(p)
        if (dp > EPSILON and dq < -EPSILON) or (dp < -EPSILON and dq > EPSILON):
            middle = lerp(p, q, dp / (dp - dq))
            front.append(middle)
            back.append(middle)
    return (front if len(front) >= 3 else None), (back if len(back) >= 3 else None), False


def polygon_area(polygon, normal):
    total = (0.0, 0.0, 0.0)
    for i in range(len(polygon)):
        total = add(total, cross(polygon[i], polygon[(i + 1) % len(polygon)]))
    return abs(dot(total, normal)) * 0.5


# ---------------------------------------------------------------- CSG (qbsp's CSGFaces rules)

def boxes_touch(a, b):
    return all(a.mins[k] <= b.maxs[k] + EPSILON and a.maxs[k] >= b.mins[k] - EPSILON for k in range(3))


def csg(brushes):
    """Cuts away every face fragment that lies inside another brush of the same entity.

    A fragment lying exactly on a face of the other brush is cut when the two faces point in opposite directions
    (touching brushes) or when the other brush comes later in the file (later brush wins coplanar ties).
    Faces of solid brushes are never cut by water or sky, so walls stay visible underwater.
    """
    kept = []
    for b1 in brushes:
        outside = [(polygon, face) for polygon, face in b1.polygons]
        for b2 in brushes:
            if b2 is b1 or not boxes_touch(b1, b2):
                continue
            later = b2.order > b1.order
            inside, outside = outside, []
            for face2 in b2.faces:
                still_inside = []
                for polygon, face in inside:
                    front, back, on = split(polygon, face2.normal, face2.distance)
                    if on:
                        if dot(face.normal, face2.normal) < 0 or later:
                            still_inside.append((polygon, face))
                        else:
                            outside.append((polygon, face))
                        continue
                    if front:
                        outside.append((front, face))
                    if back:
                        still_inside.append((back, face))
                inside = still_inside
            if b1.contents == SOLID and b2.contents != SOLID:
                outside.extend(inside)
        kept.extend((polygon, face, b1) for polygon, face in outside)
    return kept


# ---------------------------------------------------------------- void removal and its check

class BrushGrid:
    def __init__(self, brushes):
        self.brushes = brushes
        self.cells = collections.defaultdict(list)
        for index, brush in enumerate(brushes):
            lo = [int(math.floor(brush.mins[k] / GRID_CELL)) for k in range(3)]
            hi = [int(math.floor(brush.maxs[k] / GRID_CELL)) for k in range(3)]
            for x in range(lo[0], hi[0] + 1):
                for y in range(lo[1], hi[1] + 1):
                    for z in range(lo[2], hi[2] + 1):
                        self.cells[(x, y, z)].append(index)
        self.mins = tuple(min(b.mins[k] for b in brushes) for k in range(3))
        self.maxs = tuple(max(b.maxs[k] for b in brushes) for k in range(3))

    def segment_blocked(self, a, b):
        """True if the segment a->b passes through the inside of any brush."""
        d = sub(b, a)
        cell = [int(math.floor(a[k] / GRID_CELL)) for k in range(3)]
        step, t_max, t_delta = [0] * 3, [math.inf] * 3, [math.inf] * 3
        for k in range(3):
            if d[k] > 0:
                step[k] = 1
                t_max[k] = ((cell[k] + 1) * GRID_CELL - a[k]) / d[k]
                t_delta[k] = GRID_CELL / d[k]
            elif d[k] < 0:
                step[k] = -1
                t_max[k] = (cell[k] * GRID_CELL - a[k]) / d[k]
                t_delta[k] = -GRID_CELL / d[k]
        tested = set()
        t = 0.0
        while t <= 1.0:
            for index in self.cells.get(tuple(cell), ()):
                if index not in tested:
                    tested.add(index)
                    if segment_inside_brush(a, d, self.brushes[index]):
                        return True
            axis = min(range(3), key=lambda k: t_max[k])
            t = t_max[axis]
            cell[axis] += step[axis]
            t_max[axis] += t_delta[axis]
        return False


def segment_inside_brush(a, d, brush):
    """Brushes are grown by SEAM_TOLERANCE so a segment running exactly along the seam between two touching
    brushes counts as blocked; on Quake's integer grid that happens constantly."""
    t0, t1 = 0.0, 1.0
    for face in brush.faces:
        dn = dot(face.normal, d)
        room = face.distance + SEAM_TOLERANCE - dot(face.normal, a)
        if abs(dn) < 1e-12:
            if room < 0:
                return False
            continue
        t = room / dn
        if dn > 0:
            t1 = min(t1, t)
        else:
            t0 = max(t0, t)
        if t1 - t0 <= 1e-6:
            return False
    return True


AXES = [(1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, -1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, -1.0)]


def faces_void(grid, polygon, face):
    """A face faces the void if a ray from just in front of it escapes the level along some axis.

    The level is sealed by solid and sky brushes, so a point inside it can never escape; only points outside can.
    """
    probe = add(centroid(polygon), mul(face.normal, PROBE_OFFSET))
    reach = 2.0 * max(grid.maxs[k] - grid.mins[k] for k in range(3))
    return any(not grid.segment_blocked(probe, add(probe, mul(axis, reach))) for axis in AXES)


def check_removed_faces_are_invisible(grid, removed, view_points):
    """Every removed face must be hidden from every view point inside the level."""
    visible = []
    for polygon, face, _ in removed:
        middle = centroid(polygon)
        samples = [middle] + [lerp(p, middle, 0.25) for p in polygon[:4]]
        samples = [add(s, mul(face.normal, PROBE_OFFSET)) for s in samples]
        for view in view_points:
            if dot(face.normal, view) - face.distance <= 0:
                continue
            if any(not grid.segment_blocked(view, s) for s in samples):
                visible.append((face.texture, [round(c) for c in middle], [round(c) for c in view]))
                break
    return visible


# ---------------------------------------------------------------- welding, T-junctions, triangulation

def weld(polygons):
    """Snaps every vertex to a 1/1024 unit grid and shares identical positions, so shared corners are bit-identical."""
    ids, positions, welded = {}, [], []
    for polygon, face, brush in polygons:
        indices = []
        for p in polygon:
            key = tuple(int(round(c * WELD_STEPS_PER_UNIT)) for c in p)
            if key not in ids:
                ids[key] = len(positions)
                positions.append(tuple(k / WELD_STEPS_PER_UNIT for k in key))
            index = ids[key]
            if not indices or indices[-1] != index:
                indices.append(index)
        while len(indices) > 1 and indices[0] == indices[-1]:
            indices.pop()
        if len(indices) >= 3 and polygon_area([positions[i] for i in indices], face.normal) > 1e-4:
            welded.append((indices, face, brush))
    return positions, welded


def fix_t_junctions(positions, polygons):
    """Inserts into each edge every vertex that lies on it, so neighbouring faces share their edges exactly."""
    cells = collections.defaultdict(list)
    for index, p in enumerate(positions):
        cells[tuple(int(math.floor(c / GRID_CELL)) for c in p)].append(index)
    edge_cache = {}
    inserted = 0
    fixed = []
    for indices, face, brush in polygons:
        result = []
        for i in range(len(indices)):
            a, b = indices[i], indices[(i + 1) % len(indices)]
            result.append(a)
            key = (min(a, b), max(a, b))
            if key not in edge_cache:
                edge_cache[key] = vertices_on_segment(positions, cells, key[0], key[1])
            between = edge_cache[key] if a == key[0] else list(reversed(edge_cache[key]))
            result.extend(between)
            inserted += len(between)
        fixed.append((result, face, brush))
    return fixed, inserted


def vertices_on_segment(positions, cells, a, b):
    pa, pb = positions[a], positions[b]
    d = sub(pb, pa)
    length_squared = dot(d, d)
    lo = [int(math.floor((min(pa[k], pb[k]) - EPSILON) / GRID_CELL)) for k in range(3)]
    hi = [int(math.floor((max(pa[k], pb[k]) + EPSILON) / GRID_CELL)) for k in range(3)]
    found = []
    for x in range(lo[0], hi[0] + 1):
        for y in range(lo[1], hi[1] + 1):
            for z in range(lo[2], hi[2] + 1):
                for c in cells.get((x, y, z), ()):
                    if c == a or c == b:
                        continue
                    pc = positions[c]
                    t = dot(sub(pc, pa), d) / length_squared
                    if t <= 1e-6 or t >= 1 - 1e-6:
                        continue
                    if length(sub(pc, add(pa, mul(d, t)))) < EPSILON:
                        found.append((t, c))
    return [c for _, c in sorted(found)]


def distance_to_line(p, a, b):
    d = sub(b, a)
    return length(cross(sub(p, a), d)) / length(d)


def has_collinear_vertices(points):
    count = len(points)
    return any(distance_to_line(points[i], points[i - 1], points[(i + 1) % count]) < EPSILON for i in range(count))


def newell_normal(points):
    total = (0.0, 0.0, 0.0)
    for i in range(len(points)):
        total = add(total, cross(points[i], points[(i + 1) % len(points)]))
    return total


def merge_key(face, brush):
    s_axis, t_axis = texture_axes(face)
    return (tuple(round(c, 5) for c in face.normal), round(face.distance, 3), face.texture, brush.contents,
            tuple(round(c, 6) for c in s_axis + t_axis), face.offset)


def merge_coplanar(positions, polygons):
    """Merges neighbouring fragments that share a plane and texture alignment while the result stays convex,
    as qbsp did after CSG. Without it, CSG's cuts would multiply the triangle count."""
    groups = collections.defaultdict(list)
    for item in polygons:
        groups[merge_key(item[1], item[2])].append(item)
    merged_all = []
    for items in groups.values():
        normal = items[0][1].normal
        alive = {i: list(item[0]) for i, item in enumerate(items)}
        edges = {}
        for i, indices in alive.items():
            for k in range(len(indices)):
                edges[(indices[k], indices[(k + 1) % len(indices)])] = i
        work = list(alive)
        while work:
            i = work.pop()
            if i not in alive:
                continue
            p = alive[i]
            for k in range(len(p)):
                a, b = p[k], p[(k + 1) % len(p)]
                j = edges.get((b, a))
                if j is None or j == i or j not in alive:
                    continue
                result = try_merge(p, alive[j], a, b, positions, normal)
                if result is None:
                    continue
                for poly in (p, alive[j]):
                    for m in range(len(poly)):
                        edges.pop((poly[m], poly[(m + 1) % len(poly)]), None)
                del alive[j]
                alive[i] = result
                for m in range(len(result)):
                    edges[(result[m], result[(m + 1) % len(result)])] = i
                work.append(i)
                break
        for i, indices in alive.items():
            merged_all.append((indices, items[i][1], items[i][2]))
    return merged_all


def try_merge(p, q, a, b, positions, normal):
    i = p.index(a)
    j = q.index(b)
    p_seq = p[i + 1:] + p[:i + 1]
    q_seq = q[j + 1:] + q[:j + 1]
    merged = p_seq + q_seq[1:-1]
    if len(set(merged)) != len(merged):
        return None
    orientation = 1.0 if dot(newell_normal([positions[v] for v in p]), normal) > 0 else -1.0
    result = []
    count = len(merged)
    for m in range(count):
        prev, cur, nxt = positions[merged[m - 1]], positions[merged[m]], positions[merged[(m + 1) % count]]
        if distance_to_line(cur, prev, nxt) < EPSILON:
            if dot(sub(cur, prev), sub(nxt, cur)) <= 0:
                return None
            if merged[m] in (a, b):
                continue
        elif dot(cross(sub(cur, prev), sub(nxt, cur)), normal) * orientation < 0:
            return None
        result.append(merged[m])
    return result if len(result) >= 3 else None


def is_corner(points, ring, k):
    return distance_to_line(points[ring[k]], points[ring[k - 1]], points[ring[(k + 1) % len(ring)]]) >= EPSILON


def triangulate(points):
    """Splits a convex polygon into n - 2 triangles, none of them degenerate.

    A plain fan would give zero-area triangles wherever T-junction fixing put vertices in the middle of an edge,
    so ears are clipped only at real corners, and only when what remains still has area. If no such ear exists,
    the polygon fans around its centre instead, which costs two extra triangles."""
    count = len(points)
    if not has_collinear_vertices(points):
        return points, [(0, i, i + 1) for i in range(1, count - 1)]
    ring = list(range(count))
    triangles = []
    while len(ring) > 3:
        for k in range(len(ring)):
            rest = ring[:k] + ring[k + 1:]
            if is_corner(points, ring, k) and any(is_corner(points, rest, r) for r in range(len(rest))):
                triangles.append((ring[k - 1], ring[k], ring[(k + 1) % len(ring)]))
                ring = rest
                break
        else:
            break
    if len(ring) == 3 and is_corner(points, ring, 1):
        return points, triangles + [tuple(ring)]
    return points + [centroid(points)], [(count, i, (i + 1) % count) for i in range(count)]


# ---------------------------------------------------------------- output checks

def count_coplanar_overlaps(positions, polygons):
    """Pairs of polygons on the same plane, facing the same way, overlapping with positive area: z-fighting."""
    groups = collections.defaultdict(list)
    for indices, face, brush in polygons:
        groups[(tuple(round(c, 4) for c in face.normal), round(face.distance, 2))].append([positions[i] for i in indices])
    overlaps = 0
    for (normal, _), items in groups.items():
        for i in range(len(items)):
            for j in range(i + 1, len(items)):
                clipped = items[i]
                for k in range(len(items[j])):
                    a, b = items[j][k], items[j][(k + 1) % len(items[j])]
                    inward = normalize(cross(normal, sub(b, a)))
                    if dot(inward, sub(centroid(items[j]), a)) < 0:
                        inward = mul(inward, -1.0)
                    clipped = clip_behind(clipped, mul(inward, -1.0), -dot(inward, a)) if clipped else []
                if clipped and polygon_area(clipped, normal) > 0.5:
                    overlaps += 1
    return overlaps


def count_t_junctions(positions, polygons):
    cells = collections.defaultdict(list)
    used = {i for indices, _, _ in polygons for i in indices}
    for index in used:
        cells[tuple(int(math.floor(c / GRID_CELL)) for c in positions[index])].append(index)
    edges = {(min(a, b), max(a, b)) for indices, _, _ in polygons for a, b in zip(indices, indices[1:] + indices[:1])}
    return sum(len(vertices_on_segment(positions, cells, a, b)) for a, b in edges)


def count_visible_open_edges(positions, polygons, grid, view_points):
    """Edges with no neighbour running the opposite way and no polygon behind them, seen from inside the level.

    An open edge is harmless when it ends on the middle of another face (a wall rising into a sky brush) or when it
    sits in a void pocket the axis rays could not escape from; anywhere else it would be a visible hole."""
    directed = {(a, b) for indices, _, _ in polygons for a, b in zip(indices, indices[1:] + indices[:1])}
    cells = collections.defaultdict(list)
    for number, (indices, _, _) in enumerate(polygons):
        points = [positions[i] for i in indices]
        lo = [int(math.floor((min(p[k] for p in points) - EPSILON) / GRID_CELL)) for k in range(3)]
        hi = [int(math.floor((max(p[k] for p in points) + EPSILON) / GRID_CELL)) for k in range(3)]
        for x in range(lo[0], hi[0] + 1):
            for y in range(lo[1], hi[1] + 1):
                for z in range(lo[2], hi[2] + 1):
                    cells[(x, y, z)].append(number)

    def lies_on_other_polygon(owner, p):
        for number in cells.get(tuple(int(math.floor(c / GRID_CELL)) for c in p), ()):
            indices, face, _ = polygons[number]
            if number != owner and abs(dot(face.normal, p) - face.distance) < 0.05:
                points = [positions[i] for i in indices]
                orientation = dot(newell_normal(points), face.normal)
                if all(dot(cross(sub(points[(k + 1) % len(points)], points[k]), sub(p, points[k])), face.normal) * orientation >= -1e-6
                       for k in range(len(points))):
                    return True
        return False

    visible = 0
    for owner, (indices, face, _) in enumerate(polygons):
        middle = centroid([positions[i] for i in indices])
        for a, b in zip(indices, indices[1:] + indices[:1]):
            if (b, a) in directed:
                continue
            if all(lies_on_other_polygon(owner, lerp(positions[a], positions[b], t)) for t in (0.25, 0.5, 0.75)):
                continue
            samples = [lerp(positions[a], positions[b], t) for t in (0.1, 0.3, 0.5, 0.7, 0.9)]
            samples = [add(add(s, mul(normalize(sub(middle, s)), 0.25)), mul(face.normal, 0.25)) for s in samples]
            if any(dot(face.normal, v) - face.distance > 0 and not grid.segment_blocked(v, s) for v in view_points for s in samples):
                visible += 1
    return visible


# ---------------------------------------------------------------- texture projection (qbsp's QuakeEd rules)

BASE_AXES = [
    ((0, 0, 1), (1, 0, 0), (0, -1, 0)),
    ((0, 0, -1), (1, 0, 0), (0, -1, 0)),
    ((1, 0, 0), (0, 1, 0), (0, 0, -1)),
    ((-1, 0, 0), (0, 1, 0), (0, 0, -1)),
    ((0, 1, 0), (1, 0, 0), (0, 0, -1)),
    ((0, -1, 0), (1, 0, 0), (0, 0, -1)),
]


def texture_axes(face):
    best, best_index = 0.0, 0
    for index, (axis, _, _) in enumerate(BASE_AXES):
        d = dot(face.normal, axis)
        if d > best:
            best, best_index = d, index
    s_axis, t_axis = list(BASE_AXES[best_index][1]), list(BASE_AXES[best_index][2])
    rotation = {0.0: (0.0, 1.0), 90.0: (1.0, 0.0), 180.0: (0.0, -1.0), 270.0: (-1.0, 0.0)}.get(face.rotation)
    sin_r, cos_r = rotation if rotation else (math.sin(math.radians(face.rotation)), math.cos(math.radians(face.rotation)))
    sv = 0 if s_axis[0] else (1 if s_axis[1] else 2)
    tv = 0 if t_axis[0] else (1 if t_axis[1] else 2)
    for axis in (s_axis, t_axis):
        s, t = axis[sv], axis[tv]
        axis[sv] = cos_r * s - sin_r * t
        axis[tv] = sin_r * s + cos_r * t
    scale_s = face.scale[0] or 1.0
    scale_t = face.scale[1] or 1.0
    return tuple(c / scale_s for c in s_axis), tuple(c / scale_t for c in t_axis)


def texture_uvs(face, points, size):
    s_axis, t_axis = texture_axes(face)
    uvs = [((dot(p, s_axis) + face.offset[0]) / size[0], -(dot(p, t_axis) + face.offset[1]) / size[1]) for p in points]
    shift_u = math.floor(min(u for u, _ in uvs))
    shift_v = math.floor(min(v for _, v in uvs))
    return [(u - shift_u, v - shift_v) for u, v in uvs]


# ---------------------------------------------------------------- textures

def texture_file_name(texture, texture_dir):
    base = texture.replace("*", "star_").replace("+", "plus_")
    for candidate in (base, base + "_fbr"):
        if os.path.exists(os.path.join(texture_dir, candidate + ".png")):
            return candidate + ".png"
    raise FileNotFoundError("No PNG for texture '%s' in %s" % (texture, texture_dir))


def material_name(texture):
    return texture.replace("*", "star_").replace("+", "plus_")


def png_size(path):
    data = open(path, "rb").read(24)
    return struct.unpack(">II", data[16:24])


# ---------------------------------------------------------------- movers

def start_offset(classname, keys, brushes):
    """Where a mover starts in the scene, relative to where the map draws it, in Quake units.

    Doors are placed open and platforms lowered, with the rules of Quake's own game code, so the level has a clear
    path without any door logic. func_door (doors.qc) moves along its angle by its size minus `lip`; a door flagged
    START_OPEN spawns in that same moved position. func_plat (plats.qc) spawns `height`, or its own height minus 8,
    below where it is drawn unless something targets it. Secret doors and buttons stay where they are drawn."""
    size = sub(tuple(max(b.maxs[k] for b in brushes) for k in range(3)), tuple(min(b.mins[k] for b in brushes) for k in range(3)))
    if classname == "func_door":
        angle = float(keys.get("angle", "0"))
        if angle == -1:
            direction = (0.0, 0.0, 1.0)
        elif angle == -2:
            direction = (0.0, 0.0, -1.0)
        else:
            direction = (round(math.cos(math.radians(angle)), 6), round(math.sin(math.radians(angle)), 6), 0.0)
        distance = abs(dot(direction, size)) - float(keys.get("lip", "8"))
        return mul(direction, distance)
    if classname == "func_plat" and "targetname" not in keys:
        height = float(keys.get("height", "0"))
        return (0.0, 0.0, -(height if height else size[2] - 8.0))
    return (0.0, 0.0, 0.0)


# ---------------------------------------------------------------- main conversion

def convert(map_path, texture_dir, output_dir, json_path):
    entities = parse_map(map_path)
    report = collections.OrderedDict()

    world_brushes, models = [], []
    start_offsets = {}
    class_counts = collections.Counter()
    skipped = collections.Counter()
    for keys, raw_brushes in entities:
        classname = keys.get("classname", "")
        if not raw_brushes:
            continue
        if classname.startswith("trigger_"):
            skipped["trigger brushes"] += len(raw_brushes)
            continue
        brushes = []
        for raw in raw_brushes:
            if raw[0].texture == "clip":
                skipped["clip brushes"] += 1
                continue
            brushes.append(Brush(raw, len(brushes)))
        if classname == "worldspawn":
            world_brushes = brushes
        elif brushes:
            class_counts[classname] += 1
            name = "%s_%02d" % (classname, class_counts[classname])
            models.append((name, brushes))
            start_offsets[name] = start_offset(classname, keys, brushes)
    report["skipped"] = dict(skipped)

    world = csg(world_brushes)
    report["world fragments after CSG"] = len(world)

    blockers = BrushGrid([b for b in world_brushes if b.contents != LIQUID])
    kept, removed = [], []
    for item in world:
        (removed if faces_void(blockers, item[0], item[1]) else kept).append(item)
    report["world fragments facing the void (removed)"] = len(removed)

    view_points = []
    for keys, raw_brushes in entities:
        if not raw_brushes and "origin" in keys:
            view_points.append(tuple(float(x) for x in keys["origin"].split()))
    visible = check_removed_faces_are_invisible(blockers, removed, view_points)
    report["removed fragments visible from inside (must be 0)"] = len(visible)
    if visible:
        print(json.dumps(report, indent=2))
        for v in visible[:20]:
            print("  visible removed face:", v)
        sys.exit("Void removal deleted faces that can be seen from inside the level.")

    def mirrored(items):
        result = []
        for polygon, face, brush in items:
            result.append((polygon, face, brush))
            if brush.contents == LIQUID:
                flipped = Face(mul(face.normal, -1.0), -face.distance, face.texture, face.offset, face.rotation, face.scale)
                flipped.texture_face = face
                result.append((list(reversed(polygon)), flipped, brush))
        return result

    groups = [("worldspawn", mirrored(kept))] + [(name, mirrored(csg(brushes))) for name, brushes in models]

    texture_sizes, texture_files = {}, {}
    objects = []
    total_inserted = 0
    check_totals = collections.Counter()
    for name, items in groups:
        positions, polygons = weld(items)
        polygons = merge_coplanar(positions, polygons)
        polygons, inserted = fix_t_junctions(positions, polygons)
        total_inserted += inserted
        check_totals["coplanar overlaps left (must be 0)"] += count_coplanar_overlaps(positions, polygons)
        check_totals["T-junctions left (must be 0)"] += count_t_junctions(positions, polygons)
        if name == "worldspawn":
            check_totals["visible open edges (must be 0)"] += count_visible_open_edges(positions, polygons, blockers, view_points)
        if name == "worldspawn":
            origin = tuple(min(b.mins[k] for b in world_brushes) for k in range(3))
            chunks = collections.defaultdict(list)
            for polygon in polygons:
                center = polygon[2].center
                key = (int((center[0] - origin[0]) // CHUNK_SIZE), int((center[1] - origin[1]) // CHUNK_SIZE))
                chunks[key].append(polygon)
            for key in sorted(chunks):
                objects.append(("World_%02d_%02d" % key, True, positions, chunks[key]))
        else:
            objects.append((name, False, positions, polygons))
    report["vertices inserted to fix T-junctions"] = total_inserted
    report.update(check_totals)

    os.makedirs(os.path.join(output_dir, "Textures"), exist_ok=True)
    obj_lines = ["# Generated by map_to_obj.py from %s. Do not edit by hand." % os.path.basename(map_path),
                 "mtllib e1m1.mtl"]
    vertex_ids, uv_ids, normal_ids = {}, {}, {}
    v_lines, vt_lines, vn_lines, f_lines = [], [], [], []

    def index_of(table, lines, prefix, value):
        if value not in table:
            table[value] = len(table) + 1
            lines.append(prefix + " " + " ".join(format_number(c) for c in value))
        return table[value]

    stats_objects = []
    total_triangles = 0
    bounds_min, bounds_max = [math.inf] * 3, [-math.inf] * 3
    winding_errors = 0
    for name, is_static, positions, polygons in objects:
        f_lines.append("g " + name)
        by_material = collections.defaultdict(list)
        for polygon in polygons:
            texture_face = getattr(polygon[1], "texture_face", polygon[1])
            by_material[texture_face.texture].append(polygon)
        triangles = 0
        for texture in sorted(by_material):
            if texture not in texture_sizes:
                texture_files[texture] = texture_file_name(texture, texture_dir)
                texture_sizes[texture] = png_size(os.path.join(texture_dir, texture_files[texture]))
            f_lines.append("usemtl " + material_name(texture))
            for indices, face, brush in by_material[texture]:
                texture_face = getattr(face, "texture_face", face)
                points = [positions[i] for i in indices]
                points, fan = triangulate(points)
                uvs = texture_uvs(texture_face, points, texture_sizes[texture])
                unity_points = [to_unity(p) for p in points]
                unity_normal = to_unity_direction(face.normal)
                if dot(newell_normal(unity_points[:len(indices)]), unity_normal) < 0:
                    fan = [(t[0], t[2], t[1]) for t in fan]
                normal_id = index_of(normal_ids, vn_lines, "vn", (-unity_normal[0], unity_normal[1], unity_normal[2]))
                corner_ids = []
                for p, uv in zip(unity_points, uvs):
                    v_id = index_of(vertex_ids, v_lines, "v", (-p[0], p[1], p[2]))
                    vt_id = index_of(uv_ids, vt_lines, "vt", (round(uv[0], 6), round(uv[1], 6)))
                    corner_ids.append("%d/%d/%d" % (v_id, vt_id, normal_id))
                    for k in range(3):
                        bounds_min[k] = min(bounds_min[k], p[k])
                        bounds_max[k] = max(bounds_max[k], p[k])
                for t in fan:
                    a, b, c = unity_points[t[0]], unity_points[t[1]], unity_points[t[2]]
                    if dot(cross(sub(b, a), sub(c, a)), unity_normal) <= 0:
                        winding_errors += 1
                    # OBJ is right-handed: Unity's importer negates x and reverses the winding, undoing both here.
                    f_lines.append("f %s %s %s" % (corner_ids[t[0]], corner_ids[t[2]], corner_ids[t[1]]))
                triangles += len(fan)
        stats_objects.append({"Name": name, "IsStatic": is_static, "Triangles": triangles,
                              "StartOffset": vector(to_unity(start_offsets.get(name, (0.0, 0.0, 0.0))))})
        total_triangles += triangles
    report["objects"] = len(objects)
    report["triangles"] = total_triangles
    report["triangles with wrong or zero winding (must be 0)"] = winding_errors

    with open(os.path.join(output_dir, "e1m1.obj"), "w", newline="\n") as f:
        f.write("\n".join(obj_lines + v_lines + vt_lines + vn_lines + f_lines) + "\n")
    with open(os.path.join(output_dir, "e1m1.mtl"), "w", newline="\n") as f:
        for texture in sorted(texture_files):
            f.write("newmtl %s\nKd 1 1 1\nmap_Kd Textures/%s\n\n" % (material_name(texture), texture_files[texture]))
    for texture in sorted(texture_files):
        source = os.path.join(texture_dir, texture_files[texture])
        destination = os.path.join(output_dir, "Textures", texture_files[texture])
        if os.path.abspath(source) != os.path.abspath(destination):
            shutil.copyfile(source, destination)
    report["materials"] = len(texture_files)

    lights, views = [], []
    counts = collections.Counter()
    for keys, raw_brushes in entities:
        classname = keys.get("classname", "")
        if raw_brushes or "origin" not in keys:
            continue
        origin = tuple(float(x) for x in keys["origin"].split())
        if classname.startswith("light"):
            value = float(keys.get("light", DEFAULT_LIGHT))
            counts[classname] += 1
            name = "%s_%03d" % (classname, counts[classname])
            if "style" in keys:
                name += " (style %s)" % keys["style"]
            lights.append({"Name": name, "Position": vector(to_unity(origin)),
                           "Range": value * UNITY_UNITS_PER_QUAKE_UNIT, "Intensity": value / LIGHT_INTENSITY_UNIT,
                           "StartsOff": int(keys.get("spawnflags", "0")) & 1 == 1})
        elif classname == "info_player_start":
            yaw = float(keys.get("angle", "0"))
            views.insert(0, {"Name": "info_player_start", "Position": vector(to_unity(add(origin, (0, 0, EYE_HEIGHT)))),
                             "Rotation": vector((0.0, 90.0 - yaw, 0.0))})
        elif classname == "info_intermission":
            pitch, yaw, roll = (float(x) for x in keys.get("mangle", "0 0 0").split())
            counts[classname] += 1
            views.append({"Name": "info_intermission_%d" % counts[classname], "Position": vector(to_unity(origin)),
                          "Rotation": vector((pitch, 90.0 - yaw, roll))})
    report["lights"] = len(lights)
    report["views"] = len(views)

    with open(json_path, "w", newline="\n") as f:
        json.dump({"Source": os.path.basename(map_path), "Triangles": total_triangles,
                   "BoundsMin": vector(bounds_min), "BoundsMax": vector(bounds_max), "Objects": stats_objects,
                   "Lights": lights, "Views": views}, f, indent=1)
    print(json.dumps(report, indent=2))
    failed = [key for key, value in report.items() if "must be 0" in key and value != 0]
    if failed:
        sys.exit("Checks failed: " + ", ".join(failed))


def vector(v):
    return {"x": v[0], "y": v[1], "z": v[2]}


def format_number(x):
    if x == 0:
        return "0"
    return repr(x) if isinstance(x, float) and abs(x - round(x, 6)) > 0 else ("%.6f" % x).rstrip("0").rstrip(".")


if __name__ == "__main__":
    if len(sys.argv) != 5:
        sys.exit(__doc__)
    convert(*sys.argv[1:])
