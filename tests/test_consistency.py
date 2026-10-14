"""Garante que servidor Python, add-in C# e manifesto expõem o mesmo contrato."""

import ast
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CS = "\n".join(p.read_text(encoding="utf-8") for p in sorted((ROOT / "addin").glob("Commands*.cs")))
# Todas as ferramentas MCP ficam nos módulos server/revit_mcp/tools_*.py.
PY = ast.Module(body=[node for path in [ROOT / "server" / "revit_mcp" / name for name in
                ["tools_model.py", "tools_editing.py", "tools_pro.py", "tools_arch.py"]]
                      for node in ast.parse(path.read_text(encoding="utf-8")).body], type_ignores=[])

CASES = dict(re.findall(r'case "(\w+)":\s*return (\w+)\(', CS))
CASES.pop("ping", None)


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
REQUIRED = re.compile(r'(?:Get\w*Req\(\s*args\s*,\s*"(\w+)"|ProIds\(\s*args\s*,\s*"(\w+)"\s*,\s*required:\s*true)')


def _required_keys(method, seen=None):
    seen = seen or set()
    if method in seen or method not in BODIES:
        return set()
    seen.add(method)
    body = BODIES[method]
    keys = {a or b for a, b in REQUIRED.findall(body)}
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
                    if isinstance(node, ast.Call) and getattr(node.func, "id", None) == "_clean":
                        sent |= {k.arg for k in node.keywords if k.arg}
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


def test_every_tool_has_annotations():
    import server

    tools = server.mcp._tool_manager.list_tools()
    missing = [t.name for t in tools if t.annotations is None or t.annotations.readOnlyHint is None]
    assert not missing, missing
    by_name = {t.name: t.annotations for t in tools}
    assert by_name["list_levels"].readOnlyHint is True
    assert by_name["find_elements"].readOnlyHint is True
    assert by_name["create_wall"].readOnlyHint is False and by_name["create_wall"].destructiveHint is False
    assert by_name["delete_element"].destructiveHint is True
    assert by_name["purge_unused"].destructiveHint is True
    assert by_name["export_sheets_pdf"].readOnlyHint is False and by_name["export_sheets_pdf"].destructiveHint is False
