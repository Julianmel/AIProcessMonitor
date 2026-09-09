[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$now = Get-Date

$targetRegex = '(?i)^(agy|cloudcode_cli|M365Copilot|ollama.*|lmstudio.*|lms|koboldcpp|jan|anythingllm|localai|vllm|tabby|cursor|claude|chatbox|msty|comfy.*)$'

# Get matching processes
$procs = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -match $targetRegex -or $_.ProcessName -in 'node', 'python', 'py'
}

if (-not $procs) {
    Write-Output "[]"
    exit
}

# Collect target PIDs
$pids = $procs | Select-Object -ExpandProperty Id

# Query WMI only for candidate processes for CommandLine
$cmdLines = @{}
try {
    $filter = ($pids | ForEach-Object { "ProcessId = $_" }) -join " OR "
    Get-CimInstance Win32_Process -Filter $filter -ErrorAction SilentlyContinue | ForEach-Object {
        $cmdLines[[int]$_.ProcessId] = [string]$_.CommandLine
    }
} catch {}

$results = [System.Collections.Generic.List[PSObject]]::new()

$aiKeywords = '(?i)(torch|diffusion|tensor|model|inference|agent|llm|huggingface|gradio|streamlit|langchain|ollama|gemini|antigravity|copilot|claude|server_webcam|duet)'

# Check Antigravity CLI recent sessions
$isAgyWaiting = $false
try {
    $brain = "$env:USERPROFILE\.gemini\antigravity-cli\brain"
    if (Test-Path $brain) {
        $recent = Get-ChildItem $brain -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 3
        foreach ($d in $recent) {
            $t = "$($d.FullName)\.system_generated\logs\transcript.jsonl"
            if (Test-Path $t) {
                $last = Get-Content $t -Tail 1 | ConvertFrom-Json -ErrorAction SilentlyContinue
                if ($last) {
                    if ($last.type -eq 'PLANNER_RESPONSE' -and $last.status -eq 'DONE') { $isAgyWaiting = $true; break }
                    if ($last.content -like "*ask_question*") { $isAgyWaiting = $true; break }
                }
            }
        }
    }
} catch {}

foreach ($p in $procs) {
    $id = [int]$p.Id
    $name = $p.ProcessName
    $cmd = if ($cmdLines.ContainsKey($id)) { [string]$cmdLines[$id] } else { "" }
    $isAi = $false
    $friendlyName = $name
    $category = "Outro"

    if ($name -match 'agy') {
        $isAi = $true
        $friendlyName = "Google Antigravity CLI"
        $category = "Agente de IA / CLI"
    } elseif ($name -match 'cloudcode') {
        $isAi = $true
        $friendlyName = "Google Gemini Code Assist"
        $category = "Assistente de IDE"
    } elseif ($name -match 'M365Copilot|copilot') {
        $isAi = $true
        $friendlyName = "Microsoft Copilot"
        $category = "App Desktop de IA"
    } elseif ($name -match 'ollama') {
        $isAi = $true
        $friendlyName = "Ollama Server"
        $category = "Servidor Local de IA"
    } elseif ($name -match 'lmstudio|lms') {
        $isAi = $true
        $friendlyName = "LM Studio"
        $category = "App Desktop / Servidor Local"
    } elseif ($name -match 'cursor') {
        $isAi = $true
        $friendlyName = "Cursor Editor"
        $category = "IDE com IA"
    } elseif ($name -match 'claude') {
        $isAi = $true
        $friendlyName = "Claude Desktop"
        $category = "App Desktop de IA"
    } elseif ($name -in 'node', 'python', 'py') {
        if ($cmd -match $aiKeywords) {
            $isAi = $true
            if ($cmd -match 'server_webcam') {
                $friendlyName = "Assistente Visual por Voz com IA"
                $category = "Aplicação Web / Assistente"
            } else {
                $friendlyName = "$name [IA Script]"
                $category = "Script de IA"
            }
        }
    }

    if ($isAi) {
        $st = $null
        $stStr = "N/A"
        $totalSec = 0
        $uptimeStr = "N/A"
        try {
            $st = $p.StartTime
            $diff = $now - $st
            $totalSec = [math]::Floor($diff.TotalSeconds)
            $parts = @()
            if ($diff.Days -gt 0) { $parts += "$($diff.Days)d" }
            if ($diff.Hours -gt 0) { $parts += "$($diff.Hours)h" }
            if ($diff.Minutes -gt 0) { $parts += "$($diff.Minutes)m" }
            $parts += "$($diff.Seconds)s"
            $uptimeStr = $parts -join " "
            $stStr = $st.ToString("yyyy-MM-dd HH:mm:ss")
        } catch {}

        $mem = 0
        try {
            $mem = [math]::Round($p.WorkingSet64 / 1MB, 1)
        } catch {}

        $needsHuman = $false
        $statusReason = "Em execução"

        if ($name -match 'agy') {
            if ($isAgyWaiting) {
                $needsHuman = $true
                $statusReason = "Aguardando resposta / comando do usuário"
            }
        } else {
            try {
                $waitCount = 0
                foreach ($th in $p.Threads) {
                    if ($th.ThreadState -eq 'Wait' -and $th.WaitReason -eq 'UserRequest') { $waitCount++ }
                }
                if ($waitCount -gt 0 -and $waitCount -ge ($p.Threads.Count / 2)) {
                    $needsHuman = $true
                    $statusReason = "Aguardando entrada no console"
                }
            } catch {}
        }

        $results.Add([PSCustomObject]@{
            pid             = $id
            processName     = $name
            friendlyName    = $friendlyName
            category        = $category
            startTime       = $stStr
            uptimeSeconds   = $totalSec
            uptimeHuman     = $uptimeStr
            memoryMB        = $mem
            commandLine     = $cmd
            needsHumanInput = $needsHuman
            statusReason    = $statusReason
        })
    }
}

$results | ConvertTo-Json -Compress
