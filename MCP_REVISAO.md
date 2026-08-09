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
