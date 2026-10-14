import asyncio
import json

import pytest


from revit_mcp import core


def run(coro):
    return asyncio.run(coro)


def test_health_ok(bridge):
    srv, _ = bridge
    assert run(srv.bridge_health())["ok"] is True


def test_rpc_sends_command_and_args(bridge):
    srv, fake = bridge
    result = run(srv.create_wall(x1_m=0, y1_m=0, x2_m=5, y2_m=0))
    assert result["echo"]["cmd"] == "create_wall"
    assert fake.calls[-1]["args"]["x2_m"] == 5


def test_rpc_error_includes_request_id(bridge):
    srv, _ = bridge
    with pytest.raises(RuntimeError, match=r"\[boom\] falhou \(requisição abc\)"):
        run(srv._rpc("boom"))


def test_wrong_token_is_reported(bridge, monkeypatch):
    srv, _ = bridge
    monkeypatch.setenv("REVIT_MCP_TOKEN", "errado")
    with pytest.raises(RuntimeError, match="Não autorizado.*reload-revit-mcp"):
        run(srv._rpc("get_info"))


def test_missing_token(bridge, monkeypatch, tmp_path):
    srv, _ = bridge
    monkeypatch.delenv("REVIT_MCP_TOKEN")
    monkeypatch.setattr(core, "CONFIG_PATH", tmp_path / "nao-existe.json")
    monkeypatch.setattr(core, "WORKSPACE_CONFIG_PATH", tmp_path / "nao-existe2.json")
    with pytest.raises(RuntimeError, match="Token do RevitMcp não encontrado"):
        run(srv._rpc("get_info"))


def test_token_read_from_bridge_json_with_bom(bridge, monkeypatch, tmp_path):
    srv, _ = bridge
    monkeypatch.delenv("REVIT_MCP_TOKEN")
    cfg = tmp_path / "bridge.json"
    # PowerShell 5.1 grava UTF-8 com BOM.
    cfg.write_text(json.dumps({"token": "token-de-teste"}), encoding="utf-8-sig")
    monkeypatch.setattr(core, "CONFIG_PATH", cfg)
    assert run(srv._rpc("get_info"))["echo"]["cmd"] == "get_info"


def test_bridge_offline_message(bridge, monkeypatch):
    srv, _ = bridge
    monkeypatch.setattr(core, "BRIDGE_URL", "http://127.0.0.1:1/rpc")
    with pytest.raises(RuntimeError, match="Não foi possível conectar ao Revit"):
        run(srv._rpc("get_info"))


def test_remote_url_rejected(bridge, monkeypatch):
    srv, _ = bridge
    monkeypatch.setattr(core, "BRIDGE_URL", "http://10.0.0.5:14733/rpc")
    monkeypatch.delenv("REVIT_MCP_ALLOW_REMOTE", raising=False)
    with pytest.raises(RuntimeError, match="bridge HTTP local"):
        run(srv._rpc("get_info"))


def test_ceiling_plan_translates_corners(bridge):
    srv, fake = bridge
    run(srv.create_ceiling_plan(x1_m=6, y1_m=4, x2_m=1, y2_m=0))
    args = fake.calls[-1]["args"]
    assert (args["x_min_m"], args["y_min_m"], args["x_max_m"], args["y_max_m"]) == (1, 0, 6, 4)
    assert args["height_offset_m"] == 2.7


def test_bridge_url_uses_port_from_config(monkeypatch, tmp_path):
    cfg = tmp_path / "bridge.json"
    cfg.write_text(json.dumps({"token": "x", "port": 15000}), encoding="utf-8")
    monkeypatch.delenv("REVIT_MCP_URL", raising=False)
    monkeypatch.delenv("REVIT_MCP_PORT", raising=False)
    monkeypatch.setattr(core, "CONFIG_PATH", cfg)
    assert core._bridge_url() == "http://127.0.0.1:15000/rpc"


def test_check_cli(bridge, capsys):
    srv, _ = bridge
    assert run(srv._check()) == 0
    assert "OK" in capsys.readouterr().out


def test_optional_arguments_are_omitted(bridge):
    srv, fake = bridge
    run(srv.find_elements(category="Doors", where=[{"parameter": "Marca", "op": "empty"}]))
    args = fake.calls[-1]["args"]
    assert fake.calls[-1]["cmd"] == "find_elements"
    assert args == {"category": "Doors", "where": [{"parameter": "Marca", "op": "empty"}], "limit": 200}


def test_bulk_parameters_payload(bridge):
    srv, fake = bridge
    run(srv.set_parameters_bulk(params={"Comentários": "Revisar"}, filter={"category": "Doors"}))
    assert fake.calls[-1]["args"] == {
        "params": {"Comentários": "Revisar"}, "filter": {"category": "Doors"}, "target": "instance"}


def test_create_beam_requires_coordinates_or_list(bridge):
    srv, fake = bridge
    with pytest.raises(ValueError):
        run(srv.create_beam(x1_m=0, y1_m=0))
    run(srv.create_beam(beams=[{"x1_m": 0, "y1_m": 0, "x2_m": 5, "y2_m": 0}], type="20x50"))
    assert fake.calls[-1]["args"]["beams"][0]["x2_m"] == 5
    assert "x1_m" not in fake.calls[-1]["args"]


def test_mep_run_payload(bridge):
    srv, fake = bridge
    run(srv.create_mep_run(kind="duct", points=[{"x_m": 0, "y_m": 0}, {"x_m": 6, "y_m": 0}],
                           width_m=0.4, height_m=0.3))
    args = fake.calls[-1]["args"]
    assert args["kind"] == "duct" and args["width_m"] == 0.4 and "diameter_m" not in args
