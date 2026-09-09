using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("AI Process Monitor")]
[assembly: AssemblyDescription("Monitor de Processos de IA e Ação Humana em Tempo Real")]
[assembly: AssemblyCompany("Julianmel")]
[assembly: AssemblyProduct("AI Process Monitor")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2.0")]

namespace AIProcessMonitor
{
    public static class Program
    {
        public const string AppVersion = "1.2.0";
        public const string BuildDate = "2026-09-09";

        public static int port = 3333;
        private static HttpListener listener;
        private static string embeddedHtml = null;
        private static readonly object cacheLock = new object();
        private static string cachedJson = null;
        private static List<ProcessInfo> cachedProcessList = new List<ProcessInfo>();
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

        [STAThread]
        static void Main(string[] args)
        {
            LoadEmbeddedHtml();

            int customPort;
            if (args.Length > 0 && int.TryParse(args[0], out customPort))
            {
                port = customPort;
            }

            bool headless = false;
            foreach (var a in args)
            {
                if (a.Equals("--headless", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--no-gui", StringComparison.OrdinalIgnoreCase))
                {
                    headless = true;
                }
            }

            // Start background HTTP API server
            StartServer();

            if (headless)
            {
                var exitEvent = new ManualResetEvent(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; exitEvent.Set(); };
                exitEvent.WaitOne();
                try { if (listener != null) listener.Stop(); } catch { }
            }
            else
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm(port));
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), ex.ToString());
                    }
                    catch { }
                }
            }
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
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("index.html"))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            embeddedHtml = reader.ReadToEnd();
                            return;
                        }
                    }
                }
            }
            catch { }

            if (embeddedHtml == null)
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

            if (started)
            {
                ThreadPool.QueueUserWorkItem(ListenLoop);
            }
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

            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            try
            {
                string path = req.Url.AbsolutePath;

                if (path == "/" || path == "/index.html")
                {
                    byte[] buf = Encoding.UTF8.GetBytes(embeddedHtml);
                    res.ContentType = "text/html; charset=utf-8";
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.Close();
                    return;
                }

                if (path == "/api/processes")
                {
                    string json = GetProcessesJson();
                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.Close();
                    return;
                }

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
                        message = message,
                        version = AppVersion
                    });

                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.Close();
                    return;
                }

                if (path == "/api/health")
                {
                    var ser = new JavaScriptSerializer();
                    string json = ser.Serialize(new
                    {
                        status = "ok",
                        version = AppVersion,
                        buildDate = BuildDate,
                        timestamp = DateTime.UtcNow.ToString("o")
                    });

                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.Close();
                    return;
                }

                res.StatusCode = 404;
                byte[] notFoundBuf = Encoding.UTF8.GetBytes("{\"error\":\"Endpoint não encontrado\"}");
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = notFoundBuf.Length;
                res.OutputStream.Write(notFoundBuf, 0, notFoundBuf.Length);
                res.Close();
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
            }
        }

        #region Window Focus Logic
        public static bool ActivateHwnd(IntPtr hWnd)
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

                try { SwitchToThisWindow(hWnd, true); } catch { }

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

                        int curr = targetPid;
                        int depth = 0;
                        while (depth < 8 && parentMap.ContainsKey(curr))
                        {
                            curr = parentMap[curr];
                            familyPids.Add(curr);
                            depth++;
                        }

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

                // Tier 4: Fallback search by friendly keywords
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

        #region Process Scanning & Accurate Human Action Detection
        public static string GetProcessesJson()
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

                cachedProcessList = list;
                double totalMem = list.Sum(p => p.memoryMB);

                var payload = new
                {
                    status = "success",
                    version = AppVersion,
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

        public static List<ProcessInfo> ScanProcesses()
        {
            var rawList = new List<RawProc>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, CommandLine, WorkingSetSize FROM Win32_Process"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            string name = mo["Name"] != null ? mo["Name"].ToString() : "";
                            string cmd = mo["CommandLine"] != null ? mo["CommandLine"].ToString() : "";
                            ulong ws = mo["WorkingSetSize"] != null ? Convert.ToUInt64(mo["WorkingSetSize"]) : 0;
                            rawList.Add(new RawProc { Pid = pid, Name = name, CommandLine = cmd, WorkingSet = ws });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            var startTimeMap = new Dictionary<int, DateTime>();
            foreach (var p in Process.GetProcesses())
            {
                try { startTimeMap[p.Id] = p.StartTime; } catch { }
            }

            // Detect active agent prompts and tool authorization pauses across all sessions in brain/
            Dictionary<int, AgentPromptStatus> agentStatusMap;
            DetectAgentPrompts(rawList, startTimeMap, out agentStatusMap);

            var aiResults = new List<ProcessInfo>();

            foreach (var rp in rawList)
            {
                string pName = rp.Name ?? "";
                string pCmd = rp.CommandLine ?? "";

                if (pName.IndexOf("AIProcessMonitor", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                string friendly = null;
                string category = null;

                if (pCmd.IndexOf("server_webcam.mjs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pCmd.IndexOf("server_webcam", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Assistente Visual por Voz com IA";
                    category = "Aplicação Web / Assistente";
                }
                else if (pName.IndexOf("cloudcode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("cloudcode_cli", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Google Gemini Code Assist";
                    category = "Assistente de IDE";
                }
                else if (pName.IndexOf("M365Copilot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("M365Copilot", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Microsoft Copilot";
                    category = "App Desktop de IA";
                }
                else if (pName.IndexOf("agy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("\\agy.exe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("\\agy ", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Google Antigravity CLI";
                    category = "Agente de IA / CLI";
                }
                else if (pCmd.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Anthropic Claude";
                    category = "Assistente de IA";
                }
                else if (pCmd.IndexOf("ollama", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pName.IndexOf("ollama", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Ollama Local LLM";
                    category = "Servidor de IA Local";
                }

                if (friendly != null)
                {
                    DateTime start = DateTime.Now;
                    if (startTimeMap.ContainsKey(rp.Pid)) start = startTimeMap[rp.Pid];

                    double uptimeSec = Math.Max(0, (DateTime.Now - start).TotalSeconds);
                    double memMB = Math.Round(rp.WorkingSet / (1024.0 * 1024.0), 1);

                    string statusReason;
                    bool needsInput = DetermineAccurateHumanAction(rp.Pid, pName, pCmd, agentStatusMap, out statusReason);

                    string cleanName = pName;
                    if (cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        cleanName = cleanName.Substring(0, cleanName.Length - 4);

                    aiResults.Add(new ProcessInfo
                    {
                        pid = rp.Pid,
                        processName = cleanName,
                        friendlyName = friendly,
                        category = category,
                        startTime = start.ToString("yyyy-MM-dd HH:mm:ss"),
                        uptimeSeconds = Math.Round(uptimeSec),
                        uptimeHuman = FormatUptime(uptimeSec),
                        memoryMB = memMB,
                        commandLine = pCmd,
                        needsHumanInput = needsInput,
                        statusReason = statusReason
                    });
                }
            }

            return aiResults.OrderByDescending(p => p.needsHumanInput)
                            .ThenByDescending(p => p.memoryMB)
                            .ToList();
        }

        private static void DetectAgentPrompts(List<RawProc> rawList, Dictionary<int, DateTime> startTimeMap, out Dictionary<int, AgentPromptStatus> pidStatusMap)
        {
            pidStatusMap = new Dictionary<int, AgentPromptStatus>();

            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string brainDir = Path.Combine(userProfile, ".gemini", "antigravity-cli", "brain");
                if (!Directory.Exists(brainDir)) return;

                var dirs = new DirectoryInfo(brainDir).GetDirectories();
                var sessionInfos = new List<SessionInfo>();

                foreach (var d in dirs)
                {
                    try
                    {
                        string transcriptPath = Path.Combine(d.FullName, ".system_generated", "logs", "transcript.jsonl");
                        if (!File.Exists(transcriptPath)) continue;

                        var fi = new FileInfo(transcriptPath);
                        // Only consider sessions modified in the last 24 hours
                        if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours > 24) continue;

                        string lastLine = null;
                        using (var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (!string.IsNullOrWhiteSpace(line)) lastLine = line;
                            }
                        }

                        if (lastLine != null)
                        {
                            sessionInfos.Add(new SessionInfo
                            {
                                DirName = d.Name,
                                CreationTime = d.CreationTime,
                                LastWriteTime = fi.LastWriteTime,
                                LastLine = lastLine
                            });
                        }
                    }
                    catch { }
                }

                // Match each agy process to its closest session in brain/
                var ser = new JavaScriptSerializer();
                foreach (var rp in rawList)
                {
                    string pName = rp.Name ?? "";
                    string pCmd = rp.CommandLine ?? "";

                    if (pName.IndexOf("agy", StringComparison.OrdinalIgnoreCase) < 0 &&
                        pCmd.IndexOf("agy.exe", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    DateTime procStart = DateTime.Now;
                    if (startTimeMap.ContainsKey(rp.Pid)) procStart = startTimeMap[rp.Pid];

                    // Find session with creation time closest to procStart (within 10 minutes)
                    SessionInfo bestMatch = null;
                    double bestDiff = double.MaxValue;

                    foreach (var s in sessionInfos)
                    {
                        double diff = Math.Abs((s.CreationTime - procStart).TotalMinutes);
                        if (diff < 10 && diff < bestDiff)
                        {
                            bestDiff = diff;
                            bestMatch = s;
                        }
                    }

                    // Fallback: if no match within 10 min, pick the most recently active session
                    if (bestMatch == null && sessionInfos.Count > 0)
                    {
                        bestMatch = sessionInfos.OrderByDescending(s => s.LastWriteTime).FirstOrDefault();
                    }

                    if (bestMatch != null && bestMatch.LastLine != null)
                    {
                        try
                        {
                            var dict = ser.Deserialize<Dictionary<string, object>>(bestMatch.LastLine);
                            string type = dict.ContainsKey("type") ? dict["type"].ToString() : "";
                            bool hasToolCalls = dict.ContainsKey("tool_calls") && dict["tool_calls"] is System.Collections.ArrayList;

                            // Condition 1: Tool call waiting for human authorization or response
                            if (type == "PLANNER_RESPONSE" && hasToolCalls)
                            {
                                var toolCalls = (System.Collections.ArrayList)dict["tool_calls"];
                                if (toolCalls.Count > 0)
                                {
                                    var firstTool = toolCalls[0] as Dictionary<string, object>;
                                    string toolName = firstTool != null && firstTool.ContainsKey("name") ? firstTool["name"].ToString() : "ferramenta";
                                    string toolSummary = "";

                                    if (firstTool != null && firstTool.ContainsKey("args") && firstTool["args"] is Dictionary<string, object>)
                                    {
                                        var args = (Dictionary<string, object>)firstTool["args"];
                                        if (args.ContainsKey("toolSummary")) toolSummary = args["toolSummary"].ToString().Trim('\"', ' ');
                                        else if (args.ContainsKey("toolAction")) toolSummary = args["toolAction"].ToString().Trim('\"', ' ');
                                    }

                                    string reason;
                                    if (toolName == "ask_question")
                                    {
                                        reason = "Pergunta aguardando resposta humana";
                                    }
                                    else if (!string.IsNullOrEmpty(toolSummary))
                                    {
                                        reason = "Aguardando autorização: " + toolSummary;
                                    }
                                    else if (toolName == "run_command")
                                    {
                                        reason = "Aguardando autorização para executar comando";
                                    }
                                    else if (toolName == "write_to_file" || toolName == "replace_file_content")
                                    {
                                        reason = "Aguardando confirmação para editar arquivo";
                                    }
                                    else
                                    {
                                        reason = "Aguardando autorização de ferramenta (" + toolName + ")";
                                    }

                                    pidStatusMap[rp.Pid] = new AgentPromptStatus
                                    {
                                        NeedsHumanInput = true,
                                        Reason = reason
                                    };
                                }
                            }
                            else if (type == "PLANNER_RESPONSE")
                            {
                                pidStatusMap[rp.Pid] = new AgentPromptStatus
                                {
                                    NeedsHumanInput = false,
                                    Reason = "Terminal pronto (aguardando comando)"
                                };
                            }
                            else if (type == "USER_INPUT" || type == "GENERIC" || type == "SYSTEM_MESSAGE")
                            {
                                pidStatusMap[rp.Pid] = new AgentPromptStatus
                                {
                                    NeedsHumanInput = false,
                                    Reason = "Executando tarefas..."
                                };
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static bool DetermineAccurateHumanAction(int pid, string name, string cmd, Dictionary<int, AgentPromptStatus> agentStatusMap, out string statusReason)
        {
            statusReason = "Em execução";

            // 1. Authoritative check from agent transcript session
            if (agentStatusMap != null && agentStatusMap.ContainsKey(pid))
            {
                statusReason = agentStatusMap[pid].Reason;
                return agentStatusMap[pid].NeedsHumanInput;
            }

            // 2. Check modal dialogs and window titles for GUI & terminal windows
            try
            {
                var proc = Process.GetProcessById(pid);
                IntPtr hMain = proc.MainWindowHandle;
                if (hMain != IntPtr.Zero)
                {
                    IntPtr hPopup = GetWindow(hMain, GW_ENABLEDPOPUP);
                    if (hPopup != IntPtr.Zero && hPopup != hMain)
                    {
                        var sbPopup = new StringBuilder(256);
                        GetWindowText(hPopup, sbPopup, 256);
                        statusReason = "Janela modal ou diálogo de confirmação aberto";
                        return true;
                    }

                    var sbTitle = new StringBuilder(512);
                    GetWindowText(hMain, sbTitle, 512);
                    string title = sbTitle.ToString().ToLowerInvariant();

                    if (title.Contains("confirm") || title.Contains("autoriz") ||
                        title.Contains("aguardando") || title.Contains("permissão") ||
                        title.Contains("pause") || title.Contains("pausado") ||
                        title.Contains("input required") || title.Contains("y/n"))
                    {
                        statusReason = "Aguardando confirmação na janela";
                        return true;
                    }
                }
            }
            catch { }

            // 3. Normal process statuses
            if (cmd.IndexOf("server_webcam", StringComparison.OrdinalIgnoreCase) >= 0)
                statusReason = "Servidor Web Ativo";
            else if (name.IndexOf("cloudcode", StringComparison.OrdinalIgnoreCase) >= 0)
                statusReason = "Serviço IDE Ativo";
            else if (name.IndexOf("M365Copilot", StringComparison.OrdinalIgnoreCase) >= 0)
                statusReason = "Em segundo plano";
            else if (name.IndexOf("agy", StringComparison.OrdinalIgnoreCase) >= 0)
                statusReason = "Agente CLI Ativo";
            else
                statusReason = "Em execução";

            return false;
        }

        public static string FormatUptime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalDays >= 1)
                return string.Format("{0}d {1}h {2}m", (int)ts.TotalDays, ts.Hours, ts.Minutes);
            if (ts.TotalHours >= 1)
                return string.Format("{0}h {1}m {2}s", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            if (ts.TotalMinutes >= 1)
                return string.Format("{0}m {1}s", (int)ts.TotalMinutes, ts.Seconds);
            return string.Format("{0}s", (int)ts.TotalSeconds);
        }

        private class RawProc
        {
            public int Pid { get; set; }
            public string Name { get; set; }
            public string CommandLine { get; set; }
            public ulong WorkingSet { get; set; }
        }

        private class SessionInfo
        {
            public string DirName { get; set; }
            public DateTime CreationTime { get; set; }
            public DateTime LastWriteTime { get; set; }
            public string LastLine { get; set; }
        }

        private class AgentPromptStatus
        {
            public bool NeedsHumanInput { get; set; }
            public string Reason { get; set; }
        }
        #endregion
    }

    #region Native Windows Forms Desktop GUI (Requisito: Tela direta no EXE sem precisar de browser)
    public class MainForm : Form
    {
        private int apiPort;
        private System.Windows.Forms.Timer refreshTimer;
        private List<ProcessInfo> currentProcesses = new List<ProcessInfo>();
        private bool isScanning = false;
        private bool hadAlertBefore = false;

        // UI Controls
        private Panel headerPanel;
        private Label lblTitle;
        private Label lblVersionBadge;
        private Label lblSubtitle;
        private Label lblCountValue;
        private Label lblMemValue;
        private Label lblAlertValue;
        private Panel alertBanner;
        private Label lblAlertBanner;
        private Button btnAlertFocus;
        private DataGridView dgv;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;
        private ToolStripStatusLabel versionStatusLabel;
        private ToolStripStatusLabel actionLabel;
        private ComboBox cmbInterval;
        private Button btnRefreshNow;
        private Button btnOpenBrowser;
        private NotifyIcon trayIcon;

        public MainForm(int port)
        {
            this.apiPort = port;
            InitializeComponent();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshData();
        }

        private void InitializeComponent()
        {
            this.Text = "⚡ AI Process Monitor v" + Program.AppVersion + " • Painel Nativo";
            this.Size = new Size(1180, 720);
            this.MinimumSize = new Size(950, 580);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(15, 23, 42); // slate-900
            this.ForeColor = Color.FromArgb(248, 250, 252);
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            this.DoubleBuffered = true;

            try { this.Icon = SystemIcons.Application; } catch { }

            // 1. Header Panel
            headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 120,
                BackColor = Color.FromArgb(30, 41, 59), // slate-800
                Padding = new Padding(20, 12, 20, 12)
            };

            lblTitle = new Label
            {
                Text = "⚡ AI PROCESS MONITOR",
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = Color.FromArgb(248, 250, 252),
                AutoSize = true,
                Location = new Point(20, 14)
            };

            // Version Badge (Controle de Versão visual na tela)
            lblVersionBadge = new Label
            {
                Text = "v" + Program.AppVersion,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248), // sky-400
                BackColor = Color.FromArgb(15, 23, 42),   // slate-900
                Padding = new Padding(6, 2, 6, 2),
                AutoSize = true,
                Location = new Point(275, 18)
            };

            lblSubtitle = new Label
            {
                Text = "Painel nativo em tempo real • Detecção de IA, Pausas de Terminal e Alertas de Ação Humana",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Location = new Point(22, 44)
            };

            // Metrics Cards in Header
            Panel cardProcs = CreateMetricCard("PROCESSOS ATIVOS", "0", Color.FromArgb(56, 189, 248), 20, 68, out lblCountValue);
            Panel cardMem = CreateMetricCard("MEMÓRIA RAM TOTAL", "0 MB", Color.FromArgb(192, 132, 252), 190, 68, out lblMemValue);
            Panel cardAlert = CreateMetricCard("ALERTAS DE AÇÃO", "0", Color.FromArgb(34, 197, 94), 370, 68, out lblAlertValue);

            // Right-aligned header controls
            btnOpenBrowser = new Button
            {
                Text = "🌐 Abrir no Navegador",
                Size = new Size(160, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 180, 14),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(37, 99, 235), // blue-600
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnOpenBrowser.FlatAppearance.BorderSize = 0;
            btnOpenBrowser.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("http://localhost:" + apiPort) { UseShellExecute = true }); } catch { }
            };

            btnRefreshNow = new Button
            {
                Text = "🔄 Atualizar",
                Size = new Size(100, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 290, 14),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular)
            };
            btnRefreshNow.FlatAppearance.BorderSize = 0;
            btnRefreshNow.Click += (s, e) => RefreshData();

            Label lblInterval = new Label
            {
                Text = "Taxa:",
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 410, 22)
            };

            cmbInterval = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 95,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 365, 18),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            cmbInterval.Items.AddRange(new object[] { "1 segundo", "2 segundos", "5 segundos" });
            cmbInterval.SelectedIndex = 1;
            cmbInterval.SelectedIndexChanged += (s, e) =>
            {
                int ms = 2000;
                if (cmbInterval.SelectedIndex == 0) ms = 1000;
                else if (cmbInterval.SelectedIndex == 2) ms = 5000;
                refreshTimer.Interval = ms;
            };

            headerPanel.Controls.Add(lblTitle);
            headerPanel.Controls.Add(lblVersionBadge);
            headerPanel.Controls.Add(lblSubtitle);
            headerPanel.Controls.Add(cardProcs);
            headerPanel.Controls.Add(cardMem);
            headerPanel.Controls.Add(cardAlert);
            headerPanel.Controls.Add(lblInterval);
            headerPanel.Controls.Add(cmbInterval);
            headerPanel.Controls.Add(btnRefreshNow);
            headerPanel.Controls.Add(btnOpenBrowser);

            headerPanel.Resize += (s, e) =>
            {
                btnOpenBrowser.Location = new Point(headerPanel.Width - 180, 14);
                btnRefreshNow.Location = new Point(headerPanel.Width - 290, 14);
                cmbInterval.Location = new Point(headerPanel.Width - 395, 18);
                lblInterval.Location = new Point(headerPanel.Width - 440, 22);
            };

            // 2. Alert Banner Panel (Red alert when human action required)
            alertBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(127, 29, 29), // red-900
                Padding = new Padding(20, 8, 20, 8),
                Visible = false
            };

            lblAlertBanner = new Label
            {
                Text = "🚨 ATENÇÃO: Um ou mais processos de IA estão aguardando sua intervenção humana!",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(20, 14)
            };

            btnAlertFocus = new Button
            {
                Text = "🎯 Abrir Janela Agora",
                Size = new Size(180, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(alertBanner.Width - 200, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(220, 38, 38), // red-600
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btnAlertFocus.FlatAppearance.BorderSize = 0;
            btnAlertFocus.Click += (s, e) =>
            {
                var alertProc = currentProcesses.FirstOrDefault(p => p.needsHumanInput);
                if (alertProc != null)
                {
                    FocusAndNotify(alertProc.pid, alertProc.friendlyName);
                }
            };

            alertBanner.Controls.Add(lblAlertBanner);
            alertBanner.Controls.Add(btnAlertFocus);
            alertBanner.Resize += (s, e) =>
            {
                btnAlertFocus.Location = new Point(alertBanner.Width - 200, 9);
            };

            // 3. DataGridView for processes
            dgv = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(15, 23, 42),
                GridColor = Color.FromArgb(51, 65, 85),
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 46 }
            };

            dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(148, 163, 184);
            dgv.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            dgv.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            dgv.ColumnHeadersHeight = 36;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            dgv.DefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            dgv.DefaultCellStyle.ForeColor = Color.FromArgb(248, 250, 252);
            dgv.DefaultCellStyle.SelectionBackColor = Color.FromArgb(37, 99, 235);
            dgv.DefaultCellStyle.SelectionForeColor = Color.White;
            dgv.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);

            dgv.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(24, 34, 50);

            // Columns
            var colStatus = new DataGridViewTextBoxColumn
            {
                Name = "colStatus",
                HeaderText = "STATUS",
                Width = 175
            };

            var colName = new DataGridViewTextBoxColumn
            {
                Name = "colName",
                HeaderText = "APLICAÇÃO / IA",
                Width = 240
            };

            var colCategory = new DataGridViewTextBoxColumn
            {
                Name = "colCategory",
                HeaderText = "CATEGORIA",
                Width = 170
            };

            var colPid = new DataGridViewTextBoxColumn
            {
                Name = "colPid",
                HeaderText = "PID",
                Width = 75
            };

            var colMem = new DataGridViewTextBoxColumn
            {
                Name = "colMem",
                HeaderText = "MEMÓRIA",
                Width = 100
            };

            var colUptime = new DataGridViewTextBoxColumn
            {
                Name = "colUptime",
                HeaderText = "TEMPO ATIVO",
                Width = 110
            };

            var colReason = new DataGridViewTextBoxColumn
            {
                Name = "colReason",
                HeaderText = "DETALHES / MOTIVO",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 150
            };

            var colAction = new DataGridViewButtonColumn
            {
                Name = "colAction",
                HeaderText = "AÇÃO",
                Width = 130,
                Text = "🎯 Abrir Janela",
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.Flat
            };
            colAction.DefaultCellStyle.BackColor = Color.FromArgb(37, 99, 235);
            colAction.DefaultCellStyle.ForeColor = Color.White;
            colAction.DefaultCellStyle.SelectionBackColor = Color.FromArgb(29, 78, 216);
            colAction.DefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

            dgv.Columns.AddRange(new DataGridViewColumn[] {
                colStatus, colName, colCategory, colPid, colMem, colUptime, colReason, colAction
            });

            dgv.CellContentClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == dgv.Columns["colAction"].Index)
                {
                    int pid = Convert.ToInt32(dgv.Rows[e.RowIndex].Cells["colPid"].Value);
                    string name = dgv.Rows[e.RowIndex].Cells["colName"].Value.ToString();
                    FocusAndNotify(pid, name);
                }
            };

            dgv.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    int pid = Convert.ToInt32(dgv.Rows[e.RowIndex].Cells["colPid"].Value);
                    string name = dgv.Rows[e.RowIndex].Cells["colName"].Value.ToString();
                    FocusAndNotify(pid, name);
                }
            };

            // Custom row highlighting for alerts
            dgv.RowPrePaint += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < currentProcesses.Count)
                {
                    var proc = currentProcesses[e.RowIndex];
                    if (proc.needsHumanInput)
                    {
                        dgv.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(69, 10, 10); // red-950
                        dgv.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.FromArgb(254, 202, 202);
                        dgv.Rows[e.RowIndex].DefaultCellStyle.SelectionBackColor = Color.FromArgb(153, 27, 27);
                    }
                }
            };

            // 4. Status Strip
            statusStrip = new StatusStrip
            {
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(148, 163, 184),
                Height = 32,
                SizingGrip = false
            };

            statusLabel = new ToolStripStatusLabel
            {
                Text = "● Monitor Ativo | API: http://localhost:" + apiPort,
                ForeColor = Color.FromArgb(34, 197, 94),
                Spring = false
            };

            actionLabel = new ToolStripStatusLabel
            {
                Text = "Dica: Clique em '🎯 Abrir Janela' ou duplo-clique na linha para focar a aplicação.",
                ForeColor = Color.FromArgb(148, 163, 184),
                Spring = true,
                TextAlign = ContentAlignment.MiddleRight
            };

            versionStatusLabel = new ToolStripStatusLabel
            {
                Text = "v" + Program.AppVersion,
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Spring = false,
                BorderSides = ToolStripStatusLabelBorderSides.Left,
                BorderStyle = Border3DStyle.Etched
            };

            statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel, actionLabel, versionStatusLabel });

            // 5. System Tray Icon
            try
            {
                trayIcon = new NotifyIcon
                {
                    Text = "AI Process Monitor v" + Program.AppVersion,
                    Icon = SystemIcons.Application,
                    Visible = true
                };

                var contextMenu = new ContextMenuStrip();
                var headerItem = contextMenu.Items.Add("AI Process Monitor v" + Program.AppVersion);
                headerItem.Enabled = false;
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Restaurar Painel", null, (s, e) =>
                {
                    this.Show();
                    this.WindowState = FormWindowState.Normal;
                    this.BringToFront();
                });
                contextMenu.Items.Add("Abrir no Navegador", null, (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo("http://localhost:" + apiPort) { UseShellExecute = true }); } catch { }
                });
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Sair", null, (s, e) => Application.Exit());

                trayIcon.ContextMenuStrip = contextMenu;
                trayIcon.DoubleClick += (s, e) =>
                {
                    this.Show();
                    this.WindowState = FormWindowState.Normal;
                    this.BringToFront();
                };
            }
            catch { }

            // Add controls to Form
            this.Controls.Add(dgv);
            this.Controls.Add(alertBanner);
            this.Controls.Add(headerPanel);
            this.Controls.Add(statusStrip);

            // Timer for automatic background refresh
            refreshTimer = new System.Windows.Forms.Timer
            {
                Interval = 2000
            };
            refreshTimer.Tick += (s, e) => RefreshData();
            refreshTimer.Start();
        }

        private Panel CreateMetricCard(string title, string initialValue, Color accentColor, int x, int y, out Label valueLabel)
        {
            var pnl = new Panel
            {
                Size = new Size(160, 44),
                Location = new Point(x, y),
                BackColor = Color.FromArgb(15, 23, 42)
            };

            var lblT = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(8, 4),
                AutoSize = true
            };

            var lblV = new Label
            {
                Text = initialValue,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = accentColor,
                Location = new Point(8, 20),
                AutoSize = true
            };

            pnl.Controls.Add(lblT);
            pnl.Controls.Add(lblV);
            valueLabel = lblV;
            return pnl;
        }

        private void RefreshData()
        {
            if (isScanning) return;
            isScanning = true;

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    var list = Program.ScanProcesses();
                    if (this.IsHandleCreated && !this.IsDisposed)
                    {
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            try
                            {
                                if (!this.IsDisposed)
                                {
                                    UpdateGridAndMetrics(list);
                                }
                            }
                            catch { }
                            finally
                            {
                                isScanning = false;
                            }
                        });
                    }
                    else
                    {
                        isScanning = false;
                    }
                }
                catch
                {
                    isScanning = false;
                }
            });
        }

        private void UpdateGridAndMetrics(List<ProcessInfo> list)
        {
            currentProcesses = list;

            int alertCount = list.Count(p => p.needsHumanInput);
            double totalMem = Math.Round(list.Sum(p => p.memoryMB), 1);

            lblCountValue.Text = list.Count.ToString();
            lblMemValue.Text = totalMem + " MB";

            if (alertCount > 0)
            {
                lblAlertValue.Text = alertCount.ToString();
                lblAlertValue.ForeColor = Color.FromArgb(239, 68, 68); // red-500

                var firstAlert = list.First(p => p.needsHumanInput);
                lblAlertBanner.Text = string.Format("🚨 AÇÃO HUMANA NECESSÁRIA: {0} ({1})", firstAlert.friendlyName, firstAlert.statusReason);
                alertBanner.Visible = true;

                // Show Tray notification once when alert starts
                if (!hadAlertBefore && trayIcon != null)
                {
                    try
                    {
                        trayIcon.ShowBalloonTip(3000, "AI Process Monitor • Alerta",
                            firstAlert.friendlyName + ": " + firstAlert.statusReason, ToolTipIcon.Warning);
                    }
                    catch { }
                }
                hadAlertBefore = true;
            }
            else
            {
                lblAlertValue.Text = "0";
                lblAlertValue.ForeColor = Color.FromArgb(34, 197, 94); // green-500
                alertBanner.Visible = false;
                hadAlertBefore = false;
            }

            // Populate / Refresh Grid Rows smoothly
            int selectedPid = -1;
            if (dgv.SelectedRows.Count > 0)
            {
                try { selectedPid = Convert.ToInt32(dgv.SelectedRows[0].Cells["colPid"].Value); } catch { }
            }

            dgv.Rows.Clear();
            foreach (var p in list)
            {
                string statusDisplay = p.needsHumanInput ? "🔴 AÇÃO HUMANA" : "🟢 Ativo";
                string nameDisplay = p.friendlyName + " (" + p.processName + ")";

                int rowIndex = dgv.Rows.Add(
                    statusDisplay,
                    nameDisplay,
                    p.category,
                    p.pid,
                    p.memoryMB + " MB",
                    p.uptimeHuman,
                    p.statusReason,
                    "🎯 Abrir Janela"
                );

                if (p.pid == selectedPid)
                {
                    dgv.Rows[rowIndex].Selected = true;
                }
            }
        }

        private void FocusAndNotify(int pid, string friendlyName)
        {
            actionLabel.Text = "🎯 Abrindo janela de " + friendlyName + " (PID " + pid + ")...";
            actionLabel.ForeColor = Color.FromArgb(250, 204, 21); // yellow-400

            ThreadPool.QueueUserWorkItem(state =>
            {
                string msg;
                bool ok = Program.FocusProcessWindow(pid, out msg);

                if (this.IsHandleCreated && !this.IsDisposed)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (ok)
                        {
                            actionLabel.Text = "✅ " + msg;
                            actionLabel.ForeColor = Color.FromArgb(34, 197, 94); // green-500
                        }
                        else
                        {
                            actionLabel.Text = "ℹ️ " + msg;
                            actionLabel.ForeColor = Color.FromArgb(148, 163, 184);
                        }
                    });
                }
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (refreshTimer != null) refreshTimer.Stop();
            if (trayIcon != null) trayIcon.Dispose();
            base.OnFormClosing(e);
        }
    }
    #endregion

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
