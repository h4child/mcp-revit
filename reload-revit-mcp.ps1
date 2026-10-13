<#
.SYNOPSIS
    Compila, assina, instala e carrega o add-in RevitMcp no Revit 2025.

.DESCRIPTION
    1. Garante o token local compartilhado com o servidor MCP.
    2. Salva e fecha o Revit em execução (a DLL fica bloqueada enquanto ele está aberto).
    3. Compila addin\RevitMcp.csproj em Release.
    4. Assina a DLL com um certificado local de desenvolvimento (opcional).
    5. Instala UM único manifesto .addin e remove cópias duplicadas, que fazem o
       Revit recusar o add-in por AddInId repetido.
    6. Abre o Revit e aguarda o bridge responder em /health.

.PARAMETER ProjectPath
    Arquivo .rvt aberto após o build. Padrão: REVIT_MCP_PROJECT_PATH do .env.
.PARAMETER AllUsers
    Instala o manifesto em %ProgramData% (todos os usuários; requer administrador)
    em vez de %APPDATA% (usuário atual).
.PARAMETER NoSign
    Não assina a DLL. O Revit mostrará a pergunta de segurança de add-in não assinado
    na primeira carga; escolha "Sempre carregar".
.PARAMETER NoLaunch
    Apenas compila e instala; não abre o Revit.
.PARAMETER WaitSeconds
    Tempo máximo de espera pelo bridge após abrir o Revit (0 desativa).
#>
param(
    [string]$ProjectPath,
    [switch]$AllUsers,
    [switch]$NoSign,
    [switch]$NoLaunch,
    [int]$WaitSeconds = 180
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$revitVersion = '2025'
$addinClientId = '7C7E5B6E-2E7F-4E12-9A62-8A9F3B6E5C41'

# ---------------------------------------------------------------------------
# .env do repositório (não versionado)
# ---------------------------------------------------------------------------
$envFile = Join-Path $projectRoot '.env'
if (Test-Path -LiteralPath $envFile) {
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line.TrimStart().StartsWith('#')) { continue }
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
            $value = $matches[2].Trim('"').Trim("'")
            [Environment]::SetEnvironmentVariable($matches[1], $value, 'Process')
        }
    }
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) { $ProjectPath = $env:REVIT_MCP_PROJECT_PATH }

function Find-RevitExe {
    if ($env:REVIT_EXE -and (Test-Path -LiteralPath $env:REVIT_EXE)) { return $env:REVIT_EXE }
    $candidates = @()
    if ($env:REVIT_API_DIR) { $candidates += (Join-Path $env:REVIT_API_DIR 'Revit.exe') }
    $candidates += "C:\Program Files\Autodesk\Revit $revitVersion\Revit.exe"
    foreach ($key in @("HKLM:\SOFTWARE\Autodesk\Revit\Autodesk Revit $revitVersion", "HKLM:\SOFTWARE\Autodesk\Revit\$revitVersion")) {
        try {
            $dir = (Get-ItemProperty -LiteralPath $key -ErrorAction Stop).InstallationLocation
            if ($dir) { $candidates += (Join-Path $dir 'Revit.exe') }
        } catch { }
    }
    foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c)) { return $c } }
    return $null
}

function Find-SignTool {
    if ($env:SIGNTOOL -and (Test-Path -LiteralPath $env:SIGNTOOL)) { return $env:SIGNTOOL }
    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $found = Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($found) { return $found }
        $legacy = Join-Path $kits 'x64\signtool.exe'
        if (Test-Path -LiteralPath $legacy) { return $legacy }
    }
    return $null
}

function Invoke-BridgeHealth([string]$Url, [string]$Token) {
    # HttpWebRequest sem proxy: funciona no Windows PowerShell 5.1 e no 7, e não
    # deixa um proxy corporativo interceptar 127.0.0.1.
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Proxy = $null
    $request.Timeout = 5000
    $request.Headers.Add('X-Revit-Mcp-Token', $Token)
    $response = $request.GetResponse()
    try {
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
        return ($reader.ReadToEnd() | ConvertFrom-Json)
    }
    finally { $response.Close() }
}

$addinProject = Join-Path $projectRoot 'addin\RevitMcp.csproj'
$dllPath = Join-Path $projectRoot 'addin\bin\Release\RevitMcp.dll'
$addinTemplate = Join-Path $projectRoot 'addin\RevitMcp.addin.example'
$userAddinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$revitVersion"
$allUsersAddinDir = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$revitVersion"
$targetAddinDir = if ($AllUsers) { $allUsersAddinDir } else { $userAddinDir }
$targetManifest = Join-Path $targetAddinDir 'RevitMcp.addin'
$stateDir = Join-Path $env:LOCALAPPDATA 'RevitMcp'
$bridgeConfig = Join-Path $stateDir 'bridge.json'
$serverBridgeConfig = Join-Path $projectRoot 'server\.revit_mcp_bridge.json'

# ---------------------------------------------------------------------------
# Token compartilhado entre o servidor MCP e o add-in. O bridge continua
# apenas em localhost, mas rejeita qualquer outro processo local sem o token.
# O token é preservado entre reloads para não derrubar o servidor MCP.
# ---------------------------------------------------------------------------
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

# ---------------------------------------------------------------------------
# Revit bloqueia a DLL do add-in. Salva e encerra a instância atual antes do build.
# ---------------------------------------------------------------------------
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

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet não encontrado. Instale o .NET SDK 8 (https://dotnet.microsoft.com/download/dotnet/8.0).'
}
$revitExe = Find-RevitExe
if (-not $env:REVIT_API_DIR -and $revitExe) { $env:REVIT_API_DIR = Split-Path -Parent $revitExe }
$revitApiDir = if ($env:REVIT_API_DIR) { $env:REVIT_API_DIR } else { "C:\Program Files\Autodesk\Revit $revitVersion" }
if (-not (Test-Path -LiteralPath (Join-Path $revitApiDir 'RevitAPI.dll'))) {
    throw "RevitAPI.dll não encontrada. Defina REVIT_API_DIR no .env com a pasta de instalação do Revit $revitVersion."
}

dotnet build $addinProject -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'A compilação do add-in falhou.' }
if (-not (Test-Path -LiteralPath $dllPath)) { throw "A compilação não gerou $dllPath." }
# Remove a marca "baixado da internet", que impede o Revit de carregar a DLL.
Get-ChildItem -LiteralPath (Split-Path -Parent $dllPath) -File | Unblock-File -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# Assinatura (certificado local de desenvolvimento)
# ---------------------------------------------------------------------------
$signTool = if ($NoSign) { $null } else { Find-SignTool }
if (-not $NoSign -and -not $signTool) {
    Write-Warning 'signtool.exe não encontrado (Windows SDK). A DLL não será assinada; na primeira carga o Revit perguntará se deve carregar o add-in: escolha "Sempre carregar".'
}
if ($signTool) {
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
        $rootCer = Join-Path $projectRoot 'addin\RevitMcp-LocalDevelopment-Root.cer'
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
        $leafCer = Join-Path $projectRoot 'addin\RevitMcp-LocalDevelopment.cer'
        Export-Certificate -Cert $newLeaf -FilePath $leafCer -Force | Out-Null
        Import-Certificate -FilePath $leafCer -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null
    }
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq 'CN=RevitMcp Local Development' -and $_.HasPrivateKey
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw 'Certificado RevitMcp Local Development não encontrado.' }
    & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $dllPath
    if ($LASTEXITCODE -ne 0) { throw 'A assinatura da DLL do MCP falhou.' }
}

# ---------------------------------------------------------------------------
# Manifesto: exatamente UM arquivo com este AddInId. Dois manifestos com o
# mesmo ClientId (ex.: em %APPDATA% e %ProgramData%) fazem o Revit rejeitar
# o add-in com "AddInId duplicado".
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $addinTemplate)) { throw "Template de manifesto não encontrado: $addinTemplate" }
$escapedDll = [System.Security.SecurityElement]::Escape($dllPath)
$manifestContent = (Get-Content -Raw -LiteralPath $addinTemplate).Replace('__REVIT_MCP_ASSEMBLY__', $escapedDll)
$null = [xml]$manifestContent  # valida o XML antes de instalar

foreach ($dir in @($userAddinDir, $allUsersAddinDir)) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $dir -Filter '*.addin' -File -ErrorAction SilentlyContinue) {
        if ($file.FullName -eq $targetManifest) { continue }
        $text = Get-Content -Raw -LiteralPath $file.FullName -ErrorAction SilentlyContinue
        if ($text -and ($text -match [regex]::Escape($addinClientId) -or $text -match 'RevitMcp\.dll')) {
            try {
                Rename-Item -LiteralPath $file.FullName -NewName ($file.Name + '.disabled') -Force -ErrorAction Stop
                Write-Host "Manifesto duplicado desativado: $($file.FullName)"
            }
            catch {
                Write-Warning "Não foi possível desativar o manifesto duplicado $($file.FullName): $($_.Exception.Message). Remova-o manualmente (pode exigir administrador)."
            }
        }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $targetAddinDir -ErrorAction Stop | Out-Null
    $manifestContent | Set-Content -LiteralPath $targetManifest -Encoding UTF8 -ErrorAction Stop
}
catch {
    if ($AllUsers) { throw "Falha ao gravar $targetManifest. Execute o PowerShell como administrador ou omita -AllUsers. Detalhe: $($_.Exception.Message)" }
    throw
}
Write-Host "Manifesto instalado: $targetManifest"

if ($NoLaunch) {
    Write-Host 'Add-in compilado e instalado. Abra o Revit para carregá-lo.'
    return
}

# ---------------------------------------------------------------------------
# Abre o Revit e aguarda o bridge
# ---------------------------------------------------------------------------
if (-not $revitExe) { throw "Revit.exe $revitVersion não encontrado. Defina REVIT_EXE no .env." }
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    Start-Process -FilePath $revitExe
    Write-Host 'Revit aberto sem projeto. Configure REVIT_MCP_PROJECT_PATH no .env para abrir um .rvt específico.'
}
else {
    if (-not (Test-Path -LiteralPath $ProjectPath)) { throw "Arquivo Revit não encontrado: $ProjectPath" }
    Start-Process -FilePath $revitExe -ArgumentList ('"' + $ProjectPath + '"')
    Write-Host "Revit aberto: $ProjectPath"
}

if ($WaitSeconds -le 0) { return }
$port = if ($bridgeState.port) { [int]$bridgeState.port } else { 14733 }
$healthUrl = "http://127.0.0.1:$port/health"
Write-Host "Aguardando o bridge em $healthUrl (até $WaitSeconds s)..."
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    try {
        # O add-in pode ter regenerado o token; relê o arquivo a cada tentativa.
        $current = Get-Content -Raw -LiteralPath $bridgeConfig | ConvertFrom-Json
        $health = Invoke-BridgeHealth -Url $healthUrl -Token $current.token
        if ($health.ok) {
            Write-Host "Bridge ativo (porta $($health.port)). O MCP está pronto." -ForegroundColor Green
            return
        }
    }
    catch { }
}
Write-Warning "O bridge não respondeu em $WaitSeconds s. Se o Revit mostrou um aviso de segurança do add-in, escolha 'Sempre carregar'. Depois execute: PowerShell -ExecutionPolicy Bypass -File .\diagnose-revit-mcp.ps1"
