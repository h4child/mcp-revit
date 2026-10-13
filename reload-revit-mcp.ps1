param([string]$ProjectPath)

$ErrorActionPreference = 'Stop'
$revitExe = 'C:\Program Files\Autodesk\Revit 2025\Revit.exe'
$signTool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$envFile = Join-Path $projectRoot '.env'
if (Test-Path -LiteralPath $envFile) {
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -and -not $line.TrimStart().StartsWith('#')) {
            $value = $matches[2].Trim('"').Trim("'")
            [Environment]::SetEnvironmentVariable($matches[1], $value, 'Process')
        }
    }
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) { $ProjectPath = $env:REVIT_MCP_PROJECT_PATH }
$addinProject = Join-Path $projectRoot 'addin\RevitMcp.csproj'
$dllPath = Join-Path $projectRoot 'addin\bin\Release\RevitMcp.dll'
$addinTemplate = Join-Path $projectRoot 'addin\RevitMcp.addin.example'
$addinInstallDir = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$addinManifest = Join-Path $addinInstallDir 'RevitMcp.addin'
$addinInstallDirAllUsers = Join-Path $env:ProgramData 'Autodesk\Revit\Addins\2025'
$addinManifestAllUsers = Join-Path $addinInstallDirAllUsers 'RevitMcp.addin'
$stateDir = Join-Path $env:LOCALAPPDATA 'RevitMcp'
$bridgeConfig = Join-Path $stateDir 'bridge.json'
$serverBridgeConfig = Join-Path $projectRoot 'server\.revit_mcp_bridge.json'

# Segredo compartilhado entre o servidor MCP e o add-in. O bridge continua
# apenas em localhost, mas passa a rejeitar qualquer outro processo local sem
# o token. O token é preservado entre reloads para não derrubar o servidor MCP.
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$bridgeState = $null
try { $bridgeState = Get-Content -Raw -LiteralPath $bridgeConfig | ConvertFrom-Json } catch { }
if (-not $bridgeState -or [string]::IsNullOrWhiteSpace($bridgeState.token)) {
    $tokenBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($tokenBytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    [pscustomobject]@{ token = $token; development_mode = $false; created_at = (Get-Date).ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $bridgeConfig -Encoding UTF8
    $bridgeState = Get-Content -Raw -LiteralPath $bridgeConfig | ConvertFrom-Json
}
# O processo MCP pode estar em sandbox e não conseguir ler LocalAppData. Uma
# cópia privada ao lado do servidor mantém ambos os processos sincronizados.
$bridgeState | ConvertTo-Json | Set-Content -LiteralPath $serverBridgeConfig -Encoding UTF8
try {
    # Evita que outros perfis locais leiam o token de autenticação do bridge.
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls $serverBridgeConfig /inheritance:r /grant:r "${currentUser}:(R,W)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'icacls retornou falha' }
}
catch {
    throw "Não foi possível proteger o arquivo de token do MCP: $($_.Exception.Message)"
}

# Revit bloqueia a DLL do add-in. Salva e encerra a instância atual antes do build.
$revit = Get-Process Revit -ErrorAction SilentlyContinue | Select-Object -First 1
if ($revit) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    Add-Type -AssemblyName System.Windows.Forms
    try { [Microsoft.VisualBasic.Interaction]::AppActivate($revit.Id) | Out-Null }
    catch { $revit = $null }
    if ($revit) {
        try { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') } catch { }
        try { [System.Windows.Forms.SendKeys]::SendWait('^s') } catch { }
        Start-Sleep -Seconds 3

    # O Revit pode abrir confirmação de salvamento/aviso durante o fechamento.
    # Repetimos uma sequência conservadora de fechar + confirmar botão padrão.
    for ($tentativa = 1; $tentativa -le 4 -and -not $revit.HasExited; $tentativa++) {
        try { [Microsoft.VisualBasic.Interaction]::AppActivate($revit.Id) | Out-Null }
        catch { break }
        $null = $revit.CloseMainWindow()
        Start-Sleep -Seconds 2
        try { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') } catch { }
        Start-Sleep -Seconds 4
        $revit.Refresh()
    }
    if (-not $revit.HasExited) {
        throw 'O Revit manteve uma caixa de diálogo que não é seguro confirmar automaticamente. Feche-a uma vez e execute o script novamente.'
    }
    }
}

dotnet build $addinProject -c Release
$renewBefore = (Get-Date).AddDays(1)
$root = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq 'CN=RevitMcp Local Development Root' -and $_.HasPrivateKey
} | Sort-Object NotAfter -Descending | Select-Object -First 1
$existingLeaf = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq 'CN=RevitMcp Local Development' -and $_.HasPrivateKey
} | Sort-Object NotAfter -Descending | Select-Object -First 1
$newRoot = $false
if (-not $root -or $root.NotAfter -le $renewBefore) {
    $root = New-SelfSignedCertificate -Type Custom `
        -Subject 'CN=RevitMcp Local Development Root' `
        -KeyUsage CertSign, CRLSign, DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 4096 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -TextExtension @('2.5.29.19={critical}{text}CA=true') `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddDays(7)
    $rootCer = Join-Path $PSScriptRoot 'addin\RevitMcp-LocalDevelopment-Root.cer'
    Export-Certificate -Cert $root -FilePath $rootCer -Force | Out-Null
    Import-Certificate -FilePath $rootCer -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    $newRoot = $true
}
if ($newRoot -or -not $existingLeaf -or $existingLeaf.NotAfter -le $renewBefore) {
    $newLeaf = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject 'CN=RevitMcp Local Development' -Signer $root `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddDays(7)
    $leafCer = Join-Path $PSScriptRoot 'addin\RevitMcp-LocalDevelopment.cer'
    Export-Certificate -Cert $newLeaf -FilePath $leafCer -Force | Out-Null
    Import-Certificate -FilePath $leafCer -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null
}
if ($LASTEXITCODE -ne 0) { throw 'A compilação do MCP falhou.' }

# Assina cada build com o certificado local confiado pelo usuário.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq 'CN=RevitMcp Local Development' -and $_.HasPrivateKey
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { throw 'Certificado RevitMcp Local Development não encontrado.' }
& $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $dllPath
if ($LASTEXITCODE -ne 0) { throw 'A assinatura da DLL do MCP falhou.' }

# Gera o manifesto instalado com o caminho absoluto local sem versionar dados
# de usuário no repositório.
if (-not (Test-Path -LiteralPath $addinTemplate)) { throw "Template de manifesto não encontrado: $addinTemplate" }
New-Item -ItemType Directory -Force -Path $addinInstallDir | Out-Null
$manifestContent = (Get-Content -Raw -LiteralPath $addinTemplate).Replace('__REVIT_MCP_ASSEMBLY__', $dllPath)
$manifestContent | Set-Content -LiteralPath $addinManifest -Encoding UTF8

# O Revit também aceita um manifesto em ProgramData (todos os usuários). Em
# algumas instalações, a política de complementos só reconhece esse local.
# Grava ali também quando possível, sem falhar o reload se faltar permissão.
try {
    New-Item -ItemType Directory -Force -Path $addinInstallDirAllUsers -ErrorAction Stop | Out-Null
    $manifestContent | Set-Content -LiteralPath $addinManifestAllUsers -Encoding UTF8 -ErrorAction Stop
    Write-Host "Manifesto também instalado em $addinManifestAllUsers."
}
catch {
    Write-Host "Aviso: não foi possível instalar o manifesto em $addinManifestAllUsers ($($_.Exception.Message)). Mantendo apenas $addinManifest."
}

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    Start-Process -FilePath $revitExe
    Write-Host 'MCP recompilado e Revit aberto sem projeto. Configure REVIT_MCP_PROJECT_PATH no .env para abrir um .rvt específico.'
    return
}
if (-not (Test-Path -LiteralPath $ProjectPath)) { throw "Arquivo Revit não encontrado: $ProjectPath" }
Start-Process -FilePath $revitExe -ArgumentList ('"' + $ProjectPath + '"')
Write-Host "MCP recompilado e Revit aberto: $ProjectPath"
