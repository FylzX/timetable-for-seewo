"""Screen-relative persistence. Coordinates may be physical or logical, never mixed."""

import math

DEFAULT_STATE = {"width": 0.36, "height": 0.60, "x": 0.965, "y": 0.18, "locked": False}


def clean_state(raw):
    result = DEFAULT_STATE.copy()
    if not isinstance(raw, dict):
        return result
    for key, low, high in (("width", .20, 1.8), ("height", .30, 1.0), ("x", 0, 1), ("y", 0, 1)):
        value = raw.get(key)
        if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value):
            result[key] = max(low, min(high, value))
    result["locked"] = raw.get("locked") is True
    if isinstance(raw.get("screen"), str):
        result["screen"] = raw["screen"]
    return result


def restore_rect(state, work):
    x, y, width, height = work
    state = clean_state(state)
    w = min(width, round(height * state["width"]))
    h = min(height, round(height * state["height"]))
    return (x + round((width - w) * state["x"]),
            y + round((height - h) * state["y"]), w, h)


def capture_rect(rect, work):
    x, y, w, h = rect
    sx, sy, sw, sh = work
    return {"width": w / sh, "height": h / sh,
            "x": max(0, min(1, (x - sx) / max(1, sw - w))),
            "y": max(0, min(1, (y - sy) / max(1, sh - h)))}


def clamp_rect(rect, work):
    x, y, w, h = rect
    sx, sy, sw, sh = work
    w = max(min(round(sh * .20), sw), min(w, sw))
    h = max(round(sh * .30), min(h, sh))
    return (round(max(sx, min(x, sx + sw - w))),
            round(max(sy, min(y, sy + sh - h))), round(w), round(h))
