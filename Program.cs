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
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]
[assembly: AssemblyInformationalVersion("1.3.0")]

namespace AIProcessMonitor
{
    public static class Program
    {
        public const string AppVersion = "1.3.0";
        public const string BuildDate = "2026-09-13";

        public static int port = 3333;
        private static HttpListener listener;
        private static string embeddedHtml = null;
        private static readonly object cacheLock = new object();
        private static string cachedJson = null;
        private static List<ProcessInfo> cachedProcessList = new List<ProcessInfo>();
        private static DateTime lastScanTime = DateTime.MinValue;

        #region CPU Tracker Storage
        private class CpuTracker
        {
            public DateTime LastSampleUtc;
            public TimeSpan LastProcessorTime;
            public double LastCalculatedCpu;
        }

        private static readonly Dictionary<int, CpuTracker> cpuTrackers = new Dictionary<int, CpuTracker>();
        private static readonly object cpuLock = new object();
        #endregion

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
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + targetPid))
                    {
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            try { familyPids.Add(Convert.ToInt32(mo["ProcessId"])); } catch { }
                        }
                    }
                }
                catch { }

                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = " + targetPid))
                    {
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            try
                            {
                                int parentId = Convert.ToInt32(mo["ParentProcessId"]);
                                if (parentId > 0)
                                {
                                    familyPids.Add(parentId);
                                    using (var subSearcher = new ManagementObjectSearcher(
                                        "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + parentId))
                                    {
                                        foreach (ManagementObject subMo in subSearcher.Get())
                                        {
                                            try { familyPids.Add(Convert.ToInt32(subMo["ProcessId"])); } catch { }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                IntPtr foundHwnd = IntPtr.Zero;
                string foundTitle = "";

                // Tier 1: Process MainWindowHandle
                if (p != null && p.MainWindowHandle != IntPtr.Zero)
                {
                    foundHwnd = p.MainWindowHandle;
                    foundTitle = p.MainWindowTitle;
                }

                // Tier 2: Check active interactive desktops
                if (foundHwnd == IntPtr.Zero)
                {
                    EnumDesktopWindowsProc matchPidCallback = (hWnd, lParam) =>
                    {
                        if (!IsWindowVisible(hWnd)) return true;

                        uint wPid;
                        GetWindowThreadProcessId(hWnd, out wPid);
                        if (familyPids.Contains((int)wPid))
                        {
                            var sb = new StringBuilder(512);
                            GetWindowText(hWnd, sb, 512);
                            string title = sb.ToString().Trim();

                            if (foundHwnd == IntPtr.Zero || !string.IsNullOrEmpty(title))
                            {
                                foundHwnd = hWnd;
                                foundTitle = title;
                                if (!string.IsNullOrEmpty(title)) return false;
                            }
                        }
                        return true;
                    };

                    IntPtr hDesk = OpenDesktop("Default", 0, false, DESKTOP_ALL);
                    if (hDesk == IntPtr.Zero) hDesk = OpenInputDesktop(0, false, DESKTOP_ALL);
                    if (hDesk != IntPtr.Zero)
                    {
                        EnumDesktopWindows(hDesk, matchPidCallback, IntPtr.Zero);
                        CloseDesktop(hDesk);
                    }
                }

                // Tier 3: Global EnumWindows
                if (foundHwnd == IntPtr.Zero)
                {
                    EnumWindowsProc matchPidCallback = (hWnd, lParam) =>
                    {
                        if (!IsWindowVisible(hWnd)) return true;

                        uint wPid;
                        GetWindowThreadProcessId(hWnd, out wPid);
                        if (familyPids.Contains((int)wPid))
                        {
                            var sb = new StringBuilder(512);
                            GetWindowText(hWnd, sb, 512);
                            string title = sb.ToString().Trim();

                            if (foundHwnd == IntPtr.Zero || !string.IsNullOrEmpty(title))
                            {
                                foundHwnd = hWnd;
                                foundTitle = title;
                                if (!string.IsNullOrEmpty(title)) return false;
                            }
                        }
                        return true;
                    };

                    EnumWindows(matchPidCallback, IntPtr.Zero);
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

                    // Accurate CPU & process detail collection
                    double cpuUsage = 0.0;
                    string execPath = "";
                    string winTitle = "";
                    string priority = "Normal";
                    int threadCount = 0;
                    double peakMemMB = 0;

                    try
                    {
                        using (var proc = Process.GetProcessById(rp.Pid))
                        {
                            DateTime nowUtc = DateTime.UtcNow;
                            TimeSpan totalProcTime = proc.TotalProcessorTime;

                            lock (cpuLock)
                            {
                                if (cpuTrackers.ContainsKey(rp.Pid))
                                {
                                    var tr = cpuTrackers[rp.Pid];
                                    double elapsedSec = (nowUtc - tr.LastSampleUtc).TotalSeconds;
                                    if (elapsedSec >= 0.25)
                                    {
                                        double procSec = (totalProcTime - tr.LastProcessorTime).TotalSeconds;
                                        double percent = (procSec / (elapsedSec * Environment.ProcessorCount)) * 100.0;
                                        if (percent < 0) percent = 0;
                                        if (percent > 100) percent = 100;
                                        cpuUsage = Math.Round(percent, 1);
                                        tr.LastSampleUtc = nowUtc;
                                        tr.LastProcessorTime = totalProcTime;
                                        tr.LastCalculatedCpu = cpuUsage;
                                    }
                                    else
                                    {
                                        cpuUsage = tr.LastCalculatedCpu;
                                    }
                                }
                                else
                                {
                                    cpuTrackers[rp.Pid] = new CpuTracker
                                    {
                                        LastSampleUtc = nowUtc,
                                        LastProcessorTime = totalProcTime,
                                        LastCalculatedCpu = 0.0
                                    };
                                    cpuUsage = 0.0;
                                }
                            }

                            try { winTitle = proc.MainWindowTitle; } catch { }
                            try { threadCount = proc.Threads.Count; } catch { }
                            try { priority = proc.PriorityClass.ToString(); } catch { }
                            try { peakMemMB = Math.Round(proc.PeakWorkingSet64 / (1024.0 * 1024.0), 1); } catch { }
                            try { if (proc.MainModule != null) execPath = proc.MainModule.FileName; } catch { }
                        }
                    }
                    catch
                    {
                        lock (cpuLock)
                        {
                            if (cpuTrackers.ContainsKey(rp.Pid))
                                cpuUsage = cpuTrackers[rp.Pid].LastCalculatedCpu;
                        }
                    }

                    if (string.IsNullOrEmpty(execPath) && !string.IsNullOrEmpty(pCmd))
                    {
                        string trimmed = pCmd.Trim();
                        if (trimmed.StartsWith("\""))
                        {
                            int q = trimmed.IndexOf('\"', 1);
                            if (q > 1) execPath = trimmed.Substring(1, q - 1);
                        }
                        else
                        {
                            int sp = trimmed.IndexOf(' ');
                            if (sp > 0) execPath = trimmed.Substring(0, sp);
                            else execPath = trimmed;
                        }
                    }

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
                        cpuPercent = cpuUsage,
                        memoryMB = memMB,
                        commandLine = pCmd,
                        needsHumanInput = needsInput,
                        statusReason = statusReason,
                        executablePath = execPath,
                        windowTitle = winTitle,
                        priority = priority,
                        threadsCount = threadCount,
                        peakMemoryMB = peakMemMB
                    });
                }
            }

            // Purge dead trackers
            lock (cpuLock)
            {
                var alivePids = new HashSet<int>(rawList.Select(r => r.Pid));
                var dead = cpuTrackers.Keys.Where(k => !alivePids.Contains(k)).ToList();
                foreach (var d in dead) cpuTrackers.Remove(d);
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

    #region Native Windows Forms Desktop GUI
    public class MainForm : Form
    {
        private int apiPort;
        private System.Windows.Forms.Timer refreshTimer;
        private System.Windows.Forms.Timer audioAlertTimer;
        private List<ProcessInfo> currentProcesses = new List<ProcessInfo>();
        private int selectedPid = -1;
        private bool isScanning = false;
        private bool hadAlertBefore = false;

        // UI Header Controls
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

        // Details Panel Controls (Requisitos 3 & 4)
        private Panel detailsPanel;
        private Label lblDetailHeader;
        private Label lblDetailSubtitle;
        private Button btnDetailFocus;
        private Panel cardDetail1;
        private Panel cardDetail2;
        private Panel cardDetail3;
        private Label lblD1_Name, lblD1_Bin, lblD1_Cat, lblD1_Status, lblD1_Reason;
        private Label lblD2_Cpu, lblD2_Mem, lblD2_Peak, lblD2_Threads, lblD2_Uptime;
        private Label lblD3_WinTitle, lblD3_Path, lblD3_Cmd;

        // Status & System Controls
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
            this.Size = new Size(1200, 750);
            this.MinimumSize = new Size(1000, 620);
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
                Text = "Painel nativo em tempo real • Clique no processo para ver detalhes e na tela de detalhes para focar",
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
                RowTemplate = { Height = 40 }
            };

            dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(148, 163, 184);
            dgv.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            dgv.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            dgv.ColumnHeadersHeight = 36;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            // Requisito 1: "Não precisa de faixa azul quando seleciono algum processo. Deixa sem nada."
            dgv.DefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            dgv.DefaultCellStyle.ForeColor = Color.FromArgb(248, 250, 252);
            dgv.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 41, 59);
            dgv.DefaultCellStyle.SelectionForeColor = Color.FromArgb(248, 250, 252);
            dgv.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);

            dgv.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(24, 34, 50);
            dgv.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(24, 34, 50);
            dgv.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.FromArgb(248, 250, 252);

            // Evitar retângulo pontilhado de foco de célula
            dgv.CellPainting += (s, e) =>
            {
                if (e.RowIndex >= 0 && (e.PaintParts & DataGridViewPaintParts.Focus) != 0)
                {
                    e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.Focus);
                    e.Handled = true;
                }
            };

            // Custom row styling (mantendo sem faixa azul ao selecionar)
            dgv.RowPrePaint += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < currentProcesses.Count)
                {
                    var proc = currentProcesses[e.RowIndex];
                    if (proc.needsHumanInput)
                    {
                        dgv.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(69, 10, 10); // red-950
                        dgv.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.FromArgb(254, 202, 202);
                        dgv.Rows[e.RowIndex].DefaultCellStyle.SelectionBackColor = Color.FromArgb(69, 10, 10);
                        dgv.Rows[e.RowIndex].DefaultCellStyle.SelectionForeColor = Color.FromArgb(254, 202, 202);
                    }
                    else
                    {
                        Color rowBg = (e.RowIndex % 2 == 0) ? Color.FromArgb(30, 41, 59) : Color.FromArgb(24, 34, 50);
                        dgv.Rows[e.RowIndex].DefaultCellStyle.SelectionBackColor = rowBg;
                        dgv.Rows[e.RowIndex].DefaultCellStyle.SelectionForeColor = Color.FromArgb(248, 250, 252);
                    }
                }
            };

            // Requisito 2: "Preciso de uma coluna que mostre o consumo de CPU."
            var colStatus = new DataGridViewTextBoxColumn
            {
                Name = "colStatus",
                HeaderText = "STATUS",
                Width = 145
            };

            var colName = new DataGridViewTextBoxColumn
            {
                Name = "colName",
                HeaderText = "APLICAÇÃO / IA",
                Width = 215
            };

            var colCategory = new DataGridViewTextBoxColumn
            {
                Name = "colCategory",
                HeaderText = "CATEGORIA",
                Width = 150
            };

            var colPid = new DataGridViewTextBoxColumn
            {
                Name = "colPid",
                HeaderText = "PID",
                Width = 65
            };

            var colCpu = new DataGridViewTextBoxColumn
            {
                Name = "colCpu",
                HeaderText = "CPU (%)",
                Width = 85
            };

            var colMem = new DataGridViewTextBoxColumn
            {
                Name = "colMem",
                HeaderText = "MEMÓRIA",
                Width = 95
            };

            var colUptime = new DataGridViewTextBoxColumn
            {
                Name = "colUptime",
                HeaderText = "TEMPO ATIVO",
                Width = 105
            };

            var colReason = new DataGridViewTextBoxColumn
            {
                Name = "colReason",
                HeaderText = "DETALHES / MOTIVO",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 170
            };

            dgv.Columns.AddRange(new DataGridViewColumn[] {
                colStatus, colName, colCategory, colPid, colCpu, colMem, colUptime, colReason
            });

            // Requisitos 3 & 4: Clicar no processo mostra todos os detalhes na tela de detalhes
            dgv.CellClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < dgv.Rows.Count)
                {
                    try
                    {
                        int pid = Convert.ToInt32(dgv.Rows[e.RowIndex].Cells["colPid"].Value);
                        SelectAndDisplayProcess(pid);
                    }
                    catch { }
                }
            };

            dgv.SelectionChanged += (s, e) =>
            {
                if (dgv.SelectedRows.Count > 0 && dgv.SelectedRows[0].Index >= 0)
                {
                    try
                    {
                        int pid = Convert.ToInt32(dgv.SelectedRows[0].Cells["colPid"].Value);
                        SelectAndDisplayProcess(pid);
                    }
                    catch { }
                }
            };

            // 4. Details Screen Panel (Requisitos 3 & 4)
            InitializeDetailsPanel();

            // 5. Status Strip
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
                Text = "💡 Dica: Clique no processo para ver detalhes e na tela de detalhes para alternar o foco.",
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

            // 6. System Tray Icon
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

            // Add controls to Form with proper docking precedence
            this.Controls.Add(dgv);           // Dock = Fill
            this.Controls.Add(detailsPanel);  // Dock = Bottom (Height = 225)
            this.Controls.Add(alertBanner);   // Dock = Top (Height = 52)
            this.Controls.Add(headerPanel);   // Dock = Top (Height = 120)
            this.Controls.Add(statusStrip);   // Dock = Bottom (Height = 32)

            // Timer for automatic background refresh
            refreshTimer = new System.Windows.Forms.Timer
            {
                Interval = 2000
            };
            refreshTimer.Tick += (s, e) => RefreshData();
            refreshTimer.Start();

            // Requisito 5: "Tocar alerta de ação humana a cada 4 segundos, enquanto o humano não der sequência ao processo."
            audioAlertTimer = new System.Windows.Forms.Timer
            {
                Interval = 4000
            };
            audioAlertTimer.Tick += (s, e) =>
            {
                if (currentProcesses != null && currentProcesses.Any(p => p.needsHumanInput))
                {
                    PlayHumanAlertSound();
                }
                else
                {
                    audioAlertTimer.Stop();
                }
            };
        }

        private void InitializeDetailsPanel()
        {
            detailsPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 230,
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(16, 6, 16, 8),
                Cursor = Cursors.Hand
            };

            // Desenhar linha de separação no topo do painel de detalhes
            detailsPanel.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(51, 65, 85), 1))
                {
                    e.Graphics.DrawLine(pen, 0, 0, detailsPanel.Width, 0);
                }
            };

            // Ao clicar em qualquer ponto do painel de detalhes, o foco muda para a janela do aplicativo (Requisito 4)
            detailsPanel.Click += (s, e) => FocusSelectedProcess();

            lblDetailHeader = new Label
            {
                Text = "📋 DETALHES DO PROCESSO SELECIONADO",
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248), // sky-400
                AutoSize = true,
                Location = new Point(14, 8),
                Cursor = Cursors.Hand
            };
            lblDetailHeader.Click += (s, e) => FocusSelectedProcess();

            lblDetailSubtitle = new Label
            {
                Text = "💡 Dica: Clique em qualquer lugar deste painel para alternar o foco para o aplicativo selecionado.",
                Font = new Font("Segoe UI", 8.2f, FontStyle.Italic),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Location = new Point(16, 30),
                Cursor = Cursors.Hand
            };
            lblDetailSubtitle.Click += (s, e) => FocusSelectedProcess();

            btnDetailFocus = new Button
            {
                Text = "🎯 CLIQUE AQUI PARA ABRIR A JANELA DO APLICATIVO",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(37, 99, 235), // blue-600
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Size = new Size(410, 32),
                Location = new Point(detailsPanel.Width - 430, 6)
            };
            btnDetailFocus.FlatAppearance.BorderSize = 0;
            btnDetailFocus.Click += (s, e) => FocusSelectedProcess();

            detailsPanel.Resize += (s, e) =>
            {
                btnDetailFocus.Location = new Point(detailsPanel.Width - 430, 6);
            };

            // Layout com 3 cartões de detalhes proporcionais
            var tlp = new TableLayoutPanel
            {
                Location = new Point(12, 50),
                Size = new Size(detailsPanel.Width - 24, 170),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent
            };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            tlp.Click += (s, e) => FocusSelectedProcess();

            // Card 1: Identificação & Status
            cardDetail1 = CreateDetailCard("📌 IDENTIFICAÇÃO & STATUS", Color.FromArgb(251, 191, 36));
            lblD1_Name = AddDetailField(cardDetail1, "Aplicação:", "--", true, Color.White, 30);
            lblD1_Bin = AddDetailField(cardDetail1, "Executável:", "--", false, Color.FromArgb(203, 213, 225), 54);
            lblD1_Cat = AddDetailField(cardDetail1, "Categoria:", "--", false, Color.FromArgb(203, 213, 225), 78);
            lblD1_Status = AddDetailField(cardDetail1, "Status:", "--", true, Color.FromArgb(74, 222, 128), 102);
            lblD1_Reason = AddDetailField(cardDetail1, "Motivo:", "--", false, Color.FromArgb(226, 232, 240), 126);

            // Card 2: Consumo & Performance
            cardDetail2 = CreateDetailCard("⚡ RECURSOS DO SISTEMA", Color.FromArgb(74, 222, 128));
            lblD2_Cpu = AddDetailField(cardDetail2, "Uso de CPU:", "--", true, Color.FromArgb(56, 189, 248), 30);
            lblD2_Mem = AddDetailField(cardDetail2, "Memória RAM:", "--", false, Color.FromArgb(203, 213, 225), 54);
            lblD2_Peak = AddDetailField(cardDetail2, "Pico de RAM:", "--", false, Color.FromArgb(203, 213, 225), 78);
            lblD2_Threads = AddDetailField(cardDetail2, "Threads / Prioridade:", "--", false, Color.FromArgb(203, 213, 225), 102);
            lblD2_Uptime = AddDetailField(cardDetail2, "Tempo Ativo:", "--", false, Color.FromArgb(203, 213, 225), 126);

            // Card 3: Janela & Caminho
            cardDetail3 = CreateDetailCard("🪟 JANELA & COMANDO", Color.FromArgb(192, 132, 252));
            lblD3_WinTitle = AddDetailField(cardDetail3, "Título da Janela:", "--", true, Color.White, 30);
            lblD3_Path = AddDetailField(cardDetail3, "Caminho no Disco:", "--", false, Color.FromArgb(203, 213, 225), 56, 32);
            lblD3_Cmd = AddDetailField(cardDetail3, "Linha de Comando:", "--", false, Color.FromArgb(148, 163, 184), 92, 42);

            tlp.Controls.Add(cardDetail1, 0, 0);
            tlp.Controls.Add(cardDetail2, 1, 0);
            tlp.Controls.Add(cardDetail3, 2, 0);

            detailsPanel.Controls.Add(lblDetailHeader);
            detailsPanel.Controls.Add(lblDetailSubtitle);
            detailsPanel.Controls.Add(btnDetailFocus);
            detailsPanel.Controls.Add(tlp);
        }

        private Panel CreateDetailCard(string title, Color headerColor)
        {
            var pnl = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 41, 59), // slate-800
                Margin = new Padding(4),
                Padding = new Padding(10, 8, 10, 8),
                Cursor = Cursors.Hand
            };
            pnl.Click += (s, e) => FocusSelectedProcess();

            var lblT = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
                ForeColor = headerColor,
                Location = new Point(10, 8),
                AutoSize = true,
                Cursor = Cursors.Hand
            };
            lblT.Click += (s, e) => FocusSelectedProcess();
            pnl.Controls.Add(lblT);

            return pnl;
        }

        private Label AddDetailField(Panel parent, string prefix, string initialValue, bool isBold, Color foreColor, int topY, int height = 22)
        {
            var lbl = new Label
            {
                Text = prefix + " " + initialValue,
                Font = new Font("Segoe UI", isBold ? 9f : 8.3f, isBold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = foreColor,
                Location = new Point(10, topY),
                AutoSize = false,
                Width = Math.Max(100, parent.Width - 20),
                Height = height,
                AutoEllipsis = true,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            lbl.Click += (s, e) => FocusSelectedProcess();
            parent.Controls.Add(lbl);
            return lbl;
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

                // Requisito 5: Iniciar alarme sonoro a cada 4s enquanto não houver ação humana
                if (!audioAlertTimer.Enabled)
                {
                    PlayHumanAlertSound();
                    audioAlertTimer.Start();
                }

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

                if (audioAlertTimer.Enabled)
                {
                    audioAlertTimer.Stop();
                }
            }

            // Populate Grid Rows smoothly
            dgv.Rows.Clear();
            int selectedRowIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                string statusDisplay = p.needsHumanInput ? "🔴 AÇÃO HUMANA" : "🟢 Ativo";
                string nameDisplay = p.friendlyName + " (" + p.processName + ")";

                dgv.Rows.Add(
                    statusDisplay,
                    nameDisplay,
                    p.category,
                    p.pid,
                    p.cpuPercent.ToString("0.0") + " %",
                    p.memoryMB.ToString("0.0") + " MB",
                    p.uptimeHuman,
                    p.statusReason
                );

                if (p.pid == selectedPid)
                {
                    selectedRowIndex = i;
                }
            }

            if (selectedRowIndex >= 0 && selectedRowIndex < dgv.Rows.Count)
            {
                dgv.Rows[selectedRowIndex].Selected = true;
                var currentProc = list.FirstOrDefault(p => p.pid == selectedPid);
                if (currentProc != null)
                {
                    UpdateDetailsDisplay(currentProc);
                }
            }
            else if (selectedPid == -1 && list.Count > 0)
            {
                selectedPid = list[0].pid;
                dgv.Rows[0].Selected = true;
                UpdateDetailsDisplay(list[0]);
            }
        }

        // Requisito 3: Mostrar todos os detalhes possíveis ao clicar no processo
        private void SelectAndDisplayProcess(int pid)
        {
            selectedPid = pid;
            var proc = currentProcesses.FirstOrDefault(p => p.pid == pid);
            if (proc != null)
            {
                UpdateDetailsDisplay(proc);
            }
        }

        private void UpdateDetailsDisplay(ProcessInfo p)
        {
            if (p == null)
            {
                lblDetailHeader.Text = "📋 DETALHES DO PROCESSO";
                lblDetailSubtitle.Text = "💡 Selecione qualquer processo na lista acima para visualizar todos os detalhes aqui.";
                btnDetailFocus.Text = "🎯 CLIQUE AQUI PARA ABRIR A JANELA DO APLICATIVO";
                btnDetailFocus.BackColor = Color.FromArgb(51, 65, 85);
                return;
            }

            selectedPid = p.pid;
            lblDetailHeader.Text = "📋 DETALHES DO PROCESSO: " + p.friendlyName + " (PID " + p.pid + ")";
            lblDetailSubtitle.Text = "💡 Clique em qualquer lugar deste painel para alternar o foco para o aplicativo.";

            lblD1_Name.Text = "Aplicação: " + p.friendlyName;
            lblD1_Bin.Text = "Executável: " + p.processName + ".exe (PID: " + p.pid + ")";
            lblD1_Cat.Text = "Categoria: " + p.category;
            lblD1_Status.Text = "Status: " + (p.needsHumanInput ? "🔴 AGUARDANDO AÇÃO HUMANA" : "🟢 Em Execução / Ativo");
            lblD1_Status.ForeColor = p.needsHumanInput ? Color.FromArgb(248, 113, 113) : Color.FromArgb(74, 222, 128);
            lblD1_Reason.Text = "Motivo: " + p.statusReason;

            lblD2_Cpu.Text = "Consumo de CPU: " + p.cpuPercent.ToString("0.0") + " %";
            lblD2_Mem.Text = "Memória RAM: " + p.memoryMB.ToString("0.0") + " MB";
            lblD2_Peak.Text = "Pico de RAM: " + (p.peakMemoryMB > 0 ? p.peakMemoryMB.ToString("0.0") + " MB" : "--");
            lblD2_Threads.Text = "Threads: " + (p.threadsCount > 0 ? p.threadsCount.ToString() : "--") + " | Prioridade: " + (string.IsNullOrEmpty(p.priority) ? "Normal" : p.priority);
            lblD2_Uptime.Text = "Tempo Ativo: " + p.uptimeHuman + " (Início: " + p.startTime + ")";

            lblD3_WinTitle.Text = "Título da Janela: " + (string.IsNullOrEmpty(p.windowTitle) ? "(Nenhuma janela visível direta)" : p.windowTitle);
            lblD3_Path.Text = "Caminho: " + (string.IsNullOrEmpty(p.executablePath) ? "--" : p.executablePath);
            lblD3_Cmd.Text = "Comando: " + (string.IsNullOrEmpty(p.commandLine) ? "--" : p.commandLine);

            if (p.needsHumanInput)
            {
                btnDetailFocus.BackColor = Color.FromArgb(220, 38, 38);
                btnDetailFocus.Text = "🚨 CLIQUE AQUI PARA ABRIR A JANELA E INTERVIR";
            }
            else
            {
                btnDetailFocus.BackColor = Color.FromArgb(37, 99, 235);
                btnDetailFocus.Text = "🎯 CLIQUE AQUI PARA ABRIR A JANELA DESTE APLICATIVO";
            }
        }

        // Requisito 4: "Só quando clicar sobre a tela de detalhes, é que o foco muda para a janela onde o aplicativo está sendo executado."
        private void FocusSelectedProcess()
        {
            if (selectedPid > 0)
            {
                var proc = currentProcesses.FirstOrDefault(p => p.pid == selectedPid);
                string name = proc != null ? proc.friendlyName : ("PID " + selectedPid);
                FocusAndNotify(selectedPid, name);
            }
            else
            {
                actionLabel.Text = "ℹ️ Nenhum processo selecionado para focar.";
                actionLabel.ForeColor = Color.FromArgb(148, 163, 184);
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

        // Requisito 5: Tocar alerta sonoro de ação humana
        private void PlayHumanAlertSound()
        {
            try
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
            catch
            {
                try { Console.Beep(1000, 200); } catch { }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (refreshTimer != null) refreshTimer.Stop();
            if (audioAlertTimer != null) audioAlertTimer.Stop();
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
        public double cpuPercent { get; set; }
        public double memoryMB { get; set; }
        public string commandLine { get; set; }
        public bool needsHumanInput { get; set; }
        public string statusReason { get; set; }
        public string executablePath { get; set; }
        public string windowTitle { get; set; }
        public string priority { get; set; }
        public int threadsCount { get; set; }
        public double peakMemoryMB { get; set; }
    }
}
