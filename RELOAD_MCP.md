# Recarregamento automático do MCP no Revit

Use este comando sempre que o código do add-in for alterado:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\reload-revit-mcp.ps1
```

O script salva o projeto ativo, fecha o Revit, compila o `RevitMcp.dll` e reabre automaticamente a cópia de referência. O Revit não suporta hot reload de add-ins; reiniciar é a forma correta de carregar a DLL nova.
