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

## Requisitos

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

`tests/test_consistency.py` garante que todo comando enviado pelo servidor existe no add-in, que os argumentos obrigatórios são enviados e que `server/manifest.json` lista todas as ferramentas.

Principais diretórios:

| Caminho | Conteúdo |
|---|---|
| `addin/` | Add-in C# e integração com Revit API |
| `server/` | Servidor MCP Python e ferramentas expostas |
| `reload-revit-mcp.ps1` | Build, assinatura, configuração local e carregamento |
| `PRODUCAO.md` | Procedimento operacional e segurança |

## Licença

Distribuído sob a [licença MIT](LICENSE).
