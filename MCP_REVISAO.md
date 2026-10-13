# Revisão técnica — MCP Revit 2025

Data: 08/08/2026

## Resultado dos testes

| Verificação | Resultado | Evidência |
|---|---|---|
| Compilação do add-in | Aprovada | `dotnet build -c Release`: 0 erros |
| Ferramentas de planta | Revisadas | `set_plan_crop`, `create_text` com tamanho, `delete_text_notes`, `place_rooms_at`, `place_furniture` |
| Inicialização pelo Revit 2025 | Reprovada | A porta local `127.0.0.1:14733` permanece fechada |
| Execução de `OnStartup` | Reprovada | o log `%TEMP%\revit_mcp_startup.log` não é criado |

## Diagnóstico

O Revit está deixando de descobrir/carregar o manifesto antes de entrar no código do add-in. Portanto, a falha atual não é de uma tool nem do servidor HTTP: ela ocorre na descoberta do arquivo `.addin`.

## Melhorias já incorporadas

- Fila de comandos com estado por requisição, para evitar troca de resposta em timeout.
- Ferramentas para plantas: ambientes por coordenada, mobiliário orientável, crop de vista e limpeza seletiva de textos.
- Tamanho de texto parametrizável em `create_text`.
- Log de startup em `App.cs`, para que o próximo carregamento exponha a etapa que falhar.

## Próxima ação recomendada

No Revit, verificar **Arquivo > Opções > Complementos** e a política de add-ins, mantendo somente um manifesto ativo com este caminho:

`C:\ProgramData\Autodesk\Revit\Addins\2025\RevitMcp.addin`

Depois, reiniciar o Revit e validar `http://127.0.0.1:14733/health`. A ponte estará aprovada quando retornar `ok: true`.

---

# Revisão 2 — correções para o MCP funcionar de ponta a ponta

Data: 13/10/2026

| Problema | Efeito | Correção |
|---|---|---|
| `requirements.txt` com `mcp>=1.0` instalava o mcp 2.x, que removeu `mcp.server.fastmcp` | `server.py` encerrava no import; o cliente MCP não via nenhuma ferramenta | Fixado `mcp>=1.2,<2`; mensagem clara se a versão errada estiver instalada |
| Manifesto instalado em `%APPDATA%` **e** `%ProgramData%` com o mesmo `ClientId` | Revit recusa o complemento por AddInId duplicado | O script instala um único manifesto (`-AllUsers` para ProgramData) e desativa duplicatas |
| Bridge em `HttpListener` com prefixo `http://127.0.0.1:14733/` | Em conta sem administrador, HTTP.sys lança "Acesso negado" no `OnStartup` | Bridge reescrito sobre `TcpListener` em loopback, sem urlacl |
| `signtool.exe` com caminho fixo do SDK 10.0.26100.0 | Script falhava em máquinas com outro SDK ou sem SDK | Localização automática; sem SDK, segue sem assinatura com aviso |
| Verificação de falha do `dotnet build` após a geração de certificados | Erro de build mascarado | Verificação logo após o build e conferência da DLL |
| `bridge.json` ausente quando o Revit é aberto sem o script | Add-in falhava ao iniciar | O add-in gera o token e o servidor Python o lê primeiro |
| Auditoria em Extensible Storage dentro do fluxo do comando | Comando concluído podia retornar erro (doc somente leitura, `open_document`) | Auditoria protegida e ignorada em simulações e comandos de arquivo |
| Mensagens de erro ocultadas fora de `development_mode` | A IA não conseguia corrigir argumentos | Mensagem sempre retornada; stack trace só em modo de desenvolvimento |
| `create_ceiling_plan` enviava `x1_m..y2_m`, o add-in lia `x_min_m..y_max_m` | Forro sempre criado no retângulo padrão 8×5 m | Servidor converte os cantos e envia `height_offset_m` |
| Proxy corporativo em `HTTP(S)_PROXY` | Chamadas a 127.0.0.1 podiam ir para o proxy | Cliente HTTP local com `trust_env=False` |
| `server/manifest.json` sem 25 ferramentas | Lista incompleta no cliente | Manifesto regenerado (117 ferramentas) e coberto por teste |

## Verificações executadas nesta revisão

- `pytest tests`: 15 testes (servidor contra bridge falso, erros, token, porta, contrato Python ↔ C# ↔ manifesto).
- Sessão MCP real via stdio: `initialize`, `list_tools` (117) e `call_tool`.
- `HttpBridge.cs` compilado com Roslyn (.NET 8) e exercitado pelo `server.py --check`: health, 401, Host inválido, 404, JSON inválido, `Expect: 100-continue`, payload > 1 MB, chunked recusado e 40 requisições concorrentes.
- Scripts PowerShell validados pelo parser do PowerShell 7.

Não foi possível nesta revisão: compilar o add-in completo contra `RevitAPI.dll` e carregá-lo no Revit 2025 (exige Windows com Revit). Após atualizar, execute `reload-revit-mcp.ps1` e, se necessário, `diagnose-revit-mcp.ps1`.
