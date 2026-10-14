"""Sincroniza server/manifest.json com as ferramentas declaradas em server/server.py.

Mantém as descrições já escritas e usa a primeira frase da docstring para
ferramentas novas. Uso: python scripts/update_manifest.py
"""

import ast
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "server" / "server.py"
MANIFEST = ROOT / "server" / "manifest.json"


def main() -> None:
    tree = ast.parse(SERVER.read_text(encoding="utf-8"))
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    known = {t["name"]: t["description"] for t in manifest["tools"]}
    tools = []
    for fn in tree.body:
        if not isinstance(fn, ast.AsyncFunctionDef):
            continue
        if not any(getattr(getattr(d, "func", None), "attr", None) == "tool" for d in fn.decorator_list):
            continue
        description = known.get(fn.name)
        if not description:
            paragraph = " ".join((ast.get_docstring(fn) or fn.name).strip().split("\n\n")[0].split())
            description = re.split(r"(?<=\.)\s", paragraph)[0]
            if len(description) > 160:
                description = description[:157].rsplit(" ", 1)[0] + "..."
        tools.append({"name": fn.name, "description": description})
    manifest["tools"] = tools
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(tools)} ferramentas em {MANIFEST.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
