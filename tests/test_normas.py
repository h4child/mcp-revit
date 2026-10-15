"""Base normativa: resources MCP, classificação de ambientes e audit_code_compliance."""

import asyncio
import json

import pytest

import server  # noqa: F401  (registra ferramentas e resources)
from revit_mcp import normas
from revit_mcp.core import mcp


def run(coro):
    return asyncio.run(coro)


@pytest.fixture(scope="module")
def regras():
    return normas.carregar_regras("paracatu-mg")


# --- Base de conhecimento --------------------------------------------------------

def test_municipios_e_guias_cadastrados():
    assert "paracatu-mg" in normas.municipios()
    assert {"esquadrias-e-paredes", "normas-abnt"} <= set(normas.guias())


@pytest.mark.parametrize("slug", normas.municipios())
def test_regras_municipais_consistentes(slug):
    regras = normas.carregar_regras(slug)
    assert set(regras["ordem_classificacao"]) == set(regras["compartimentos"])
    assert regras["fontes"] and all(f["lei"] and f["url"] for f in regras["fontes"])
    for nome, comp in regras["compartimentos"].items():
        assert comp["palavras"], nome
        if comp.get("verificar") is not False and "area_min_m2" in comp:
            assert comp.get("artigo"), nome
    # Todo município tem o guia legível ao lado do JSON.
    assert normas.ler_guia_municipio(slug).startswith("#")


def test_valores_paracatu(regras):
    comp = regras["compartimentos"]
    assert comp["dormitorio"]["area_min_m2"] == 7.0 and comp["dormitorio"]["dimensao_min_m"] == 2.0
    assert comp["estar"]["area_min_m2"] == 8.0
    assert comp["cozinha"]["iluminacao_fracao"] == 0.125
    assert comp["instalacao_sanitaria"]["principal"]["area_min_m2"] == 2.4
    assert regras["gerais"]["pe_direito_min_m"]["valor"] == 2.8
    esc = regras["gerais"]["escada"]
    assert (esc["espelho_min_m"], esc["espelho_max_m"], esc["piso_min_m"], esc["piso_max_m"]) == (0.16, 0.18, 0.28, 0.32)


def test_municipio_desconhecido():
    with pytest.raises(ValueError, match="não cadastrado.*paracatu-mg"):
        normas.carregar_regras("cidade-inexistente")


# --- Resources MCP ----------------------------------------------------------------

def _ler(uri):
    return "".join(c.content for c in run(mcp.read_resource(uri)))


def test_resources_publicados():
    estaticos = {str(r.uri) for r in run(mcp.list_resources())}
    templates = {t.uriTemplate for t in run(mcp.list_resource_templates())}
    assert "revit://normas" in estaticos
    assert {"revit://normas/municipios/{slug}", "revit://normas/municipios/{slug}/regras",
            "revit://normas/guias/{nome}"} <= templates


def test_resources_conteudo():
    assert "revit://normas/municipios/paracatu-mg" in _ler("revit://normas")
    assert "Art. 87" in _ler("revit://normas/municipios/paracatu-mg")
    assert json.loads(_ler("revit://normas/municipios/paracatu-mg/regras"))["slug"] == "paracatu-mg"
    assert "Verga alinhada" in _ler("revit://normas/guias/esquadrias-e-paredes")


def test_servidor_aponta_para_a_base():
    assert "revit://normas" in mcp.instructions


# --- Classificação e geometria ----------------------------------------------------

@pytest.mark.parametrize("nome,classe", [
    ("Suíte 2", "dormitorio"),
    ("Suíte Hóspedes", "dormitorio"),
    ("Closet Suíte 2", "closet_deposito"),
    ("Banho Suíte 2", "instalacao_sanitaria"),
    ("Banho Master (hidromassagem)", "instalacao_sanitaria"),
    ("Lavabo", "instalacao_sanitaria"),
    ("Varanda Gourmet", "area_externa"),
    ("Cozinha Gourmet", "cozinha"),
    ("Living - Estar / Jantar / Home Theater", "estar"),
    ("Home Gym", "estar"),
    ("Escritório", "estar"),
    ("Sala de TV / Estar Íntimo", "estar"),
    ("Hall de Entrada (PD duplo)", "circulacao"),
    ("Galeria / Circulação", "circulacao"),
    ("Área de Serviço", "area_servico"),
    ("Despensa", "closet_deposito"),
    ("Casa de Máquinas", "closet_deposito"),
    ("Garagem (4 carros)", "garagem"),
    ("Terraço", "area_externa"),
    ("Ambiente 7", None),
])
def test_classificar(regras, nome, classe):
    assert normas.classificar(nome, regras) == classe


def test_dimensao_minima_estimada():
    assert normas.dimensao_minima_estimada(20, 18) == 4.0     # retângulo 4 x 5
    assert normas.dimensao_minima_estimada(16, 15.99) == 4.0  # quadrado com arredondamento
    assert normas.dimensao_minima_estimada(0, 10) is None


# --- Avaliação --------------------------------------------------------------------

def _amb(id_, numero, nome, area, perimetro, portas=1, pe=3.0):
    return {"id": id_, "numero": numero, "nome": nome, "nivel": "Térreo", "status": "ok", "area_m2": area,
            "perimetro_m": perimetro, "pe_direito_m": pe, "portas": portas, "janelas": 0}


def _por_item(itens):
    return {c["item"]: c for c in itens}


@pytest.fixture
def resultado(regras):
    relatorio = {
        "area_total_m2": 250,
        "ambientes": [
            _amb(1, "S07", "Suíte 2", 16.0, 16.0),
            _amb(2, "S06", "Banho Suíte 2", 4.4, 8.4),
            _amb(3, "T09", "Living", 92.0, 40.0),
            _amb(4, "T03", "Lavabo", 1.2, 4.4, pe=2.4),
            _amb(5, "T12", "Varanda Gourmet", 60.0, 40.0, portas=0),
            _amb(6, "X1", "Ambiente 7", 5.0, 9.0),
        ],
    }
    janelas = [
        {"ambiente": "S07 Suíte 2", "familia": "M_Fixo", "tipo": "Quarto", "largura_m": 1.5, "altura_m": 1.2},
        {"ambiente": "T09 Living", "familia": "Correr", "tipo": "Sala", "largura_m": 2.4, "altura_m": 1.5},
    ]
    portas = {"elementos": [
        {"id": 10, "tipo": "P1", "parametros": {"DOOR_WIDTH": 1.2, "DOOR_HEIGHT": 2.4}},
        {"id": 11, "tipo": "P4", "parametros": {"DOOR_WIDTH": 0.65, "DOOR_HEIGHT": 2.1}},
        {"id": 12, "tipo": "Baixa", "parametros": {"DOOR_WIDTH": 0.8, "DOOR_HEIGHT": 2.0}},
    ]}
    escadas = {"elementos": [{"id": 20, "parametros": {
        "STAIRS_ACTUAL_RISER_HEIGHT": 0.179, "STAIRS_ACTUAL_TREAD_DEPTH": 0.25, "STAIRS_ACTUAL_NUMBER_OF_RISERS": 19}}]}
    return normas.avaliar(regras, relatorio, janelas, portas, escadas, aberturas_extras={"T09": 15.6})


def _ambiente(resultado, numero):
    return next(a for a in resultado["ambientes"] if a["numero"] == numero)


def test_quarto_com_janela_pequena_e_fixa(resultado):
    itens = _por_item(_ambiente(resultado, "S07")["verificacoes"])
    assert itens["Área mínima"]["status"] == "ok"
    assert itens["Vão de iluminação/ventilação"]["status"] == "falha"
    assert itens["Janelas que abrem"]["status"] == "atencao"
    assert itens["Dimensão mínima (estimada)"]["status"] == "ok"


def test_banheiro_sem_janela_pede_exaustao(resultado):
    itens = _por_item(_ambiente(resultado, "S06")["verificacoes"])
    assert itens["Vão de iluminação/ventilação"]["status"] == "atencao"
    assert "Art. 103" in itens["Vão de iluminação/ventilação"]["nota"]


def test_aberturas_extras_contam_no_vao(resultado):
    itens = _por_item(_ambiente(resultado, "T09")["verificacoes"])
    assert itens["Vão de iluminação/ventilação"]["status"] == "ok"


def test_lavabo_pequeno_e_pe_direito(resultado):
    itens = _por_item(_ambiente(resultado, "T03")["verificacoes"])
    assert itens["Área mínima"]["status"] == "falha"
    assert itens["Pé-direito"]["status"] == "atencao"


def test_area_externa_e_nao_classificado(resultado):
    assert _ambiente(resultado, "T12")["verificacoes"] == []
    assert resultado["nao_classificados"] == ["X1 Ambiente 7"]


def test_verificacoes_gerais(resultado):
    gerais = _por_item(resultado["gerais"])
    assert gerais["Instalação sanitária principal"]["status"] == "ok"
    assert gerais["Altura dos vãos de acesso"]["status"] == "falha"
    assert gerais["Portas entre 0,60 e 0,70 m"]["status"] == "atencao"
    assert gerais["Vão de acesso externo ≥ 0,80 m"]["status"] == "ok"
    assert gerais["Escada 20 — espelho"]["status"] == "ok"
    assert gerais["Escada 20 — piso"]["status"] == "falha"
    assert gerais["Escada 20 — Blondel (2h + p)"]["status"] == "falha"
    assert gerais["Escada 20 — patamar"]["status"] == "atencao"
    assert gerais["Depósito de lixo"]["status"] == "atencao"


def test_altura_padrao_do_revit_vira_um_aviso_geral(regras):
    relatorio = {"ambientes": [_amb(1, "S07", "Suíte 2", 16.0, 16.0, pe=2.438),
                               _amb(2, "S10", "Suíte 3", 16.0, 16.0, pe=2.438)]}
    r = normas.avaliar(regras, relatorio, [])
    assert all("Pé-direito" not in _por_item(a["verificacoes"]) for a in r["ambientes"])
    geral = _por_item(r["gerais"])["Pé-direito"]
    assert geral["status"] == "atencao" and "2 ambiente(s)" in geral["projeto"]


def test_resumo_conta_todos_os_itens(resultado):
    total = sum(len(a["verificacoes"]) for a in resultado["ambientes"]) + len(resultado["gerais"])
    assert sum(resultado["resumo"].values()) == total
    assert resultado["municipio"] == "Paracatu/MG"


# --- Ferramenta MCP ---------------------------------------------------------------

def test_audit_code_compliance_consulta_o_modelo(bridge):
    srv, fake = bridge
    resultado = run(srv.audit_code_compliance(level="Térreo"))
    cmds = [c["cmd"] for c in fake.calls]
    assert cmds == ["get_room_report", "list_windows", "find_elements", "find_elements"]
    assert fake.calls[0]["args"] == {"level": "Térreo"}
    assert "DOOR_WIDTH" in fake.calls[2]["args"]["return_parameters"]
    assert fake.calls[3]["args"]["category"] == "Stairs"
    assert resultado["municipio"] == "Paracatu/MG"


def test_audit_code_compliance_e_somente_leitura():
    anot = {t.name: t.annotations for t in mcp._tool_manager.list_tools()}["audit_code_compliance"]
    assert anot.readOnlyHint is True and anot.destructiveHint is False
