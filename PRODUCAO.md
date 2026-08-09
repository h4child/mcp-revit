# Operação em produção — Revit MCP

## Segurança e inicialização

Execute `reload-revit-mcp.ps1` para compilar, assinar e abrir o Revit com o complemento atualizado. O script cria, uma única vez, o segredo local em `%LOCALAPPDATA%\RevitMcp\bridge.json`.

- O bridge aceita somente `127.0.0.1`.
- Cada chamada exige o cabeçalho `X-Revit-Mcp-Token`.
- O servidor Python lê esse segredo a cada chamada; recarregar o Revit não exige reiniciar o MCP.
- O arquivo de segredo não deve ser movido para o repositório, compartilhado, nem enviado por chat.
- A cópia operacional em `server/.revit_mcp_bridge.json` é ignorada pelo Git e recebe ACL somente para o usuário atual durante o reload.

## Operação segura

1. Execute `bridge_health` antes de qualquer lote.
2. Após mudanças, execute `validate_document` e `list_model_warnings`.
3. Só então execute `save_document`.
4. Para alterar o destino, use `save_as` conscientemente.

O bridge limita requisições a 1 MB, identifica falhas por `request_id` e não expõe stack traces quando `development_mode` é falso. Os logs ficam em `%TEMP%\revit_mcp.log` e ao lado da DLL do complemento.

## Recuperação

- Se `bridge_health` falhar, execute `reload-revit-mcp.ps1`.
- Se houver uma caixa de diálogo do Revit, o script não a confirma às cegas; corrija a situação no Revit e execute-o novamente.
- Para rotacionar o token, feche o Revit, remova apenas `%LOCALAPPDATA%\RevitMcp\bridge.json` e execute o script. Isto invalida processos MCP que tenham obtido o token anterior.

## Limites conscientes

O complemento é apropriado para automação local assistida e geração de modelos. Antes de uso comercial, mantenha revisão humana de geometria, normas locais, famílias, quantitativos, exportações IFC/DWG e qualquer operação de exclusão ou sobrescrita.
