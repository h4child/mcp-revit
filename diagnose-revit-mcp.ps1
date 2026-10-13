<#
.SYNOPSIS
    Diagnostica por que o MCP do Revit não está respondendo.

.DESCRIPTION
    Somente leitura: não altera manifestos, tokens nem o modelo. Verifica, em
    ordem, cada elo da cadeia Cliente MCP -> server.py -> bridge -> add-in -> Revit
    e indica a correção para cada falha encontrada.
#>
param([string]$Python)

$ErrorActionPreference = 'Continue'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$revitVersion = '2025'
$addinClientId = '7C7E5B6E-2E7F-4E12-9A62-8A9F3B6E5C41'
$failures = 0

function Ok([string]$m)   { Write-Host "[ OK ] $m" -ForegroundColor Green }
function Warn([string]$m) { Write-Host "[AVISO] $m" -ForegroundColor Yellow }
function Fail([string]$m, [string]$fix) {
    $script:failures++
    Write-Host "[FALHA] $m" -ForegroundColor Red
    if ($fix) { Write-Host "        Correção: $fix" -ForegroundColor Gray }
}
function Section([string]$m) { Write-Host ""; Write-Host "== $m ==" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
Section 'Revit'
$revitExe = $env:REVIT_EXE
if (-not $revitExe) { $revitExe = "C:\Program Files\Autodesk\Revit $revitVersion\Revit.exe" }
if (Test-Path -LiteralPath $revitExe) { Ok "Revit encontrado: $revitExe" }
else { Fail "Revit.exe não encontrado em $revitExe" 'Defina REVIT_EXE e REVIT_API_DIR no .env.' }
$revitProcess = Get-Process Revit -ErrorAction SilentlyContinue | Select-Object -First 1
if ($revitProcess) { Ok "Revit em execução (PID $($revitProcess.Id))." }
else { Warn 'Revit não está aberto; o bridge só existe com o Revit aberto.' }

# ---------------------------------------------------------------------------
Section 'Manifestos .addin'
$dirs = @(
    (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$revitVersion"),
    (Join-Path $env:ProgramData "Autodesk\Revit\Addins\$revitVersion")
)
$ours = @()
foreach ($dir in $dirs) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $dir -Filter '*.addin' -File -ErrorAction SilentlyContinue) {
        $text = Get-Content -Raw -LiteralPath $file.FullName -ErrorAction SilentlyContinue
        if ($text -match [regex]::Escape($addinClientId) -or $text -match 'RevitMcp') { $ours += $file }
    }
}
$dllFromManifest = $null
if ($ours.Count -eq 0) {
    Fail 'Nenhum manifesto do RevitMcp instalado.' 'Execute reload-revit-mcp.ps1.'
}
elseif ($ours.Count -gt 1) {
    Fail ("Há {0} manifestos do RevitMcp ({1}). O Revit recusa AddInId duplicado." -f $ours.Count, (($ours | ForEach-Object FullName) -join '; ')) `
        'Execute reload-revit-mcp.ps1 (ele mantém apenas um) ou apague os excedentes.'
}
foreach ($file in $ours) {
    try {
        [xml]$xml = Get-Content -Raw -LiteralPath $file.FullName
        $addin = $xml.RevitAddIns.AddIn
        $assembly = [string]$addin.Assembly
        if ($ours.Count -eq 1) { Ok "Manifesto: $($file.FullName)" }
        if ([string]$addin.FullClassName -ne 'RevitMcp.App') { Fail "FullClassName inesperado em $($file.Name): $($addin.FullClassName)" 'Use RevitMcp.App.' }
        if (-not (Test-Path -LiteralPath $assembly)) { Fail "A DLL apontada não existe: $assembly" 'Execute reload-revit-mcp.ps1 para compilar e reinstalar.' }
        else { Ok "DLL apontada existe: $assembly"; $dllFromManifest = $assembly }
    }
    catch { Fail "Manifesto inválido (XML): $($file.FullName)" 'Apague-o e execute reload-revit-mcp.ps1.' }
}

# ---------------------------------------------------------------------------
Section 'DLL do add-in'
$dll = if ($dllFromManifest) { $dllFromManifest } else { Join-Path $projectRoot 'addin\bin\Release\RevitMcp.dll' }
if (Test-Path -LiteralPath $dll) {
    $info = Get-Item -LiteralPath $dll
    Ok ("{0} (versão {1}, compilada em {2})" -f $dll, $info.VersionInfo.FileVersion, $info.LastWriteTime)
    $zone = Get-Item -LiteralPath $dll -Stream Zone.Identifier -ErrorAction SilentlyContinue
    if ($zone) { Fail 'A DLL está marcada como baixada da internet (bloqueada).' "Unblock-File '$dll'" }
    $sig = Get-AuthenticodeSignature -LiteralPath $dll
    switch ($sig.Status) {
        'Valid'    { Ok "Assinatura válida: $($sig.SignerCertificate.Subject)" }
        'NotSigned' { Warn 'DLL não assinada: o Revit pergunta antes de carregar. Escolha "Sempre carregar".' }
        default    { Warn "Assinatura com status $($sig.Status). Se o Revit perguntar, escolha 'Sempre carregar'; ou execute reload-revit-mcp.ps1 para renovar o certificado." }
    }
}
else { Fail "DLL não encontrada: $dll" 'Execute reload-revit-mcp.ps1.' }

# ---------------------------------------------------------------------------
Section 'Token e configuração'
$bridgeConfig = Join-Path $env:LOCALAPPDATA 'RevitMcp\bridge.json'
$serverCopy = Join-Path $projectRoot 'server\.revit_mcp_bridge.json'
$state = $null
try { $state = Get-Content -Raw -LiteralPath $bridgeConfig | ConvertFrom-Json } catch { }
if ($state -and $state.token) { Ok "Token presente em $bridgeConfig" }
else { Fail "Token ausente em $bridgeConfig" 'Execute reload-revit-mcp.ps1 (o add-in também gera um ao iniciar).' }
try {
    $copy = Get-Content -Raw -LiteralPath $serverCopy | ConvertFrom-Json
    if ($state -and $copy.token -ne $state.token) {
        Warn 'A cópia server\.revit_mcp_bridge.json difere do token do Revit. O servidor usa o de %LOCALAPPDATA% primeiro; se rodar em sandbox, execute reload-revit-mcp.ps1 para sincronizar.'
    }
} catch { }
$port = if ($state -and $state.port) { [int]$state.port } else { 14733 }

# ---------------------------------------------------------------------------
Section "Bridge (127.0.0.1:$port)"
$listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($listener) {
    $owner = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    if ($owner -and $owner.ProcessName -ne 'Revit') { Fail "A porta $port está ocupada por $($owner.ProcessName) (PID $($owner.Id))." "Feche esse processo ou defina outra porta em bridge.json (\"port\") e REVIT_MCP_PORT." }
    else { Ok "Porta $port escutando (Revit)." }
}
elseif ($revitProcess) {
    Fail "O Revit está aberto mas nada escuta na porta ${port}: o add-in não carregou ou falhou ao iniciar." 'Veja os logs abaixo e a seção de journal do Revit.'
}
if ($listener -and $state) {
    try {
        $request = [System.Net.HttpWebRequest]::Create("http://127.0.0.1:$port/health")
        $request.Proxy = $null
        $request.Timeout = 5000
        $request.Headers.Add('X-Revit-Mcp-Token', $state.token)
        $response = $request.GetResponse()
        $body = (New-Object System.IO.StreamReader($response.GetResponseStream())).ReadToEnd()
        $response.Close()
        $health = $body | ConvertFrom-Json
        if ($health.ok) { Ok "Health OK: $body" } else { Fail "Health respondeu sem ok: $body" '' }
        if ($health.revit_ready -eq $false) { Warn 'O Revit ainda não ficou ocioso desde a inicialização; comandos aguardarão a UI liberar.' }
    }
    catch {
        $msg = $_.Exception.Message
        if ($msg -match '401') { Fail 'Bridge recusou o token (401).' 'O Revit carregou outro bridge.json; reinicie o Revit pelo reload-revit-mcp.ps1.' }
        else { Fail "Falha ao chamar /health: $msg" '' }
    }
}

# ---------------------------------------------------------------------------
Section 'Logs do add-in'
$logDir = Join-Path $env:LOCALAPPDATA 'RevitMcp\logs'
$startupLogs = @((Join-Path $logDir 'revit_mcp_startup.log'), (Join-Path $env:TEMP 'revit_mcp_startup.log')) | Where-Object { Test-Path -LiteralPath $_ }
if ($startupLogs) {
    $latest = $startupLogs | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending | Select-Object -First 1
    Ok "Log de inicialização: $latest"
    Get-Content -LiteralPath $latest -Tail 8 | ForEach-Object { Write-Host "        $_" }
}
else {
    Fail 'Nenhum log de inicialização: o Revit nunca executou OnStartup do RevitMcp.' `
        'O Revit não descobriu/aceitou o manifesto. Confira os itens de manifesto acima, a pergunta de segurança de add-in ao abrir o Revit e o journal abaixo.'
}
$errLog = Join-Path $logDir 'revit_mcp.log'
if (Test-Path -LiteralPath $errLog) {
    Write-Host "        Últimos erros ($errLog):"
    Get-Content -LiteralPath $errLog -Tail 10 | ForEach-Object { Write-Host "        $_" }
}

# ---------------------------------------------------------------------------
Section 'Journal do Revit'
$journalDir = Join-Path $env:LOCALAPPDATA "Autodesk\Revit\Autodesk Revit $revitVersion\Journals"
$journal = Get-ChildItem -LiteralPath $journalDir -Filter 'journal.*.txt' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($journal) {
    $hits = Select-String -LiteralPath $journal.FullName -Pattern 'RevitMcp|7C7E5B6E' -SimpleMatch:$false -ErrorAction SilentlyContinue | Select-Object -Last 10
    if ($hits) {
        Ok "Menções ao RevitMcp em $($journal.Name):"
        $hits | ForEach-Object { Write-Host ("        " + $_.Line.Trim()) }
    }
    else { Warn "O journal mais recente ($($journal.Name)) não menciona o RevitMcp: o manifesto não foi lido nessa sessão." }
}
else { Warn "Nenhum journal encontrado em $journalDir." }

# ---------------------------------------------------------------------------
Section 'Servidor MCP (Python)'
if (-not $Python) {
    $venvPython = Join-Path $projectRoot '.venv\Scripts\python.exe'
    $Python = if (Test-Path -LiteralPath $venvPython) { $venvPython } else { 'python' }
}
$pyCheck = & $Python -c "import sys, importlib.metadata as m; v=m.version('mcp'); print(sys.version.split()[0], v); sys.exit(0 if int(v.split('.')[0]) < 2 else 3)" 2>&1
switch ($LASTEXITCODE) {
    0 { Ok "Python/mcp: $pyCheck ($Python)" }
    3 { Fail "mcp 2.x instalado ($pyCheck): o servidor exige mcp 1.x." "& '$Python' -m pip install -r server\requirements.txt" }
    default { Fail "Python ou pacote mcp indisponível em '$Python': $pyCheck" 'Crie o venv: python -m venv .venv; .\.venv\Scripts\pip install -r server\requirements.txt' }
}
if ($LASTEXITCODE -eq 0 -and $listener) {
    $check = & $Python (Join-Path $projectRoot 'server\server.py') --check 2>&1
    if ($LASTEXITCODE -eq 0) { Ok 'server.py --check aprovado.' }
    else { Fail 'server.py --check falhou:' ''; $check | ForEach-Object { Write-Host "        $_" } }
}

Write-Host ""
if ($failures -eq 0) { Write-Host 'Diagnóstico concluído sem falhas.' -ForegroundColor Green; exit 0 }
Write-Host "Diagnóstico concluído com $failures falha(s)." -ForegroundColor Red
exit 1
