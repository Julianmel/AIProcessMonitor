using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace AIProcessMonitor
{
    class Program
    {
        private static int port = 3333;
        private static HttpListener listener;
        private static string embeddedHtml = null;
        private static readonly object cacheLock = new object();
        private static string cachedJson = null;
        private static DateTime lastScanTime = DateTime.MinValue;

        #region Win32 API Imports for Desktop & Window Management
        private const uint GW_ENABLEDPOPUP = 6;
        private const uint DESKTOP_ALL = 0x01FF;

        [DllImport("user32.dll")]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll")]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll")]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        public delegate bool EnumDesktopWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumDesktopWindowsProc lpfn, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpfn, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        #endregion

        static void Main(string[] args)
        {
            Console.Title = "AI Process Monitor • Server & API";
            Console.OutputEncoding = Encoding.UTF8;

            LoadEmbeddedHtml();

            int customPort;
            if (args.Length > 0 && int.TryParse(args[0], out customPort))
            {
                port = customPort;
            }

            StartServer();
        }

        private static void LoadEmbeddedHtml()
        {
            try
            {
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "public", "index.html");
                if (File.Exists(localPath))
                {
                    embeddedHtml = File.ReadAllText(localPath, Encoding.UTF8);
                    return;
                }
            }
            catch { }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("index.html"))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            embeddedHtml = reader.ReadToEnd();
                        }
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(embeddedHtml))
            {
                embeddedHtml = "<html><body><h1>AI Process Monitor</h1></body></html>";
            }
        }

        private static void StartServer()
        {
            bool started = false;
            while (!started && port < 3400)
            {
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add("http://localhost:" + port + "/");
                    listener.Start();
                    started = true;
                }
                catch (HttpListenerException)
                {
                    port++;
                }
            }

            if (!started)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERRO] Não foi possível iniciar o servidor HTTP entre as portas 3333 e 3400.");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("===============================================================");
            Console.WriteLine("        ⚡ AI PROCESS MONITOR • NATIVE EXECUTABLE ⚡          ");
            Console.WriteLine("===============================================================");
            Console.ResetColor();
            Console.WriteLine("  🌐 Painel Web   : http://localhost:" + port);
            Console.WriteLine("  📡 JSON da API  : http://localhost:" + port + "/api/processes");
            Console.WriteLine("  🎯 Focar Janela : http://localhost:" + port + "/api/focus?pid={pid}");
            Console.WriteLine("---------------------------------------------------------------");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  [ONLINE] Monitorando processos de IA com alertas de ação humana!");
            Console.ResetColor();
            Console.WriteLine("  Dica: Clique no processo no navegador para abrir a janela.");
            Console.WriteLine("  Pressione [Ctrl+C] ou feche esta janela para encerrar.");
            Console.WriteLine("===============================================================\n");

            try
            {
                Process.Start(new ProcessStartInfo("http://localhost:" + port) { UseShellExecute = true });
            }
            catch { }

            ThreadPool.QueueUserWorkItem(ListenLoop);

            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exitEvent.Set();
            };
            exitEvent.WaitOne();

            Console.WriteLine("\n[ENCERRANDO] Finalizando o servidor...");
            try { listener.Stop(); } catch { }
        }

        private static void ListenLoop(object state)
        {
            while (listener != null && listener.IsListening)
            {
                try
                {
                    var ctx = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(ProcessRequest, ctx);
                }
                catch
                {
                    if (listener == null || !listener.IsListening) break;
                }
            }
        }

        private static void ProcessRequest(object state)
        {
            var ctx = (HttpListenerContext)state;
            var req = ctx.Request;
            var res = ctx.Response;

            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            string path = req.Url.AbsolutePath.ToLowerInvariant();
            var sw = Stopwatch.StartNew();

            try
            {
                // 1. Dashboard HTML
                if (path == "/" || path == "/index.html")
                {
                    string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "public", "index.html");
                    string html = File.Exists(localPath) ? File.ReadAllText(localPath, Encoding.UTF8) : embeddedHtml;

                    byte[] buf = Encoding.UTF8.GetBytes(html);
                    res.ContentType = "text/html; charset=utf-8";
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.Close();
                    LogRequest(req.HttpMethod, path, 200, sw.ElapsedMilliseconds);
                    return;
                }

                // 2. API Processes
                if (path == "/api/processes")
                {
                    string json = GetProcessesJson();
                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.Close();
                    LogRequest(req.HttpMethod, path, 200, sw.ElapsedMilliseconds);
                    return;
                }

                // 3. API Focus Window on Click: /api/focus?pid=1234
                if (path == "/api/focus")
                {
                    string pidStr = req.QueryString["pid"];
                    int pidToFocus;
                    bool success = false;
                    string message = "PID inválido";

                    if (int.TryParse(pidStr, out pidToFocus))
                    {
                        success = FocusProcessWindow(pidToFocus, out message);
                    }

                    var ser = new JavaScriptSerializer();
                    string json = ser.Serialize(new
                    {
                        success = success,
                        pid = pidToFocus,
                        message = message
                    });

                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.Close();
                    LogRequest(req.HttpMethod, path + "?pid=" + pidStr, success ? 200 : 400, sw.ElapsedMilliseconds);
                    return;
                }

                // 4. API Health
                if (path == "/api/health")
                {
                    var ser = new JavaScriptSerializer();
                    string json = ser.Serialize(new
                    {
                        status = "ok",
                        timestamp = DateTime.UtcNow.ToString("o")
                    });
                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.Close();
                    LogRequest(req.HttpMethod, path, 200, sw.ElapsedMilliseconds);
                    return;
                }

                // 404
                res.StatusCode = 404;
                byte[] notFoundBuf = Encoding.UTF8.GetBytes("{\"error\":\"Endpoint não encontrado\"}");
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = notFoundBuf.Length;
                res.OutputStream.Write(notFoundBuf, 0, notFoundBuf.Length);
                res.Close();
                LogRequest(req.HttpMethod, path, 404, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                try
                {
                    res.StatusCode = 500;
                    byte[] errBuf = Encoding.UTF8.GetBytes("{\"error\":\"" + ex.Message.Replace("\"", "\\\"") + "\"}");
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = errBuf.Length;
                    res.OutputStream.Write(errBuf, 0, errBuf.Length);
                    res.Close();
                }
                catch { }
                LogRequest(req.HttpMethod, path, 500, sw.ElapsedMilliseconds);
            }
        }

        private static void LogRequest(string method, string path, int code, long ms)
        {
            Console.ForegroundColor = code == 200 ? ConsoleColor.DarkGray : ConsoleColor.Yellow;
            Console.WriteLine(string.Format("[{0:HH:mm:ss}] {1} {2} -> {3} ({4}ms)", DateTime.Now, method, path, code, ms));
            Console.ResetColor();
        }

        #region Window Focus Logic (Requisito 2 - Robust Desktop Window Activation)
        private static bool ActivateHwnd(IntPtr hWnd)
        {
            try
            {
                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, 9); // SW_RESTORE
                }
                else
                {
                    ShowWindow(hWnd, 5); // SW_SHOW
                }

                // Strategy 1: SwitchToThisWindow forces taskbar switch across desktop boundaries
                try { SwitchToThisWindow(hWnd, true); } catch { }

                // Strategy 2: AttachThreadInput bypass to overcome Windows foreground locks
                try
                {
                    IntPtr fgHwnd = GetForegroundWindow();
                    if (fgHwnd != IntPtr.Zero && fgHwnd != hWnd)
                    {
                        uint unusedPid;
                        uint fgThread = GetWindowThreadProcessId(fgHwnd, out unusedPid);
                        uint curThread = GetCurrentThreadId();
                        if (fgThread != 0 && fgThread != curThread)
                        {
                            AttachThreadInput(curThread, fgThread, true);
                            BringWindowToTop(hWnd);
                            SetForegroundWindow(hWnd);
                            AttachThreadInput(curThread, fgThread, false);
                        }
                    }
                }
                catch { }

                // Strategy 3: Virtual ALT key event bypass
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);

                BringWindowToTop(hWnd);
                return SetForegroundWindow(hWnd);
            }
            catch
            {
                return false;
            }
        }

        public static bool FocusProcessWindow(int targetPid, out string message)
        {
            try
            {
                Process p = null;
                try { p = Process.GetProcessById(targetPid); } catch { }
                string pName = p != null ? p.ProcessName : "";

                // Build family tree of PIDs (ancestors and children)
                var familyPids = new HashSet<int>();
                familyPids.Add(targetPid);

                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
                    {
                        var parentMap = new Dictionary<int, int>();
                        var childrenMap = new Dictionary<int, List<int>>();
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            try
                            {
                                int id = Convert.ToInt32(mo["ProcessId"]);
                                int par = Convert.ToInt32(mo["ParentProcessId"]);
                                parentMap[id] = par;
                                if (!childrenMap.ContainsKey(par)) childrenMap[par] = new List<int>();
                                childrenMap[par].Add(id);
                            }
                            catch { }
                        }

                        // Trace ancestors (e.g. agy -> powershell -> WindowsTerminal)
                        int curr = targetPid;
                        int depth = 0;
                        while (depth < 8 && parentMap.ContainsKey(curr))
                        {
                            curr = parentMap[curr];
                            familyPids.Add(curr);
                            depth++;
                        }

                        // Add children
                        if (childrenMap.ContainsKey(targetPid))
                        {
                            foreach (int ch in childrenMap[targetPid]) familyPids.Add(ch);
                        }
                    }
                }
                catch { }

                IntPtr foundHwnd = IntPtr.Zero;
                string foundTitle = "";

                // Tier 1: Check MainWindowHandle directly on target & family processes
                foreach (int pid in familyPids)
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        if (proc.MainWindowHandle != IntPtr.Zero && IsWindowVisible(proc.MainWindowHandle))
                        {
                            var sb = new StringBuilder(512);
                            GetWindowText(proc.MainWindowHandle, sb, 512);
                            string title = sb.ToString().Trim();
                            if (!string.IsNullOrEmpty(title))
                            {
                                foundHwnd = proc.MainWindowHandle;
                                foundTitle = title;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                // Helper window callback
                EnumDesktopWindowsProc matchPidCallback = (hWnd, lParam) =>
                {
                    uint winPid;
                    GetWindowThreadProcessId(hWnd, out winPid);

                    if (familyPids.Contains((int)winPid))
                    {
                        var sb = new StringBuilder(512);
                        GetWindowText(hWnd, sb, 512);
                        string title = sb.ToString().Trim();

                        if (!string.IsNullOrEmpty(title))
                        {
                            foundHwnd = hWnd;
                            foundTitle = title;
                            return false;
                        }
                        else if (foundHwnd == IntPtr.Zero)
                        {
                            foundHwnd = hWnd;
                        }
                    }
                    return true;
                };

                // Tier 2: Open Desktop (Default or Input Desktop)
                if (foundHwnd == IntPtr.Zero)
                {
                    IntPtr hDesk = OpenDesktop("Default", 0, false, DESKTOP_ALL);
                    if (hDesk == IntPtr.Zero) hDesk = OpenInputDesktop(0, false, DESKTOP_ALL);

                    if (hDesk != IntPtr.Zero)
                    {
                        EnumDesktopWindows(hDesk, matchPidCallback, IntPtr.Zero);
                        CloseDesktop(hDesk);
                    }
                }

                // Tier 3: Standard EnumWindows fallback
                if (foundHwnd == IntPtr.Zero)
                {
                    EnumWindows((hWnd, lParam) => matchPidCallback(hWnd, lParam), IntPtr.Zero);
                }

                // Tier 4: Fallback search by friendly application keywords
                if (foundHwnd == IntPtr.Zero)
                {
                    string fallbackKeyword = "";
                    if (pName.IndexOf("agy", StringComparison.OrdinalIgnoreCase) >= 0) fallbackKeyword = "Terminal";
                    else if (pName.IndexOf("cloudcode", StringComparison.OrdinalIgnoreCase) >= 0) fallbackKeyword = "Visual Studio Code";
                    else if (pName.IndexOf("copilot", StringComparison.OrdinalIgnoreCase) >= 0) fallbackKeyword = "Copilot";

                    if (!string.IsNullOrEmpty(fallbackKeyword))
                    {
                        EnumDesktopWindowsProc matchTitleCallback = (hWnd, lParam) =>
                        {
                            var sb = new StringBuilder(512);
                            GetWindowText(hWnd, sb, 512);
                            string title = sb.ToString().Trim();
                            if (!string.IsNullOrEmpty(title) && title.IndexOf(fallbackKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foundHwnd = hWnd;
                                foundTitle = title;
                                return false;
                            }
                            return true;
                        };

                        IntPtr hDesk = OpenDesktop("Default", 0, false, DESKTOP_ALL);
                        if (hDesk == IntPtr.Zero) hDesk = OpenInputDesktop(0, false, DESKTOP_ALL);
                        if (hDesk != IntPtr.Zero)
                        {
                            EnumDesktopWindows(hDesk, matchTitleCallback, IntPtr.Zero);
                            CloseDesktop(hDesk);
                        }

                        if (foundHwnd == IntPtr.Zero)
                        {
                            EnumWindows((hWnd, lParam) => matchTitleCallback(hWnd, lParam), IntPtr.Zero);
                        }
                    }
                }

                if (foundHwnd != IntPtr.Zero)
                {
                    ActivateHwnd(foundHwnd);
                    message = "Janela trazida para frente: " + (string.IsNullOrEmpty(foundTitle) ? pName : foundTitle);
                    return true;
                }

                message = "Nenhuma janela encontrada para este processo.";
                return false;
            }
            catch (Exception ex)
            {
                message = "Erro ao focar janela: " + ex.Message;
                return false;
            }
        }
        #endregion

        #region Process Scanning & Accurate Human Action Detection (Requisito 1)
        private static string GetProcessesJson()
        {
            lock (cacheLock)
            {
                if (cachedJson != null && (DateTime.Now - lastScanTime).TotalMilliseconds < 500)
                {
                    return cachedJson;
                }

                var sw = Stopwatch.StartNew();
                var list = ScanProcesses();
                sw.Stop();

                double totalMem = list.Sum(p => p.memoryMB);

                var payload = new
                {
                    status = "success",
                    timestamp = DateTime.UtcNow.ToString("o"),
                    scanDurationMs = sw.ElapsedMilliseconds,
                    count = list.Count,
                    totalMemoryMB = Math.Round(totalMem, 1),
                    processes = list
                };

                var serializer = new JavaScriptSerializer();
                cachedJson = serializer.Serialize(payload);
                lastScanTime = DateTime.Now;
                return cachedJson;
            }
        }

        private static List<ProcessInfo> ScanProcesses()
        {
            var results = new List<ProcessInfo>();
            DateTime now = DateTime.Now;

            string targetRegex = @"(?i)^(agy|cloudcode_cli|M365Copilot|ollama.*|lmstudio.*|lms|koboldcpp|jan|anythingllm|localai|vllm|tabby|cursor|claude|chatbox|msty|comfy.*)$";
            string aiKeywords = @"(?i)(torch|diffusion|tensor|model|inference|agent|llm|huggingface|gradio|streamlit|langchain|ollama|gemini|antigravity|copilot|claude|server_webcam|duet)";

            var allProcs = Process.GetProcesses();
            var candidates = new List<Process>();

            foreach (var p in allProcs)
            {
                string name = p.ProcessName;
                if (Regex.IsMatch(name, targetRegex) || name.Equals("node", StringComparison.OrdinalIgnoreCase) || name.StartsWith("python", StringComparison.OrdinalIgnoreCase) || name.Equals("py", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(p);
                }
            }

            if (candidates.Count == 0) return results;

            // Fetch command lines via WMI
            var cmdMap = new Dictionary<int, string>();
            try
            {
                var pidFilters = candidates.Select(c => "ProcessId = " + c.Id).ToArray();
                string query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE " + string.Join(" OR ", pidFilters);
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            string cmd = mo["CommandLine"] as string;
                            cmdMap[pid] = cmd ?? "";
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Check if any agent session specifically has an active question or permission prompt
            bool isExplicitQuestionPending = CheckActiveAgentPromptWaiting();

            foreach (var p in candidates)
            {
                try
                {
                    int id = p.Id;
                    string name = p.ProcessName;
                    string cmd = cmdMap.ContainsKey(id) ? cmdMap[id] : "";

                    bool isAi = false;
                    string friendlyName = name;
                    string category = "Outro";

                    if (Regex.IsMatch(name, "agy", RegexOptions.IgnoreCase))
                    {
                        isAi = true;
                        friendlyName = "Google Antigravity CLI";
                        category = "Agente de IA / CLI";
                    }
                    else if (Regex.IsMatch(name, "cloudcode", RegexOptions.IgnoreCase))
                    {
                        isAi = true;
                        friendlyName = "Google Gemini Code Assist";
                        category = "Assistente de IDE";
                    }
                    else if (Regex.IsMatch(name, "M365Copilot|copilot", RegexOptions.IgnoreCase))
                    {
                        isAi = true;
                        friendlyName = "Microsoft Copilot";
                        category = "App Desktop de IA";
                    }
                    else if (Regex.IsMatch(name, "ollama", RegexOptions.IgnoreCase))
                    {
                        isAi = true;
                        friendlyName = "Ollama Server";
                        category = "Servidor Local de IA";
                    }
                    else if (Regex.IsMatch(name, "lmstudio|lms", RegexOptions.IgnoreCase))
                    {
                        isAi = true;
                        friendlyName = "LM Studio";
                        category = "App Desktop / Servidor Local";
                    }
                    else if (Regex.IsMatch(name, "cursor", RegexOptions.IgnoreCase))
                    {
                        isAi = true;
                        friendlyName = "Cursor Editor";
                        category = "IDE com IA";
                    }
                    else if (Regex.IsMatch(name, "claude", RegexOptions.IgnoreCase))
                    {
                        isAi = true;
                        friendlyName = "Claude Desktop";
                        category = "App Desktop de IA";
                    }
                    else if (name.Equals("node", StringComparison.OrdinalIgnoreCase) || name.StartsWith("python", StringComparison.OrdinalIgnoreCase) || name.Equals("py", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Regex.IsMatch(cmd, aiKeywords))
                        {
                            isAi = true;
                            if (cmd.IndexOf("server_webcam", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                friendlyName = "Assistente Visual por Voz com IA";
                                category = "Aplicação Web / Assistente";
                            }
                            else
                            {
                                friendlyName = name + " [IA Script]";
                                category = "Script de IA";
                            }
                        }
                    }

                    if (isAi)
                    {
                        DateTime startTime = DateTime.MinValue;
                        try { startTime = p.StartTime; } catch { }

                        double totalSec = 0;
                        string uptimeStr = "N/A";
                        string startStr = "N/A";

                        if (startTime != DateTime.MinValue)
                        {
                            TimeSpan diff = now - startTime;
                            totalSec = Math.Floor(diff.TotalSeconds);
                            var parts = new List<string>();
                            if (diff.Days > 0) parts.Add(diff.Days + "d");
                            if (diff.Hours > 0) parts.Add(diff.Hours + "h");
                            if (diff.Minutes > 0) parts.Add(diff.Minutes + "m");
                            parts.Add(diff.Seconds + "s");
                            uptimeStr = string.Join(" ", parts.ToArray());
                            startStr = startTime.ToString("yyyy-MM-dd HH:mm:ss");
                        }

                        double memMB = 0;
                        try { memMB = Math.Round(p.WorkingSet64 / 1048576.0, 1); } catch { }

                        // ACCURATE HUMAN ACTION DETECTION (NO FALSE ALARMS)
                        string statusReason;
                        bool needsHuman = CheckRealHumanActionRequired(p, name, cmd, isExplicitQuestionPending, out statusReason);

                        results.Add(new ProcessInfo
                        {
                            pid = id,
                            processName = name,
                            friendlyName = friendlyName,
                            category = category,
                            startTime = startStr,
                            uptimeSeconds = totalSec,
                            uptimeHuman = uptimeStr,
                            memoryMB = memMB,
                            commandLine = cmd,
                            needsHumanInput = needsHuman,
                            statusReason = statusReason
                        });
                    }
                }
                catch { }
            }

            return results;
        }

        /// <summary>
        /// Real, accurate check for whether a process TRULY needs human action right now:
        /// 1. Modal dialog, confirmation popup or prompt window waiting on screen.
        /// 2. Window title specifically indicating a question, approval, or input prompt.
        /// 3. An active unanswered question or permission prompt explicitly presented to the user.
        /// </summary>
        private static bool CheckRealHumanActionRequired(Process p, string name, string cmd, bool isExplicitQuestionPending, out string statusReason)
        {
            statusReason = "Ativo";

            // 1. Any GUI window with a modal confirmation popup / dialog waiting for human response
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    IntPtr popup = GetWindow(p.MainWindowHandle, GW_ENABLEDPOPUP);
                    if (popup != IntPtr.Zero && popup != p.MainWindowHandle && IsWindowVisible(popup))
                    {
                        statusReason = "Caixa de diálogo / Confirmação aguardando resposta";
                        return true;
                    }
                }
            }
            catch { }

            // 2. Window Title explicitly asking for human confirmation, permission or prompt
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                {
                    string title = p.MainWindowTitle;
                    if (Regex.IsMatch(title, @"(?i)(aguardando|confirmar|confirm|pergunta|autorizar|permission|approval|\?)"))
                    {
                        statusReason = "Aguardando confirmação na janela";
                        return true;
                    }
                }
            }
            catch { }

            // 3. Explicit question / prompt pending in agent session
            if (isExplicitQuestionPending && name.IndexOf("agy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusReason = "Pergunta ou confirmação aguardando resposta humana";
                return true;
            }

            // Normal process statuses (clean and informative, NO false alarms)
            if (cmd.IndexOf("server_webcam", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusReason = "Servidor Web Ativo";
            }
            else if (name.IndexOf("cloudcode", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusReason = "Serviço IDE Ativo";
            }
            else if (name.IndexOf("M365Copilot", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusReason = "Em segundo plano";
            }
            else if (name.IndexOf("agy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusReason = "Agente CLI Ativo";
            }
            else
            {
                statusReason = "Em execução";
            }

            return false;
        }

        /// <summary>
        /// Checks if there is an explicit UNANSWERED question or confirmation prompt presented by the agent
        /// (e.g. ask_question tool or permission check).
        /// </summary>
        private static bool CheckActiveAgentPromptWaiting()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string brainDir = Path.Combine(userProfile, ".gemini", "antigravity-cli", "brain");
                if (!Directory.Exists(brainDir)) return false;

                var dirs = new DirectoryInfo(brainDir).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTime)
                    .Take(2);

                foreach (var d in dirs)
                {
                    string transcriptPath = Path.Combine(d.FullName, ".system_generated", "logs", "transcript.jsonl");
                    if (File.Exists(transcriptPath))
                    {
                        using (var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs, Encoding.UTF8))
                        {
                            string lastLine = null;
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (!string.IsNullOrWhiteSpace(line)) lastLine = line;
                            }

                            if (lastLine != null)
                            {
                                // Only trigger if the assistant is explicitly blocked waiting on ask_question
                                if (lastLine.Contains("\"ask_question\"") && !lastLine.Contains("\"type\":\"USER_INPUT\""))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
        }
        #endregion
    }

    public class ProcessInfo
    {
        public int pid { get; set; }
        public string processName { get; set; }
        public string friendlyName { get; set; }
        public string category { get; set; }
        public string startTime { get; set; }
        public double uptimeSeconds { get; set; }
        public string uptimeHuman { get; set; }
        public double memoryMB { get; set; }
        public string commandLine { get; set; }
        public bool needsHumanInput { get; set; }
        public string statusReason { get; set; }
    }
}
