# Operação em produção — Revit MCP

## Segurança e inicialização

Execute `reload-revit-mcp.ps1` para compilar, assinar e abrir o Revit com o complemento atualizado. O script cria, uma única vez, o segredo local em `%LOCALAPPDATA%\RevitMcp\bridge.json`. Se o Revit for aberto sem o script e o arquivo não existir, o próprio add-in gera o token no mesmo local, que o servidor Python lê primeiro.

- O bridge usa um socket TCP em `127.0.0.1` (sem HTTP.sys), portanto não exige `netsh http add urlacl` nem privilégio de administrador.
- Requisições com cabeçalho `Host` diferente de loopback são recusadas (proteção contra DNS rebinding).
- A porta padrão é 14733; para mudar, defina `"port"` em `bridge.json` (o servidor Python lê o mesmo campo) ou `REVIT_MCP_PORT` nos dois processos.

- O bridge aceita somente `127.0.0.1`.
- Cada chamada exige o cabeçalho `X-Revit-Mcp-Token`.
- O servidor Python lê esse segredo a cada chamada; recarregar o Revit não exige reiniciar o MCP.
- O arquivo de segredo não deve ser movido para o repositório, compartilhado, nem enviado por chat.
- A cópia operacional em `server/.revit_mcp_bridge.json` é ignorada pelo Git e recebe ACL somente para o usuário atual durante o reload.

## Operação segura

1. Execute `bridge_health` antes de qualquer lote.
2. Execute `plan_batch`; use `execute_batch` sem `commit` para simular e só então confirme o lote.
3. Antes de exclusões, execute `preview_delete`; `delete_element` exige `confirm=true`.
4. Após mudanças, execute `validate_document`, `analyze_bim_quality`, `validate_room_program`, `audit_documentation` e `list_model_warnings`.
5. Só então execute `save_document`. Para modelos individuais, `backup_document` cria um ponto de restauração antes de lotes relevantes.
6. Para alterar o destino, use `save_as` conscientemente.

O bridge limita requisições a 1 MB, identifica falhas por `request_id` e não expõe stack traces quando `development_mode` é falso (a mensagem do erro é sempre retornada, para que a IA possa corrigir a chamada). Os logs ficam em `%LOCALAPPDATA%\RevitMcp\logs` (`revit_mcp_startup.log`, `revit_mcp_bridge.log`, `revit_mcp.log`).

A auditoria em Extensible Storage é auxiliar: se não puder ser gravada (documento somente leitura, elementos emprestados), o comando continua bem-sucedido e a falha vai para o log. Simulações (`execute_batch` sem `commit`) e comandos de arquivo (`open_document`, `new_project`, `save_as`) não são auditados.

As operações de escrita são registradas no próprio RVT por Extensible Storage. Use `list_audit_log` para inspecionar data, usuário, comando e resultado. `validate_room_program` recebe as áreas mínimas e quantidades definidas para cada contrato, sem alegar atender uma legislação local. `audit_documentation` aponta vistas imprimíveis fora de prancha. Em modelos workshared, `get_worksharing_status` e `get_element_worksharing_info` são somente leitura: o MCP não toma posse nem sincroniza automaticamente.

## Recuperação

- Se `bridge_health` falhar, execute `diagnose-revit-mcp.ps1` e siga a correção indicada; normalmente basta `reload-revit-mcp.ps1`.
- Se houver uma caixa de diálogo do Revit, o script não a confirma às cegas; corrija a situação no Revit e execute-o novamente.
- Para rotacionar o token, feche o Revit, remova apenas `%LOCALAPPDATA%\RevitMcp\bridge.json` e execute o script. Isto invalida processos MCP que tenham obtido o token anterior.

## Limites conscientes

O complemento é apropriado para automação local assistida e geração de modelos. Antes de uso comercial, mantenha revisão humana de geometria, normas locais, famílias, quantitativos, exportações IFC/DWG e qualquer operação de exclusão ou sobrescrita.
