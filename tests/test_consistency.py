"""Garante que servidor Python, add-in C# e manifesto expõem o mesmo contrato."""

import ast
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CS = (ROOT / "addin" / "Commands.cs").read_text(encoding="utf-8")
PY = ast.parse((ROOT / "server" / "server.py").read_text(encoding="utf-8"))

CASES = dict(re.findall(r'case "(\w+)":\s*return (\w+)\(', CS))


def _tools():
    return [
        f for f in PY.body
        if isinstance(f, ast.AsyncFunctionDef)
        and any(getattr(getattr(d, "func", None), "attr", None) == "tool" for d in f.decorator_list)
    ]


def _rpc_calls(fn):
    for node in ast.walk(fn):
        if isinstance(node, ast.Call) and getattr(node.func, "id", None) == "_rpc":
            yield node


def _method_bodies():
    bodies = {}
    for m in re.finditer(r"static [\w<>\[\],\. ]+? (\w+)\([^)]*\)\s*\{", CS):
        i, depth = m.end(), 1
        while depth:
            depth += {"{": 1, "}": -1}.get(CS[i], 0)
            i += 1
        bodies[m.group(1)] = bodies.get(m.group(1), "") + CS[m.end():i]
    for name, body in re.findall(r"static object (\w+)\([^)]*\)\s*=>\s*([^;]+);", CS):
        bodies[name] = body
    return bodies


BODIES = _method_bodies()
REQUIRED = re.compile(r'Get\w*Req\(\s*args\s*,\s*"(\w+)"')


def _required_keys(method, seen=None):
    seen = seen or set()
    if method in seen or method not in BODIES:
        return set()
    seen.add(method)
    body = BODIES[method]
    keys = set(REQUIRED.findall(body))
    for callee in re.findall(r"(\w+)\([^()]*\bargs\b", body):
        if callee != method and callee in BODIES and callee != "ExecutarComando":
            keys |= _required_keys(callee, seen)
    return keys


def test_every_rpc_command_exists_in_addin():
    missing = []
    for fn in _tools():
        for call in _rpc_calls(fn):
            cmd = call.args[0].value
            if cmd != "ping" and cmd not in CASES:
                missing.append(f"{fn.name} -> {cmd}")
    assert not missing, missing


def test_required_arguments_are_sent():
    problems = []
    for fn in _tools():
        for call in _rpc_calls(fn):
            cmd = call.args[0].value
            if cmd in ("ping", "execute_batch") or cmd not in CASES:
                continue
            sent = {k.arg for k in call.keywords if k.arg}
            if any(k.arg is None for k in call.keywords):
                for node in ast.walk(fn):
                    if isinstance(node, ast.Dict):
                        sent |= {k.value for k in node.keys if isinstance(k, ast.Constant)}
                    if isinstance(node, ast.Subscript) and isinstance(node.slice, ast.Constant):
                        sent.add(node.slice.value)
            missing = _required_keys(CASES[cmd]) - sent
            if missing:
                problems.append(f"{fn.name}: {sorted(missing)}")
    assert not problems, problems


def test_manifest_lists_every_tool():
    manifest = json.loads((ROOT / "server" / "manifest.json").read_text(encoding="utf-8"))
    listed = {t["name"] for t in manifest["tools"]}
    exposed = {f.name for f in _tools()}
    assert listed == exposed, {"faltando": exposed - listed, "sobrando": listed - exposed}


def test_tool_names_unique():
    names = [f.name for f in _tools()]
    assert len(names) == len(set(names))
