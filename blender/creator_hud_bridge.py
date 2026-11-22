bl_info = {
    "name": "CreatorHUD Bridge",
    "author": "Eshan + Claude",
    "version": (1, 0, 0),
    "blender": (4, 2, 0),
    "location": "Runs in the background",
    "description": "Sends render progress to the CreatorHUD island on the desktop",
    "category": "Render",
}

# Writes %LOCALAPPDATA%\WinUltra\render.json while Blender renders. The island reads it and shows the
# progress ring. Works for F12 stills, Ctrl+F12 animations and command-line renders, Cycles and EEVEE.

import json
import os
import re
import tempfile
import time

import bpy
from bpy.app.handlers import persistent

PATH = os.path.join(os.environ.get("LOCALAPPDATA") or tempfile.gettempdir(), "WinUltra", "render.json")
SAMPLE_RE = re.compile(r"(?:Sample|Rendering)\s+(\d+)\s*/\s*(\d+)", re.IGNORECASE)

S = {}
_last_write = 0.0


def _reset(scene):
    S.clear()
    S.update(
        state="rendering",
        file=os.path.basename(bpy.data.filepath) or "untitled.blend",
        animation=False,
        frame_changed=False,
        pre_count=0,
        frame=scene.frame_current,
        frames_done=0,
        frames_total=1,
        sample=0,
        samples=0,
        frac=0.0,
        started=time.time(),
        scene=scene.name,
    )


def _frames_total(scene):
    step = max(1, scene.frame_step)
    return max(1, len(range(scene.frame_start, scene.frame_end + 1, step)))


def _progress():
    if S.get("state") == "complete":
        return 1.0
    total = S["frames_total"] if S["animation"] else 1
    done = S["frames_done"] if S["animation"] else 0
    frac = S["frac"]
    if not S["animation"] and S["samples"] == 0 and S["pre_count"] == 0:
        return -1.0
    return max(0.0, min(1.0, (done + frac) / total))


def _write(force=False):
    global _last_write
    now = time.time()
    if not force and now - _last_write < 0.2:
        return
    _last_write = now
    p = _progress()
    elapsed = now - S.get("started", now)
    eta = elapsed * (1 - p) / p if 0.02 < p < 1 else -1
    data = {
        "state": S.get("state", "idle"),
        "app": "Blender",
        "file": S.get("file", ""),
        "animation": S.get("animation", False),
        "frame": S.get("frame", 0),
        "frames_done": S.get("frames_done", 0),
        "frames_total": S.get("frames_total", 1),
        "sample": S.get("sample", 0),
        "samples": S.get("samples", 0),
        "progress": p,
        "elapsed": elapsed,
        "eta": eta,
        "t": now,
    }
    try:
        os.makedirs(os.path.dirname(PATH), exist_ok=True)
        tmp = PATH + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f)
        os.replace(tmp, PATH)
    except OSError:
        pass


def _scene(args):
    for a in args:
        if isinstance(a, bpy.types.Scene):
            return a
    return bpy.context.scene


@persistent
def on_init(*args):
    scene = _scene(args)
    _reset(scene)
    _write(force=True)


@persistent
def on_frame_change(*args):
    # An animation render steps the frame before each frame it renders; a still render never does.
    if S.get("state") == "rendering" and S.get("pre_count", 0) == 0:
        S["frame_changed"] = True


@persistent
def on_pre(*args):
    if S.get("state") != "rendering":
        _reset(_scene(args))
    scene = _scene(args)
    S["pre_count"] += 1
    if S["frame_changed"] or S["pre_count"] > 1:
        S["animation"] = True
        S["frames_total"] = _frames_total(scene)
    S["frame"] = scene.frame_current
    S["frac"] = 0.0
    S["sample"] = 0
    _write(force=True)


@persistent
def on_post(*args):
    if S.get("state") != "rendering":
        return
    S["frames_done"] += 1
    S["frac"] = 0.0
    _write(force=True)


@persistent
def on_stats(*args):
    if S.get("state") != "rendering":
        return
    text = next((a for a in args if isinstance(a, str)), "")
    m = None
    for m in SAMPLE_RE.finditer(text):
        pass
    if m:
        n, total = int(m.group(1)), int(m.group(2))
        if total > 0:
            S["sample"], S["samples"] = n, total
            S["frac"] = min(0.999, n / total)
            _write()


@persistent
def on_complete(*args):
    if not S:
        return
    S["state"] = "complete"
    S["frames_done"] = S.get("frames_total", 1) if S.get("animation") else 1
    _write(force=True)


@persistent
def on_cancel(*args):
    if not S:
        return
    S["state"] = "cancelled"
    _write(force=True)


HANDLERS = [
    ("render_init", on_init),
    ("frame_change_pre", on_frame_change),
    ("render_pre", on_pre),
    ("render_post", on_post),
    ("render_stats", on_stats),
    ("render_complete", on_complete),
    ("render_cancel", on_cancel),
]


def register():
    for name, fn in HANDLERS:
        lst = getattr(bpy.app.handlers, name, None)
        if lst is not None and fn not in lst:
            lst.append(fn)


def unregister():
    for name, fn in HANDLERS:
        lst = getattr(bpy.app.handlers, name, None)
        if lst is not None and fn in lst:
            lst.remove(fn)


if __name__ == "__main__":
    register()
