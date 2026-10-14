# Revit MCP

Automação BIM local para **Autodesk Revit 2025** via Model Context Protocol (MCP). O projeto conecta um servidor Python às APIs do Revit por meio de um add-in .NET, com execução segura na thread principal do Revit.

```text
Cliente MCP / IA
       │ stdio
       ▼
Servidor Python (FastMCP) ── token local ──► Add-in .NET no Revit
                                                │
                                                ▼
                                           Autodesk Revit API
```

## O que ele faz

- Cria e edita elementos arquitetônicos: níveis, paredes, portas, janelas, pisos, escadas, telhados e ambientes.
- Insere famílias, mobiliário, componentes, materiais, texturas, detalhes 2D e geometria livre.
- Gera vistas, cortes, cotas, pranchas, tabelas, screenshots e exportações.
- Trabalha em metros, valida o documento e identifica avisos do Revit.
- Protege o bridge local com token, limite de payload, logs e timeout seguro.
- Oferece lote atômico com dry-run, prévia obrigatória de exclusões, backup local, auditoria persistente, perfis IFC e verificações de programa, emissão e worksharing somente-leitura.

> O MCP é uma ferramenta de automação. A validação de normas, compatibilização, quantitativos e responsabilidade técnica continuam exigindo revisão profissional humana.

## Recursos profissionais (todas as disciplinas)

| Área | Ferramentas |
|---|---|
| Dados do projeto | `get_project_info`, `set_project_info`, `get_project_location`, `list_phases`, `set_element_phase`, `create_workset`, `set_element_workset`, `list_project_parameters`, `create_project_parameter` |
| Tipos | `list_element_types` (dimensões, camadas, uso), `duplicate_type`, `change_element_type`, `create_layered_type` (parede/piso/forro/cobertura por camadas) |
| Consulta e edição em massa | `find_elements` (filtros por categoria, nível, tipo e parâmetros), `set_parameters_bulk` (instância ou tipo, numa transação) |
| Vistas | `duplicate_view`, `set_view_properties`, `create_elevations`, `create_3d_view`, `create_drafting_view`, `create_view_filter`/`apply_view_filter`, `create_rule_filter` (filtro com regras de parâmetro), `set_category_override`, `set_element_override`, `reset_overrides`, `override_elements_in_view`, `set_category_visibility`, `tag_elements` |
| Documentação | `list_sheets`, `list_titleblocks`, `create_sheets`, `list_revisions`, `create_revision`, `add_revision_to_sheets`, `list_schedules`, `create_schedule_advanced`, `get_schedule_data` |
| Exportação | `export_sheets_pdf`, `export_sheets_dwg`, `export_schedule_csv` |
| Coordenação e qualidade | `analyze_clashes`, `analyze_quantities`, `analyze_material_takeoff`, `preview_purge_unused`, `purge_unused`, `list_links`, `load_revit_link`, `reload_link` |
| Estrutura | `create_beam`, `create_foundation` (sapatas isoladas e corridas), além de `create_column` e `create_grid` |
| MEP | `list_mep_types`, `create_mep_run` (dutos, tubos, eletrocalhas e eletrodutos com curvas), `create_spaces_from_rooms` |
| Modelagem geral | `array_elements`, `create_group`, `list_groups`, `place_group`, `create_room_separators` |

Unidades: comprimentos em metros, áreas em m², volumes em m³ e ângulos em graus — inclusive ao ler ou gravar parâmetros (`find_elements`, `set_parameters_bulk`, `modify_parameters`). Outras grandezas usam a unidade de exibição do projeto, ou texto como `"25 mm"`, interpretado pelo Revit.

Exemplos de pedidos à IA:

- "Crie as pranchas A-101 a A-105 com o carimbo A1, adicione a revisão 'Aprovação prefeitura' e exporte tudo em um PDF."
- "Liste as portas sem Marca no térreo e numere-as P01, P02..."
- "Verifique interferências entre vigas e dutos e crie uma vista 3D isolando cada conflito."
- "Crie o parâmetro 'Acabamento de piso' em Ambientes e uma tabela com Número, Nome, Área e esse parâmetro."
- "Faça um quantitativo de paredes, pisos e esquadrias por tipo para o orçamento."

## Formato das respostas

Leituras devolvem os dados diretamente. Comandos que alteram o modelo devolvem um envelope padronizado:

```json
{
  "comando": "create_wall",
  "resultado": { "...": "retorno específico do comando" },
  "alteracoes": {
    "documento": "Casa.rvt",
    "duracao_ms": 184,
    "transacoes": ["MCP: Criar parede"],
    "criados_total": 1,
    "criados": [{ "id": 312345, "categoria": "Paredes", "tipo": "Genérico - 15 cm", "nivel": "Térreo",
                  "posicao": { "inicio": {"x_m": 0, "y_m": 0, "z_m": 0}, "fim": {"x_m": 5, "y_m": 0, "z_m": 0} },
                  "comprimento_m": 5.0, "area_m2": 14.0, "volume_m3": 2.1, "altura_m": 2.8 }],
    "modificados_total": 2,
    "modificados_por_categoria": { "Paredes": 2 },
    "excluidos_total": 0
  },
  "avisos": [{ "severidade": "Warning", "descricao": "...", "ids": [312345] }],
  "avisos_total": 1
}
```

`alteracoes` vem do evento `DocumentChanged` do Revit, portanto reflete o efeito real no modelo (inclusive elementos unidos ou recalculados), e `avisos` lista somente os avisos novos gerados pelo comando.


- Windows 10/11
- Autodesk Revit 2025
- .NET SDK 8 ou posterior
- Python 3.10 ou posterior

## Instalação

```powershell
git clone <URL_DO_SEU_REPOSITORIO> revit-mcp
cd revit-mcp

Copy-Item .env.example .env
Copy-Item server\.env.example server\.env

python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r server\requirements.txt
```

Em `.env`, configure opcionalmente `REVIT_MCP_PROJECT_PATH` para abrir um arquivo `.rvt` específico após o build. O arquivo nunca é versionado.

## Compilar, assinar e carregar

O script prepara o token local, compila a DLL, assina o add-in, instala um manifesto com o caminho desta máquina e abre o Revit:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\reload-revit-mcp.ps1
```

Para abrir um projeto pontual sem gravá-lo em `.env`:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\reload-revit-mcp.ps1 `
  -ProjectPath 'C:\Projetos\modelo.rvt'
```

O manifesto é instalado em **um único** local — `%APPDATA%\Autodesk\Revit\Addins\2025\RevitMcp.addin` por padrão, ou `%ProgramData%\Autodesk\Revit\Addins\2025\RevitMcp.addin` com `-AllUsers` (requer administrador). Cópias antigas com o mesmo AddInId são renomeadas para `.addin.disabled`, pois dois manifestos com o mesmo ID fazem o Revit recusar o complemento. Nenhum dos dois faz parte do Git.

Outras opções do script: `-NoSign` (não assina; o Revit pergunta na primeira carga — escolha **Sempre carregar**), `-NoLaunch` (só compila e instala) e `-WaitSeconds` (tempo de espera pelo bridge após abrir o Revit). O `signtool.exe` é localizado automaticamente no Windows SDK; sem ele, a DLL segue sem assinatura.

Na primeira carga, o Revit pode exibir o aviso de segurança de complemento: escolha **Sempre carregar**. Ao final, o script aguarda o bridge responder e informa quando o MCP está pronto.

## Executar o servidor MCP

```powershell
.\.venv\Scripts\Activate.ps1
python .\server\server.py --check   # testa a conexão com o Revit e sai
python .\server\server.py           # modo MCP (stdio), usado pelo cliente
```

O servidor exige o pacote `mcp` 1.x (`mcp>=1.2,<2`); a série 2.x removeu o módulo `FastMCP` usado aqui.

Configuração MCP genérica:

```json
{
  "mcpServers": {
    "revit-mcp": {
      "command": "C:\\caminho\\revit-mcp\\.venv\\Scripts\\python.exe",
      "args": ["C:\\caminho\\revit-mcp\\server\\server.py"]
    }
  }
}
```

## Segurança e operação

- O bridge atende apenas em `127.0.0.1` e exige `X-Revit-Mcp-Token`.
- Tokens, certificados, `.env`, projetos Revit, famílias e builds são ignorados pelo Git.
- Execute `bridge_health` antes de um lote, `validate_document` após alterações e `save_document` quando a validação estiver aprovada.
- Use `save_as` apenas quando a troca/sobrescrita de arquivo for intencional.

Veja [PRODUCAO.md](PRODUCAO.md) para diagnóstico, logs, recuperação e rotação do token.

## Diagnóstico

Se o MCP não responder, execute o diagnóstico somente leitura. Ele verifica Revit, manifestos duplicados, DLL bloqueada/assinatura, token, porta, logs do add-in, journal do Revit e a versão do pacote `mcp`, e indica a correção de cada falha:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\diagnose-revit-mcp.ps1
```

## Desenvolvimento

```powershell
pip install -r requirements-dev.txt
python -m pytest tests            # servidor contra um bridge falso + contrato Python/C#/manifesto
dotnet build .\addin\RevitMcp.csproj -c Release
```

Sem o Revit instalado na máquina de build, o projeto compila contra os assemblies de referência da API do Revit 2025 publicados no NuGet (`Nice3point.Revit.Api.*`), que não são copiados para a saída. O CI (`.github/workflows/ci.yml`) roda os testes Python e o build do add-in a cada push e pull request.

`python scripts/update_manifest.py` sincroniza o manifesto após adicionar ferramentas. `tests/test_consistency.py` garante que todo comando enviado pelo servidor existe no add-in, que os argumentos obrigatórios são enviados e que `server/manifest.json` lista todas as ferramentas.

Principais diretórios:

| Caminho | Conteúdo |
|---|---|
| `addin/` | Add-in C# e integração com Revit API |
| `server/` | Servidor MCP Python e ferramentas expostas |
| `reload-revit-mcp.ps1` | Build, assinatura, configuração local e carregamento |
| `PRODUCAO.md` | Procedimento operacional e segurança |

## Licença

Distribuído sob a [licença MIT](LICENSE).
