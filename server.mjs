import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn, exec } from 'node:child_process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const PORT = process.env.PORT ? parseInt(process.env.PORT, 10) : 3333;
const SCANNER_PATH = path.join(__dirname, 'scanner.ps1');
const INDEX_HTML_PATH = path.join(__dirname, 'public', 'index.html');

let cachedData = null;
let lastScanTime = 0;
let isScanning = false;
let scanQueue = [];

function runScanner() {
  return new Promise((resolve, reject) => {
    const now = Date.now();
    if (cachedData && (now - lastScanTime < 600)) {
      return resolve(cachedData);
    }

    if (isScanning) {
      scanQueue.push({ resolve, reject });
      return;
    }

    isScanning = true;
    const t0 = performance.now();

    const ps = spawn('pwsh', [
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-File', SCANNER_PATH
    ]);

    let stdout = '';
    let stderr = '';

    ps.stdout.on('data', (d) => { stdout += d.toString(); });
    ps.stderr.on('data', (d) => { stderr += d.toString(); });

    ps.on('close', (code) => {
      isScanning = false;
      const durationMs = Math.round(performance.now() - t0);

      if (code !== 0) {
        const err = new Error(stderr || `Exit code ${code}`);
        reject(err);
        while (scanQueue.length) scanQueue.shift().reject(err);
        return;
      }

      let parsed = [];
      try {
        const trimmed = stdout.trim();
        if (trimmed) {
          parsed = JSON.parse(trimmed);
          if (!Array.isArray(parsed)) parsed = [parsed];
        }
      } catch (e) {
        console.error('Failed to parse scanner output JSON:', e);
      }

      const totalMemoryMB = parsed.reduce((acc, p) => acc + (p.memoryMB || 0), 0);

      const result = {
        status: 'success',
        timestamp: new Date().toISOString(),
        scanDurationMs: durationMs,
        count: parsed.length,
        totalMemoryMB: Math.round(totalMemoryMB * 10) / 10,
        processes: parsed
      };

      cachedData = result;
      lastScanTime = Date.now();

      resolve(result);
      while (scanQueue.length) scanQueue.shift().resolve(result);
    });

    ps.on('error', (err) => {
      isScanning = false;
      reject(err);
      while (scanQueue.length) scanQueue.shift().reject(err);
    });
  });
}

function focusWindow(pid) {
  return new Promise((resolve) => {
    const psCmd = `
$pidTarget = ${pid}
$p = Get-Process -Id $pidTarget -ErrorAction SilentlyContinue
if ($p -and $p.ProcessName -like "*agy*") {
    wt.exe -w 0 focus-tab
    Write-Output "Janela do Windows Terminal focada"
    exit
}
if ($p -and $p.ProcessName -like "*cloudcode*") {
    code.cmd -r
    Write-Output "Janela do VS Code focada"
    exit
}
$ws = New-Object -ComObject WScript.Shell
$ws.AppActivate($pidTarget)
`;
    exec(`pwsh -NoProfile -Command "${psCmd.replace(/"/g, '\\"')}"`, (err, stdout) => {
      resolve({ success: true, message: stdout.trim() || 'Foco acionado' });
    });
  });
}

const server = http.createServer(async (req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');

  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return;
  }

  const reqUrl = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
  const pathname = reqUrl.pathname;

  // 1. Dashboard
  if (pathname === '/' || pathname === '/index.html') {
    try {
      const html = fs.readFileSync(INDEX_HTML_PATH, 'utf-8');
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(html);
    } catch (e) {
      res.writeHead(500, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('Erro: ' + e.message);
    }
    return;
  }

  // 2. GET /api/processes
  if (pathname === '/api/processes') {
    try {
      const data = await runScanner();
      res.writeHead(200, {
        'Content-Type': 'application/json; charset=utf-8',
        'Cache-Control': 'no-cache, no-store, must-revalidate'
      });
      res.end(JSON.stringify(data, null, 2));
    } catch (err) {
      res.writeHead(500, { 'Content-Type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify({ status: 'error', message: err.message }));
    }
    return;
  }

  // 3. GET /api/focus?pid=1234
  if (pathname === '/api/focus') {
    const pid = parseInt(reqUrl.searchParams.get('pid'), 10);
    if (!pid) {
      res.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify({ success: false, message: 'PID inválido' }));
      return;
    }
    const result = await focusWindow(pid);
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(result));
    return;
  }

  // 4. Health
  if (pathname === '/api/health') {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({ status: 'ok', timestamp: new Date().toISOString() }));
    return;
  }

  res.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify({ error: 'Endpoint não encontrado' }));
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`🚀 AI Process Monitor online em http://localhost:${PORT}`);
});
