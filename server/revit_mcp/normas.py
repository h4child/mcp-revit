"""Base de conhecimento normativo (Códigos de Obras e guias) exposta como resources MCP.

Os textos e as regras ficam em ``knowledge/`` como arquivos versionados:

    knowledge/municipios/<slug>.json  regras estruturadas usadas na verificação
    knowledge/municipios/<slug>.md    guia legível do Código de Obras
    knowledge/guias/*.md              boas práticas e índice de normas ABNT

A verificação (``avaliar``) é uma função pura sobre os dados que o add-in já
devolve (get_room_report, list_windows, find_elements), para ser testável sem
o Revit. A ferramenta MCP que a chama fica em tools_arch.audit_code_compliance.
"""

import json
import math
import re
import unicodedata
from functools import lru_cache
from pathlib import Path
from typing import Optional

from .core import mcp

KNOWLEDGE_DIR = Path(__file__).resolve().parent / "knowledge"
MUNICIPIOS_DIR = KNOWLEDGE_DIR / "municipios"
GUIAS_DIR = KNOWLEDGE_DIR / "guias"

# Tolerância para arredondamentos de área/medida vindos do Revit.
_TOL = 0.005
# Limite superior padrão dos ambientes no Revit (8 pés). Não é o pé-direito do
# projeto: indica que o limite superior do ambiente nunca foi configurado.
_ALTURA_PADRAO_REVIT_M = 2.438


# =============================================================================
# Leitura da base de conhecimento
# =============================================================================

def municipios() -> list[str]:
    """Slugs dos municípios com regras cadastradas (ex.: 'paracatu-mg')."""
    return sorted(p.stem for p in MUNICIPIOS_DIR.glob("*.json"))


def guias() -> list[str]:
    return sorted(p.stem for p in GUIAS_DIR.glob("*.md"))


def _slug_valido(slug: str, disponiveis: list[str], tipo: str) -> str:
    slug = (slug or "").strip().lower()
    if slug not in disponiveis:
        raise ValueError(f"{tipo} '{slug}' não cadastrado. Disponíveis: {', '.join(disponiveis) or 'nenhum'}.")
    return slug


@lru_cache(maxsize=None)
def carregar_regras(slug: str) -> dict:
    slug = _slug_valido(slug, municipios(), "Município")
    return json.loads((MUNICIPIOS_DIR / f"{slug}.json").read_text(encoding="utf-8"))


def ler_guia_municipio(slug: str) -> str:
    slug = _slug_valido(slug, municipios(), "Município")
    return (MUNICIPIOS_DIR / f"{slug}.md").read_text(encoding="utf-8")


def ler_guia(nome: str) -> str:
    nome = _slug_valido(nome, guias(), "Guia")
    return (GUIAS_DIR / f"{nome}.md").read_text(encoding="utf-8")


def indice() -> str:
    linhas = [
        "# Base normativa do Revit MCP",
        "",
        "Conteúdo de referência para projetar e revisar o modelo. Os valores legais",
        "vêm do Código de Obras do município; os guias são boas práticas. Sempre",
        "confirme com responsável técnico habilitado e com a Consulta Prévia da Prefeitura.",
        "",
        "## Municípios",
    ]
    for slug in municipios():
        regras = carregar_regras(slug)
        leis = "; ".join(f["lei"].split(" — ")[0] for f in regras.get("fontes", []))
        linhas.append(f"- `revit://normas/municipios/{slug}` — {regras['municipio']}/{regras['uf']} ({leis})")
        linhas.append(f"  - regras estruturadas: `revit://normas/municipios/{slug}/regras`")
    linhas += ["", "## Guias"]
    for nome in guias():
        titulo = ler_guia(nome).splitlines()[0].lstrip("# ").strip()
        linhas.append(f"- `revit://normas/guias/{nome}` — {titulo}")
    linhas += ["", "## Verificação automática",
               "`audit_code_compliance(municipio=...)` cruza ambientes, janelas, portas e escadas",
               "do modelo com as regras do município e aponta falhas e pontos de atenção."]
    return "\n".join(linhas) + "\n"


# =============================================================================
# Resources MCP
# =============================================================================

@mcp.resource("revit://normas", name="normas-indice", mime_type="text/markdown",
              description="Índice da base normativa: Códigos de Obras municipais e guias de projeto.")
def recurso_indice() -> str:
    return indice()


@mcp.resource("revit://normas/municipios/{slug}", name="codigo-obras-municipio", mime_type="text/markdown",
              description="Guia do Código de Obras do município (ex.: paracatu-mg), com artigos citados.")
def recurso_municipio(slug: str) -> str:
    return ler_guia_municipio(slug)


@mcp.resource("revit://normas/municipios/{slug}/regras", name="codigo-obras-regras", mime_type="application/json",
              description="Regras estruturadas do Código de Obras usadas por audit_code_compliance.")
def recurso_regras(slug: str) -> str:
    return json.dumps(carregar_regras(slug), ensure_ascii=False, indent=2)


@mcp.resource("revit://normas/guias/{nome}", name="guia-projeto", mime_type="text/markdown",
              description="Guias de projeto: esquadrias-e-paredes (tipologias, tamanhos, posição) e "
                          "normas-abnt (o que cada NBR cobre).")
def recurso_guia(nome: str) -> str:
    return ler_guia(nome)


# =============================================================================
# Classificação e geometria
# =============================================================================

def normalizar(texto: Optional[str]) -> str:
    texto = unicodedata.normalize("NFKD", texto or "")
    return "".join(c for c in texto if not unicodedata.combining(c)).lower().strip()


def classificar(nome: Optional[str], regras: dict) -> Optional[str]:
    """Classe do compartimento pelo nome, na ordem definida pelas regras.

    A ordem importa: "Closet Suíte 2" é closet (não dormitório) e
    "Varanda Gourmet" é área externa (não cozinha).
    """
    alvo = normalizar(nome)
    for classe in regras["ordem_classificacao"]:
        for palavra in regras["compartimentos"][classe]["palavras"]:
            if re.search(r"(?<![a-z])" + re.escape(normalizar(palavra)), alvo):
                return classe
    return None


def dimensao_minima_estimada(area_m2: Optional[float], perimetro_m: Optional[float]) -> Optional[float]:
    """Menor lado do retângulo com a mesma área e perímetro do ambiente.

    Exato para ambientes retangulares; em plantas em L ou recortadas
    subestima a largura, por isso o resultado só gera "atenção".
    """
    if not area_m2 or not perimetro_m or area_m2 <= 0 or perimetro_m <= 0:
        return None
    semi = perimetro_m / 2
    disc = semi * semi - 4 * area_m2
    if disc < 0:  # arredondamentos de um quadrado
        return round(math.sqrt(area_m2), 3)
    return round((semi - math.sqrt(disc)) / 2, 3)


def _fmt(valor: Optional[float], unidade: str = "m") -> str:
    if valor is None:
        return "—"
    return f"{valor:.2f} {unidade}".replace(".", ",")


def _check(item: str, status: str, exigido: str, projeto: str, artigo: Optional[str], nota: Optional[str] = None) -> dict:
    out = {"item": item, "status": status, "exigido": exigido, "projeto": projeto, "artigo": artigo}
    if nota:
        out["nota"] = nota
    return out


# =============================================================================
# Avaliação
# =============================================================================

def _area_janela(janela: dict) -> float:
    largura, altura = janela.get("largura_m"), janela.get("altura_m")
    return (largura or 0) * (altura or 0)


def _janela_fixa(janela: dict) -> bool:
    texto = normalizar(f"{janela.get('familia') or ''} {janela.get('tipo') or ''}")
    return "fixo" in texto or "fixa" in texto or "fixed" in texto


def _extras_do_ambiente(extras: dict, numero: str, nome: str) -> float:
    total = 0.0
    for chave, valor in (extras or {}).items():
        k = normalizar(str(chave))
        if k and k in (normalizar(numero), normalizar(nome), normalizar(f"{numero} {nome}")):
            total += float(valor)
    return total


def _param(elemento: dict, *nomes: str) -> Optional[float]:
    params = elemento.get("parametros") or {}
    for nome in nomes:
        valor = params.get(nome)
        if isinstance(valor, (int, float)) and not isinstance(valor, bool):
            return float(valor)
    return None


DOOR_PARAMS = ["DOOR_WIDTH", "DOOR_HEIGHT", "Largura", "Altura"]
STAIR_PARAMS = ["STAIRS_ACTUAL_RISER_HEIGHT", "STAIRS_ACTUAL_TREAD_DEPTH", "STAIRS_ACTUAL_NUMBER_OF_RISERS",
                "Altura real do espelho", "Profundidade real do piso", "Número real de espelhos",
                "Altura desejada da escada"]


def avaliar(regras: dict, relatorio: dict, janelas: list, portas: Optional[dict] = None,
            escadas: Optional[dict] = None, aberturas_extras: Optional[dict] = None) -> dict:
    """Confronta o modelo com as regras do município.

    relatorio: saída de get_room_report; janelas: list_windows; portas/escadas:
    find_elements com DOOR_PARAMS/STAIR_PARAMS. aberturas_extras: {ambiente: m²}
    para panos de vidro e portas envidraçadas que iluminam o ambiente.
    """
    comp = regras["compartimentos"]
    gerais = regras["gerais"]
    pe_min = gerais["pe_direito_min_m"]
    relatorio = relatorio if isinstance(relatorio, dict) else {}
    janelas = janelas if isinstance(janelas, list) else []
    portas = portas if isinstance(portas, dict) else {}
    escadas = escadas if isinstance(escadas, dict) else {}

    # Janelas por ambiente (o add-in identifica o ambiente como "<número> <nome>").
    por_ambiente: dict[str, list] = {}
    for janela in janelas or []:
        rotulo = normalizar(janela.get("ambiente"))
        if rotulo:
            por_ambiente.setdefault(rotulo, []).append(janela)

    ambientes_out, nao_classificados, sanitarios, altura_padrao = [], [], [], []
    for amb in relatorio.get("ambientes", []):
        numero, nome = amb.get("numero") or "", amb.get("nome") or ""
        classe = classificar(nome, regras)
        area = amb.get("area_m2") or 0.0
        checks = []
        base = {"id": amb.get("id"), "numero": numero, "nome": nome, "nivel": amb.get("nivel"),
                "area_m2": area, "classe": classe}

        if classe is None:
            nao_classificados.append(f"{numero} {nome}".strip())
            ambientes_out.append({**base, "verificacoes": [_check(
                "Classificação", "atencao", "nome que indique a função", nome or "(sem nome)", "Art. 93",
                "Renomeie o ambiente pela função (ex.: 'Quarto', 'Banheiro') para aplicar os parâmetros.")]})
            continue
        regra = comp[classe]
        base["classe_rotulo"] = regra["rotulo"]
        if regra.get("verificar") is False:
            ambientes_out.append({**base, "verificacoes": [], "nota": regra.get("nota")})
            continue

        if amb.get("status") not in (None, "ok"):
            checks.append(_check("Delimitação", "falha", "ambiente posicionado e fechado", amb.get("status"),
                                 None, "Feche o contorno com paredes ou separadores de ambiente."))

        # Área mínima
        if regra.get("area_min_m2"):
            ok = area + _TOL >= regra["area_min_m2"]
            checks.append(_check("Área mínima", "ok" if ok else "falha", _fmt(regra["area_min_m2"], "m²"),
                                 _fmt(area, "m²"), regra.get("artigo")))
        if classe == "instalacao_sanitaria":
            sanitarios.append((area, f"{numero} {nome}".strip()))

        # Dimensão mínima (estimada pela área e perímetro)
        dim_min = regra.get("dimensao_min_m")
        if dim_min:
            est = dimensao_minima_estimada(area, amb.get("perimetro_m"))
            if est is not None:
                ok = est + _TOL >= dim_min
                checks.append(_check("Dimensão mínima (estimada)", "ok" if ok else "atencao", _fmt(dim_min),
                                     _fmt(est), regra.get("artigo"),
                                     None if ok else "Estimativa pelo retângulo equivalente; meça a menor largura útil na planta."))

        # Iluminação e ventilação natural
        jans = por_ambiente.get(normalizar(f"{numero} {nome}"), [])
        area_vaos = sum(_area_janela(j) for j in jans) + _extras_do_ambiente(aberturas_extras, numero, nome)
        artificial = regra.get("ventilacao_artificial_permitida", False)
        fracao = regra.get("iluminacao_fracao")
        if fracao:
            exigido = area * fracao
            if area_vaos + _TOL >= exigido:
                checks.append(_check("Vão de iluminação/ventilação", "ok",
                                     f"{regra['iluminacao_texto']} = {_fmt(exigido, 'm²')}", _fmt(area_vaos, "m²"),
                                     regra.get("artigo")))
            else:
                status = "atencao" if artificial else "falha"
                nota = ("Vão natural insuficiente: indique iluminação e exaustão mecânica (Art. 103 p.u. I) "
                        "ou amplie a janela.") if artificial else (
                        "Amplie as janelas ou informe panos de vidro/portas envidraçadas em aberturas_extras.")
                checks.append(_check("Vão de iluminação/ventilação", status,
                                     f"{regra['iluminacao_texto']} = {_fmt(exigido, 'm²')}", _fmt(area_vaos, "m²"),
                                     regra.get("artigo"), nota))
        elif artificial and not jans and area_vaos <= 0:
            checks.append(_check("Iluminação/ventilação", "atencao", "natural ou artificial", "sem janela",
                                 "Art. 103 p.u. I", "Sem abertura: indique iluminação e ventilação mecânica no projeto."))
        fixas = [j for j in jans if _janela_fixa(j)]
        if fixas:
            checks.append(_check("Janelas que abrem", "atencao", "vão que ventile", f"{len(fixas)} janela(s) fixa(s)",
                                 "Art. 103", "Janela fixa ilumina mas não ventila; use correr, maxim-ar ou basculante."))

        # Acesso
        if (amb.get("portas") or 0) == 0:
            checks.append(_check("Vão de acesso", "falha", "ao menos 1 porta ou vão", "0", regra.get("artigo")))

        # Pé-direito (altura do ambiente no Revit; confirmar o livre até o forro)
        alvo_pe = regra.get("pe_direito_min_m") or pe_min["valor"]
        pe = amb.get("pe_direito_m")
        if pe and abs(pe - _ALTURA_PADRAO_REVIT_M) < 0.01:
            altura_padrao.append(f"{numero} {nome}".strip())
        elif pe:
            ok = pe + _TOL >= alvo_pe
            checks.append(_check("Pé-direito", "ok" if ok else "atencao", _fmt(alvo_pe), _fmt(pe),
                                 regra.get("artigo") if regra.get("pe_direito_min_m") else pe_min["artigo"],
                                 None if ok else "Altura do ambiente no Revit (limite superior). Confirme o pé-direito livre do piso acabado ao forro."))

        ambientes_out.append({**base, "verificacoes": checks})

    gerais_out = []

    if altura_padrao:
        gerais_out.append(_check(
            "Pé-direito", "atencao", _fmt(pe_min["valor"]),
            f"não verificado em {len(altura_padrao)} ambiente(s): altura padrão do Revit (2,44 m)", pe_min["artigo"],
            "O limite superior desses ambientes não foi configurado. Ajuste 'Limite superior'/'Deslocamento "
            "do limite' dos ambientes até o forro (modify_parameters ou set_parameters_bulk) e rode de novo."))

    # Instalação sanitária principal
    if sanitarios:
        principal = comp["instalacao_sanitaria"].get("principal", {})
        maior = max(sanitarios)
        ok = maior[0] + _TOL >= principal.get("area_min_m2", 0)
        gerais_out.append(_check("Instalação sanitária principal", "ok" if ok else "falha",
                                 f"ao menos uma com {_fmt(principal.get('area_min_m2'), 'm²')}",
                                 f"maior: {maior[1]} ({_fmt(maior[0], 'm²')})", comp["instalacao_sanitaria"].get("artigo"),
                                 principal.get("nota")))
    elif relatorio.get("ambientes"):
        gerais_out.append(_check("Instalação sanitária", "falha", "obrigatória em residência", "nenhuma identificada",
                                 "Art. 96"))

    # Portas
    portas_lista = (portas or {}).get("elementos") or []
    if portas_lista:
        alt_min = gerais["vao_acesso_altura_min_m"]
        baixas, estreitas, so_secundario = [], [], []
        maior_largura = 0.0
        for porta in portas_lista:
            largura = _param(porta, "DOOR_WIDTH", "Largura")
            altura = _param(porta, "DOOR_HEIGHT", "Altura")
            ident = f"{porta.get('id')} ({porta.get('tipo')})"
            if altura is not None and altura + _TOL < alt_min["valor"]:
                baixas.append(f"{ident}: {_fmt(altura)}")
            if largura is not None:
                maior_largura = max(maior_largura, largura)
                if largura + _TOL < 0.60:
                    estreitas.append(f"{ident}: {_fmt(largura)}")
                elif largura + _TOL < 0.70:
                    so_secundario.append(f"{ident}: {_fmt(largura)}")
        gerais_out.append(_check("Altura dos vãos de acesso", "falha" if baixas else "ok", _fmt(alt_min["valor"]),
                                 "; ".join(baixas) or f"{len(portas_lista)} porta(s) atendem", alt_min["artigo"]))
        if estreitas:
            gerais_out.append(_check("Largura mínima de porta", "falha", "0,60 m (banheiro secundário)",
                                     "; ".join(estreitas), "Anexo III"))
        if so_secundario:
            gerais_out.append(_check("Portas entre 0,60 e 0,70 m", "atencao", "só em banheiro secundário",
                                     "; ".join(so_secundario), "Anexo III"))
        gerais_out.append(_check("Vão de acesso externo ≥ 0,80 m", "ok" if maior_largura + _TOL >= 0.80 else "falha",
                                 "0,80 m em um acesso externo de estar/cozinha", _fmt(maior_largura), "Anexo III",
                                 "A largura lida é a da folha; o vão livre real é menor (batente). NBR 9050 pede 0,80 m livres."))

    # Escadas
    esc = gerais["escada"]
    for escada in (escadas or {}).get("elementos") or []:
        espelho = _param(escada, "STAIRS_ACTUAL_RISER_HEIGHT", "Altura real do espelho")
        piso = _param(escada, "STAIRS_ACTUAL_TREAD_DEPTH", "Profundidade real do piso")
        n = _param(escada, "STAIRS_ACTUAL_NUMBER_OF_RISERS", "Número real de espelhos")
        ident = f"Escada {escada.get('id')}"
        if espelho is not None:
            ok = esc["espelho_min_m"] - _TOL <= espelho <= esc["espelho_max_m"] + _TOL
            gerais_out.append(_check(f"{ident} — espelho", "ok" if ok else "falha", "16 a 18 cm",
                                     _fmt(espelho * 100, "cm"), esc["artigo"]))
        if piso is not None:
            ok = esc["piso_min_m"] - _TOL <= piso <= esc["piso_max_m"] + _TOL
            gerais_out.append(_check(f"{ident} — piso", "ok" if ok else "falha", "28 a 32 cm",
                                     _fmt(piso * 100, "cm"), esc["artigo"]))
        if espelho is not None and piso is not None:
            blondel = 2 * espelho + piso
            ok = esc["blondel_min_m"] - _TOL <= blondel <= esc["blondel_max_m"] + _TOL
            gerais_out.append(_check(f"{ident} — Blondel (2h + p)", "ok" if ok else "falha", "63 a 65 cm",
                                     _fmt(blondel * 100, "cm"), esc["artigo"]))
        if espelho is not None and n is not None:
            altura = espelho * n
            if altura > esc["patamar_altura_max_m"] + _TOL:
                gerais_out.append(_check(f"{ident} — patamar", "atencao",
                                         f"patamar se vencer mais de {_fmt(esc['patamar_altura_max_m'])}",
                                         f"vence {_fmt(altura)}", esc["artigo"] + " §1º",
                                         "Confirme que a escada tem patamar com profundidade ≥ largura."))

    # Lixeira (Art. 102)
    lixo = gerais["lixeira"]
    area_total = relatorio.get("area_total_m2") or 0
    if area_total > lixo["area_construida_gatilho_m2"]:
        tem = any("lixo" in normalizar(a.get("nome")) for a in relatorio.get("ambientes", []))
        gerais_out.append(_check("Depósito de lixo", "ok" if tem else "atencao",
                                 f"obrigatório acima de {lixo['area_construida_gatilho_m2']} m²",
                                 "encontrado" if tem else f"não encontrado (área de ambientes {_fmt(area_total, 'm²')})",
                                 lixo["artigo"], lixo["texto"]))

    todos = [c for a in ambientes_out for c in a["verificacoes"]] + gerais_out
    resumo = {s: sum(1 for c in todos if c["status"] == s) for s in ("ok", "falha", "atencao")}
    return {
        "municipio": f"{regras['municipio']}/{regras['uf']}",
        "fontes": [f["lei"] for f in regras.get("fontes", [])],
        "resumo": resumo,
        "ambientes": ambientes_out,
        "gerais": gerais_out,
        "nao_classificados": nao_classificados,
        "nao_verificado_automaticamente": [
            "Recuos, taxa de ocupação e permeabilidade (Plano Diretor / Lei de Uso e Ocupação do Solo).",
            "Escalas das pranchas: cortes e fachadas em 1:50, implantação em 1:200 (Art. 20).",
            "Guarda-corpos: altura e posição em desníveis ≥ 1,00 m (Art. 89).",
            "Afastamento de 1,50 m entre aberturas e aberturas voltadas a vizinhos (Art. 104).",
            "Vão por varanda (1/5) e redução por área de serviço: confirme a origem da iluminação.",
        ],
        "aviso": regras.get("aviso"),
    }
