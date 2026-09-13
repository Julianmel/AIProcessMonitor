using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("AI Process Monitor")]
[assembly: AssemblyDescription("Monitor de Processos de IA e Ação Humana em Tempo Real")]
[assembly: AssemblyCompany("Julianmel")]
[assembly: AssemblyProduct("AI Process Monitor")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]
[assembly: AssemblyInformationalVersion("1.4.0")]

namespace AIProcessMonitor
{
    #region Speech Synthesizer (Voz Sintetizada Inteligente em Português)
    public static class SpeechAlertManager
    {
        private static SpeechSynthesizer synth;
        private static bool isInitialized = false;
        private static DateTime lastSpokenTime = DateTime.MinValue;
        private static string lastSpokenKey = null;
        private static readonly object speechLock = new object();

        private static void Init()
        {
            if (isInitialized) return;
            try
            {
                synth = new SpeechSynthesizer();
                foreach (var v in synth.GetInstalledVoices())
                {
                    if (v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
                    {
                        synth.SelectVoice(v.VoiceInfo.Name);
                        break;
                    }
                }
                synth.Rate = 1;
                synth.Volume = 95;
                isInitialized = true;
            }
            catch { }
        }

        public static void AnnounceAlert(string processName, string reason, bool force = false)
        {
            if (MuteManager.IsMuted) return;

            try
            {
                Init();
                if (synth == null) return;

                string key = (processName ?? "") + "|" + (reason ?? "");
                var now = DateTime.Now;

                if (!force && key == lastSpokenKey && (now - lastSpokenTime).TotalSeconds < 18)
                {
                    return;
                }

                lastSpokenKey = key;
                lastSpokenTime = now;

                string spokenText;
                if (!string.IsNullOrEmpty(reason) && reason.IndexOf("autoriza", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    spokenText = "Atenção: " + processName + " aguarda sua autorização.";
                }
                else if (!string.IsNullOrEmpty(reason) && reason.IndexOf("pergunta", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    spokenText = "Atenção: " + processName + " fez uma pergunta e aguarda resposta.";
                }
                else
                {
                    spokenText = "Atenção: " + processName + " aguarda ação humana.";
                }

                ThreadPool.QueueUserWorkItem(state =>
                {
                    lock (speechLock)
                    {
                        try
                        {
                            synth.SpeakAsyncCancelAll();
                            synth.Speak(spokenText);
                        }
                        catch { }
                    }
                });
            }
            catch { }
        }
    }
    #endregion

    #region Mute / Do Not Disturb Manager
    public static class MuteManager
    {
        public static bool IsMutedIndefinitely { get; private set; }
        public static DateTime MuteUntilUtc { get; private set; }

        public static bool IsMuted
        {
            get
            {
                if (IsMutedIndefinitely) return true;
                return DateTime.UtcNow < MuteUntilUtc;
            }
        }

        public static void Unmute()
        {
            IsMutedIndefinitely = false;
            MuteUntilUtc = DateTime.MinValue;
        }

        public static void MuteIndefinitely()
        {
            IsMutedIndefinitely = true;
            MuteUntilUtc = DateTime.MaxValue;
        }

        public static void MuteFor(TimeSpan duration)
        {
            IsMutedIndefinitely = false;
            MuteUntilUtc = DateTime.UtcNow.Add(duration);
        }

        public static string GetStatusLabel()
        {
            if (!IsMuted) return "🔊 Som Ativo";
            if (IsMutedIndefinitely) return "🔇 Silenciado";
            var rem = MuteUntilUtc - DateTime.UtcNow;
            if (rem.TotalMinutes >= 1)
                return string.Format("🔇 Mudo ({0}m)", (int)Math.Ceiling(rem.TotalMinutes));
            return string.Format("🔇 Mudo ({0}s)", Math.Max(1, (int)rem.TotalSeconds));
        }
    }
    #endregion

    public static class Program
    {
        public const string AppVersion = "1.4.0";
        public const string BuildDate = "2026-09-13";

        public static int port = 3333;
        private static HttpListener listener;
        private static string embeddedHtml = null;
        private static readonly object cacheLock = new object();
        private static string cachedJson = null;
        private static List<ProcessInfo> cachedProcessList = new List<ProcessInfo>();
        private static DateTime lastScanTime = DateTime.MinValue;

        #region CPU Tracker & Process Telemetry Storage
        private class CpuTracker
        {
            public DateTime LastSampleUtc;
            public TimeSpan LastProcessorTime;
            public double LastCalculatedCpu;
        }

        private static readonly Dictionary<int, CpuTracker> cpuTrackers = new Dictionary<int, CpuTracker>();
        private static readonly object cpuLock = new object();

        public class ProcessHistory
        {
            public List<double> CpuSamples = new List<double>();
            public List<double> MemSamples = new List<double>();
            public DateTime LastUpdated = DateTime.UtcNow;

            public void Add(double cpu, double mem)
            {
                CpuSamples.Add(cpu);
                MemSamples.Add(mem);
                if (CpuSamples.Count > 40) CpuSamples.RemoveAt(0);
                if (MemSamples.Count > 40) MemSamples.RemoveAt(0);
                LastUpdated = DateTime.UtcNow;
            }
        }

        private static readonly Dictionary<int, ProcessHistory> processHistories = new Dictionary<int, ProcessHistory>();
        private static readonly object historyLock = new object();

        public static ProcessHistory GetHistory(int pid)
        {
            lock (historyLock)
            {
                if (processHistories.ContainsKey(pid)) return processHistories[pid];
                var h = new ProcessHistory();
                processHistories[pid] = h;
                return h;
            }
        }
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
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
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

                if (path == "/api/kill")
                {
                    string pidStr = req.QueryString["pid"];
                    int pidToKill;
                    bool success = false;
                    string message = "PID inválido";

                    if (int.TryParse(pidStr, out pidToKill))
                    {
                        try
                        {
                            var proc = Process.GetProcessById(pidToKill);
                            proc.Kill();
                            success = true;
                            message = "Processo finalizado com sucesso.";
                        }
                        catch (Exception ex)
                        {
                            message = "Erro ao finalizar processo: " + ex.Message;
                        }
                    }

                    var ser = new JavaScriptSerializer();
                    string json = ser.Serialize(new
                    {
                        success = success,
                        pid = pidToKill,
                        message = message
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

                    // Record telemetry history
                    var hist = GetHistory(rp.Pid);
                    hist.Add(cpuUsage, memMB);

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

            if (agentStatusMap != null && agentStatusMap.ContainsKey(pid))
            {
                statusReason = agentStatusMap[pid].Reason;
                return agentStatusMap[pid].NeedsHumanInput;
            }

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

        // Header Sound Toggle Button (Modo Não Perturbe)
        private Button btnSoundToggle;

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
            this.Size = new Size(1200, 720);
            this.MinimumSize = new Size(960, 560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.FromArgb(248, 250, 252);
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            this.DoubleBuffered = true;

            try { this.Icon = SystemIcons.Application; } catch { }

            // 1. Header Panel
            headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 120,
                BackColor = Color.FromArgb(30, 41, 59),
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

            lblVersionBadge = new Label
            {
                Text = "v" + Program.AppVersion,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(6, 2, 6, 2),
                AutoSize = true,
                Location = new Point(275, 18)
            };

            lblSubtitle = new Label
            {
                Text = "Painel nativo em tempo real • Voz inteligente em português • Clique no processo para ver detalhes",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Location = new Point(22, 44)
            };

            Panel cardProcs = CreateMetricCard("PROCESSOS ATIVOS", "0", Color.FromArgb(56, 189, 248), 20, 68, out lblCountValue);
            Panel cardMem = CreateMetricCard("MEMÓRIA RAM TOTAL", "0 MB", Color.FromArgb(192, 132, 252), 190, 68, out lblMemValue);
            Panel cardAlert = CreateMetricCard("ALERTAS DE AÇÃO", "0", Color.FromArgb(34, 197, 94), 370, 68, out lblAlertValue);

            // Sound Toggle (Modo Não Perturbe)
            btnSoundToggle = new Button
            {
                Text = "🔊 Som & Voz Ativos",
                Size = new Size(160, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 180, 14),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            };
            btnSoundToggle.FlatAppearance.BorderSize = 0;
            btnSoundToggle.Click += (s, e) => ShowMuteMenu();

            btnOpenBrowser = new Button
            {
                Text = "🌐 Navegador",
                Size = new Size(115, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 305, 14),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            };
            btnOpenBrowser.FlatAppearance.BorderSize = 0;
            btnOpenBrowser.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("http://localhost:" + apiPort) { UseShellExecute = true }); } catch { }
            };

            btnRefreshNow = new Button
            {
                Text = "🔄 Atualizar",
                Size = new Size(95, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 410, 14),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Regular)
            };
            btnRefreshNow.FlatAppearance.BorderSize = 0;
            btnRefreshNow.Click += (s, e) => RefreshData();

            Label lblInterval = new Label
            {
                Text = "Taxa:",
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 530, 22)
            };

            cmbInterval = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 95,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(headerPanel.Width - 485, 18),
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
            headerPanel.Controls.Add(btnSoundToggle);

            headerPanel.Resize += (s, e) =>
            {
                btnSoundToggle.Location = new Point(headerPanel.Width - 180, 14);
                btnOpenBrowser.Location = new Point(headerPanel.Width - 305, 14);
                btnRefreshNow.Location = new Point(headerPanel.Width - 410, 14);
                cmbInterval.Location = new Point(headerPanel.Width - 515, 18);
                lblInterval.Location = new Point(headerPanel.Width - 560, 22);
            };

            // 2. Alert Banner Panel
            alertBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(127, 29, 29),
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
                BackColor = Color.FromArgb(220, 38, 38),
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
                RowTemplate = { Height = 44 }
            };

            dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(148, 163, 184);
            dgv.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            dgv.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            dgv.ColumnHeadersHeight = 38;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            dgv.DefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            dgv.DefaultCellStyle.ForeColor = Color.FromArgb(248, 250, 252);
            dgv.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 41, 59);
            dgv.DefaultCellStyle.SelectionForeColor = Color.FromArgb(248, 250, 252);
            dgv.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);

            dgv.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(24, 34, 50);
            dgv.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(24, 34, 50);
            dgv.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.FromArgb(248, 250, 252);

            dgv.CellPainting += (s, e) =>
            {
                if (e.RowIndex >= 0 && (e.PaintParts & DataGridViewPaintParts.Focus) != 0)
                {
                    e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.Focus);
                    e.Handled = true;
                }
            };

            dgv.RowPrePaint += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < currentProcesses.Count)
                {
                    var proc = currentProcesses[e.RowIndex];
                    if (proc.needsHumanInput)
                    {
                        dgv.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(69, 10, 10);
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

            var colStatus = new DataGridViewTextBoxColumn { Name = "colStatus", HeaderText = "STATUS", Width = 150 };
            var colName = new DataGridViewTextBoxColumn { Name = "colName", HeaderText = "APLICAÇÃO / IA", Width = 230 };
            var colCategory = new DataGridViewTextBoxColumn { Name = "colCategory", HeaderText = "CATEGORIA", Width = 160 };
            var colPid = new DataGridViewTextBoxColumn { Name = "colPid", HeaderText = "PID", Width = 70 };
            var colCpu = new DataGridViewTextBoxColumn { Name = "colCpu", HeaderText = "CPU (%)", Width = 90 };
            var colMem = new DataGridViewTextBoxColumn { Name = "colMem", HeaderText = "MEMÓRIA", Width = 100 };
            var colUptime = new DataGridViewTextBoxColumn { Name = "colUptime", HeaderText = "TEMPO ATIVO", Width = 110 };
            var colReason = new DataGridViewTextBoxColumn { Name = "colReason", HeaderText = "DETALHES / MOTIVO", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 170 };

            dgv.Columns.AddRange(new DataGridViewColumn[] {
                colStatus, colName, colCategory, colPid, colCpu, colMem, colUptime, colReason
            });

            // Requisito: Subjanela modal de detalhes abre apenas quando seleciona o processo
            dgv.CellClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < dgv.Rows.Count)
                {
                    OpenProcessDetailSubWindow(e.RowIndex);
                }
            };

            dgv.KeyDown += (s, e) =>
            {
                if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && dgv.SelectedRows.Count > 0)
                {
                    e.Handled = true;
                    OpenProcessDetailSubWindow(dgv.SelectedRows[0].Index);
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
                Text = "💡 Dica: Clique no processo para abrir a subjanela de detalhes. Ao clicar nela, o foco muda para o aplicativo.",
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

            // 5. System Tray Icon with Mute Options
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

                var muteMenu = new ToolStripMenuItem("🔇 Modo Não Perturbe");
                muteMenu.DropDownItems.Add("🔊 Ativar Som & Voz", null, (s, e) => UpdateMuteMode("unmute"));
                muteMenu.DropDownItems.Add("🔇 Silenciar por 15 minutos", null, (s, e) => UpdateMuteMode("15m"));
                muteMenu.DropDownItems.Add("🔇 Silenciar por 30 minutos", null, (s, e) => UpdateMuteMode("30m"));
                muteMenu.DropDownItems.Add("🔇 Silenciar por 1 hora", null, (s, e) => UpdateMuteMode("1h"));
                muteMenu.DropDownItems.Add("🔇 Silenciar Indefinidamente", null, (s, e) => UpdateMuteMode("indefinite"));
                contextMenu.Items.Add(muteMenu);

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

            this.Controls.Add(dgv);
            this.Controls.Add(alertBanner);
            this.Controls.Add(headerPanel);
            this.Controls.Add(statusStrip);

            refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            refreshTimer.Tick += (s, e) =>
            {
                RefreshData();
                btnSoundToggle.Text = MuteManager.GetStatusLabel();
                if (MuteManager.IsMuted)
                {
                    btnSoundToggle.BackColor = Color.FromArgb(71, 85, 105);
                }
                else
                {
                    btnSoundToggle.BackColor = Color.FromArgb(16, 185, 129);
                }
            };
            refreshTimer.Start();

            // Timer de alerta sonoro a cada 4 segundos
            audioAlertTimer = new System.Windows.Forms.Timer { Interval = 4000 };
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

        private void ShowMuteMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("🔊 Ativar Som & Voz", null, (s, e) => UpdateMuteMode("unmute"));
            menu.Items.Add("-");
            menu.Items.Add("🔇 Silenciar por 15 minutos", null, (s, e) => UpdateMuteMode("15m"));
            menu.Items.Add("🔇 Silenciar por 30 minutos", null, (s, e) => UpdateMuteMode("30m"));
            menu.Items.Add("🔇 Silenciar por 1 hora", null, (s, e) => UpdateMuteMode("1h"));
            menu.Items.Add("🔇 Silenciar Indefinidamente", null, (s, e) => UpdateMuteMode("indefinite"));
            menu.Show(btnSoundToggle, new Point(0, btnSoundToggle.Height));
        }

        private void UpdateMuteMode(string mode)
        {
            if (mode == "unmute") MuteManager.Unmute();
            else if (mode == "15m") MuteManager.MuteFor(TimeSpan.FromMinutes(15));
            else if (mode == "30m") MuteManager.MuteFor(TimeSpan.FromMinutes(30));
            else if (mode == "1h") MuteManager.MuteFor(TimeSpan.FromHours(1));
            else if (mode == "indefinite") MuteManager.MuteIndefinitely();

            btnSoundToggle.Text = MuteManager.GetStatusLabel();
            if (MuteManager.IsMuted)
            {
                btnSoundToggle.BackColor = Color.FromArgb(71, 85, 105);
                actionLabel.Text = "🔇 Modo Não Perturbe ativado (" + MuteManager.GetStatusLabel() + ").";
            }
            else
            {
                btnSoundToggle.BackColor = Color.FromArgb(16, 185, 129);
                actionLabel.Text = "🔊 Som e Voz reativados com sucesso.";
            }
        }

        private void OpenProcessDetailSubWindow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= dgv.Rows.Count) return;
            try
            {
                int pid = Convert.ToInt32(dgv.Rows[rowIndex].Cells["colPid"].Value);
                var proc = currentProcesses.FirstOrDefault(p => p.pid == pid);
                if (proc == null) return;

                using (var subForm = new ProcessDetailSubForm(proc))
                {
                    subForm.ShowDialog(this);
                    if (subForm.FocusRequested)
                    {
                        FocusAndNotify(proc.pid, proc.friendlyName);
                    }
                    if (subForm.ProcessKilled)
                    {
                        RefreshData();
                    }
                }
            }
            catch { }
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
                lblAlertValue.ForeColor = Color.FromArgb(239, 68, 68);

                var firstAlert = list.First(p => p.needsHumanInput);
                lblAlertBanner.Text = string.Format("🚨 AÇÃO HUMANA NECESSÁRIA: {0} ({1})", firstAlert.friendlyName, firstAlert.statusReason);
                alertBanner.Visible = true;

                // Anunciar por voz em português (Microsoft Maria)
                SpeechAlertManager.AnnounceAlert(firstAlert.friendlyName, firstAlert.statusReason);

                if (!audioAlertTimer.Enabled)
                {
                    PlayHumanAlertSound();
                    audioAlertTimer.Start();
                }

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
                lblAlertValue.ForeColor = Color.FromArgb(34, 197, 94);
                alertBanner.Visible = false;
                hadAlertBefore = false;

                if (audioAlertTimer.Enabled)
                {
                    audioAlertTimer.Stop();
                }
            }

            int selectedPid = -1;
            if (dgv.SelectedRows.Count > 0)
            {
                try { selectedPid = Convert.ToInt32(dgv.SelectedRows[0].Cells["colPid"].Value); } catch { }
            }

            dgv.Rows.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                string statusDisplay = p.needsHumanInput ? "🔴 AÇÃO HUMANA" : "🟢 Ativo";
                string nameDisplay = p.friendlyName + " (" + p.processName + ")";

                int rowIndex = dgv.Rows.Add(
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
                    dgv.Rows[rowIndex].Selected = true;
                }
            }
        }

        public void FocusAndNotify(int pid, string friendlyName)
        {
            actionLabel.Text = "🎯 Abrindo janela de " + friendlyName + " (PID " + pid + ")...";
            actionLabel.ForeColor = Color.FromArgb(250, 204, 21);

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
                            actionLabel.ForeColor = Color.FromArgb(34, 197, 94);
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

        private void PlayHumanAlertSound()
        {
            if (MuteManager.IsMuted) return;

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

    #region Process Detail SubWindow with Telemetry Graph & Process Controls
    public class ProcessDetailSubForm : Form
    {
        private ProcessInfo proc;
        public bool FocusRequested { get; private set; }
        public bool ProcessKilled { get; private set; }

        private System.Windows.Forms.Timer graphRefreshTimer;
        private Panel pnlGraph;
        private ComboBox cmbPriority;
        private Label lblGraphTelemetry;

        public ProcessDetailSubForm(ProcessInfo process)
        {
            this.proc = process;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Detalhes: " + proc.friendlyName;
            this.Size = new Size(900, 530);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ShowInTaskbar = false;
            this.BackColor = Color.FromArgb(56, 189, 248);
            this.Padding = new Padding(2);
            this.KeyPreview = true;

            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    this.Close();
                }
            };

            var mainContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(16, 12, 16, 12),
                Cursor = Cursors.Hand
            };
            mainContainer.Click += (s, e) => TriggerFocusAndClose();

            // 1. Top Header Bar
            var pnlTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            pnlTop.Click += (s, e) => TriggerFocusAndClose();

            var lblTitle = new Label
            {
                Text = "📋 DETALHES DO PROCESSO: " + proc.friendlyName + " (PID " + proc.pid + ")",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                AutoSize = true,
                Location = new Point(4, 6),
                Cursor = Cursors.Hand
            };
            lblTitle.Click += (s, e) => TriggerFocusAndClose();

            var btnClose = new Button
            {
                Text = "✕",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(34, 30),
                Location = new Point(pnlTop.Width - 40, 2),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.MouseEnter += (s, e) => { btnClose.ForeColor = Color.FromArgb(239, 68, 68); };
            btnClose.MouseLeave += (s, e) => { btnClose.ForeColor = Color.FromArgb(148, 163, 184); };
            btnClose.Click += (s, e) => { this.Close(); };

            pnlTop.Controls.Add(lblTitle);
            pnlTop.Controls.Add(btnClose);

            // 2. Action Banner
            var pnlBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = proc.needsHumanInput ? Color.FromArgb(220, 38, 38) : Color.FromArgb(37, 99, 235),
                Margin = new Padding(0, 4, 0, 6),
                Cursor = Cursors.Hand
            };
            pnlBanner.Click += (s, e) => TriggerFocusAndClose();

            var lblBannerText = new Label
            {
                Text = proc.needsHumanInput 
                    ? "🚨 AÇÃO HUMANA NECESSÁRIA • CLIQUE AQUI PARA ABRIR A JANELA DO APLICATIVO"
                    : "🎯 CLIQUE AQUI (OU EM QUALQUER LUGAR) PARA ABRIR A JANELA DO APLICATIVO",
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Cursor = Cursors.Hand
            };
            lblBannerText.Click += (s, e) => TriggerFocusAndClose();
            pnlBanner.Controls.Add(lblBannerText);

            var lblHint = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                Text = "💡 Ao clicar nesta subjanela, o foco muda para o aplicativo e a janela fecha automaticamente. (ESC para cancelar)",
                Font = new Font("Segoe UI", 8.2f, FontStyle.Italic),
                ForeColor = Color.FromArgb(148, 163, 184),
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand
            };
            lblHint.Click += (s, e) => TriggerFocusAndClose();

            // 3. Three Detail Cards in TableLayoutPanel
            var tlp = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 175,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 4, 0, 0),
                Cursor = Cursors.Hand
            };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            tlp.Click += (s, e) => TriggerFocusAndClose();

            var card1 = CreateCard("📌 IDENTIFICAÇÃO & STATUS", Color.FromArgb(251, 191, 36));
            AddField(card1, "Aplicação:", proc.friendlyName, true, Color.White, 32);
            AddField(card1, "Executável:", proc.processName + ".exe (PID " + proc.pid + ")", false, Color.FromArgb(203, 213, 225), 58);
            AddField(card1, "Categoria:", proc.category, false, Color.FromArgb(203, 213, 225), 84);
            AddField(card1, "Status:", proc.needsHumanInput ? "🔴 AGUARDANDO AÇÃO HUMANA" : "🟢 Em Execução / Ativo",
                true, proc.needsHumanInput ? Color.FromArgb(248, 113, 113) : Color.FromArgb(74, 222, 128), 110);
            AddField(card1, "Motivo:", proc.statusReason, false, Color.FromArgb(226, 232, 240), 136, 46);

            var card2 = CreateCard("⚡ RECURSOS DO SISTEMA", Color.FromArgb(74, 222, 128));
            AddField(card2, "Uso de CPU:", proc.cpuPercent.ToString("0.0") + " %", true, Color.FromArgb(56, 189, 248), 32);
            AddField(card2, "Memória RAM:", proc.memoryMB.ToString("0.0") + " MB", false, Color.FromArgb(203, 213, 225), 58);
            AddField(card2, "Pico de RAM:", (proc.peakMemoryMB > 0 ? proc.peakMemoryMB.ToString("0.0") + " MB" : "--"), false, Color.FromArgb(203, 213, 225), 84);
            AddField(card2, "Threads / Prioridade:", (proc.threadsCount > 0 ? proc.threadsCount.ToString() : "--") + " | " + (string.IsNullOrEmpty(proc.priority) ? "Normal" : proc.priority), false, Color.FromArgb(203, 213, 225), 110);
            AddField(card2, "Tempo Ativo:", proc.uptimeHuman + " (Início: " + proc.startTime + ")", false, Color.FromArgb(203, 213, 225), 136, 46);

            var card3 = CreateCard("🪟 JANELA & COMANDO", Color.FromArgb(192, 132, 252));
            AddField(card3, "Título da Janela:", (string.IsNullOrEmpty(proc.windowTitle) ? "(Nenhuma janela visível direta)" : proc.windowTitle), true, Color.White, 32, 40);
            AddField(card3, "Caminho no Disco:", (string.IsNullOrEmpty(proc.executablePath) ? "--" : proc.executablePath), false, Color.FromArgb(203, 213, 225), 76, 46);
            AddField(card3, "Linha de Comando:", (string.IsNullOrEmpty(proc.commandLine) ? "--" : proc.commandLine), false, Color.FromArgb(148, 163, 184), 126, 56);

            tlp.Controls.Add(card1, 0, 0);
            tlp.Controls.Add(card2, 1, 0);
            tlp.Controls.Add(card3, 2, 0);

            // 4. Live Telemetry Graph Panel (Últimos 60 segundos)
            pnlGraph = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 34, 50),
                Padding = new Padding(10),
                Margin = new Padding(0, 6, 0, 6),
                Cursor = Cursors.Hand
            };
            pnlGraph.Paint += DrawTelemetryGraph;
            pnlGraph.Click += (s, e) => TriggerFocusAndClose();

            lblGraphTelemetry = new Label
            {
                Text = "📈 TELEMETRIA EM TEMPO REAL (ÚLTIMOS 60s) • CPU (Ciano) & RAM (Roxo)",
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(10, 6),
                AutoSize = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            lblGraphTelemetry.Click += (s, e) => TriggerFocusAndClose();
            pnlGraph.Controls.Add(lblGraphTelemetry);

            // 5. Bottom Action Controls (Encerrar Processo & Prioridade)
            var pnlBottomControls = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = Color.Transparent,
                Padding = new Padding(4, 6, 4, 0)
            };

            var lblPriority = new Label
            {
                Text = "⚙️ Prioridade:",
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Location = new Point(4, 12)
            };

            cmbPriority = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 140,
                Location = new Point(95, 8),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            cmbPriority.Items.AddRange(new object[] { "RealTime", "High", "AboveNormal", "Normal", "BelowNormal", "Idle" });
            int priIdx = cmbPriority.FindString(proc.priority);
            cmbPriority.SelectedIndex = priIdx >= 0 ? priIdx : 3;
            cmbPriority.SelectedIndexChanged += (s, e) =>
            {
                try
                {
                    var p = Process.GetProcessById(proc.pid);
                    ProcessPriorityClass targetClass = (ProcessPriorityClass)Enum.Parse(typeof(ProcessPriorityClass), cmbPriority.SelectedItem.ToString());
                    p.PriorityClass = targetClass;
                    proc.priority = targetClass.ToString();
                    lblHint.Text = "✅ Prioridade alterada para: " + proc.priority;
                    lblHint.ForeColor = Color.FromArgb(34, 197, 94);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Não foi possível alterar a prioridade: " + ex.Message, "Permissão", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            var btnKill = new Button
            {
                Text = "🛑 Encerrar Processo (Kill)",
                Size = new Size(190, 32),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(pnlBottomControls.Width - 200, 6),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(185, 28, 28),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnKill.FlatAppearance.BorderSize = 0;
            btnKill.Click += (s, e) =>
            {
                var confirm = MessageBox.Show(
                    "Deseja realmente encerrar o processo '" + proc.friendlyName + "' (PID " + proc.pid + ")?",
                    "Confirmar Encerramento",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                );
                if (confirm == DialogResult.Yes)
                {
                    try
                    {
                        var p = Process.GetProcessById(proc.pid);
                        p.Kill();
                        this.ProcessKilled = true;
                        this.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Erro ao encerrar: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            };

            pnlBottomControls.Controls.Add(lblPriority);
            pnlBottomControls.Controls.Add(cmbPriority);
            pnlBottomControls.Controls.Add(btnKill);
            pnlBottomControls.Resize += (s, e) =>
            {
                btnKill.Location = new Point(pnlBottomControls.Width - 200, 6);
            };

            mainContainer.Controls.Add(pnlGraph);
            mainContainer.Controls.Add(tlp);
            mainContainer.Controls.Add(lblHint);
            mainContainer.Controls.Add(pnlBanner);
            mainContainer.Controls.Add(pnlTop);
            mainContainer.Controls.Add(pnlBottomControls);

            this.Controls.Add(mainContainer);

            graphRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            graphRefreshTimer.Tick += (s, e) =>
            {
                if (pnlGraph != null && !pnlGraph.IsDisposed)
                {
                    pnlGraph.Invalidate();
                }
            };
            graphRefreshTimer.Start();
        }

        private void DrawTelemetryGraph(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var rect = pnlGraph.ClientRectangle;
            int topOffset = 26;
            int bottomOffset = 18;
            int graphH = rect.Height - topOffset - bottomOffset;
            int graphW = rect.Width - 20;

            if (graphH <= 10 || graphW <= 10) return;

            // Subtle grid lines
            using (var penGrid = new Pen(Color.FromArgb(40, 55, 78), 1))
            {
                g.DrawLine(penGrid, 10, topOffset, 10 + graphW, topOffset);
                g.DrawLine(penGrid, 10, topOffset + graphH / 2, 10 + graphW, topOffset + graphH / 2);
                g.DrawLine(penGrid, 10, topOffset + graphH, 10 + graphW, topOffset + graphH);
            }

            var hist = Program.GetHistory(proc.pid);
            var cpuList = hist.CpuSamples.ToList();
            var memList = hist.MemSamples.ToList();

            if (cpuList.Count < 2)
            {
                using (var brushText = new SolidBrush(Color.FromArgb(148, 163, 184)))
                {
                    g.DrawString("Coletando amostras de telemetria...", new Font("Segoe UI", 9f), brushText, 14, topOffset + 20);
                }
                return;
            }

            // Plot CPU (0 to 100%)
            var cpuPoints = new List<PointF>();
            float stepX = (float)graphW / Math.Max(1, cpuList.Count - 1);
            for (int i = 0; i < cpuList.Count; i++)
            {
                float x = 10 + i * stepX;
                float yVal = (float)Math.Min(100.0, Math.Max(0.0, cpuList[i]));
                float y = topOffset + graphH - (yVal / 100.0f) * graphH;
                cpuPoints.Add(new PointF(x, y));
            }

            using (var penCpu = new Pen(Color.FromArgb(56, 189, 248), 2.2f))
            {
                g.DrawLines(penCpu, cpuPoints.ToArray());
            }

            // Plot RAM normalized
            double maxMem = memList.Count > 0 ? Math.Max(100.0, memList.Max() * 1.2) : 500.0;
            var memPoints = new List<PointF>();
            for (int i = 0; i < memList.Count; i++)
            {
                float x = 10 + i * stepX;
                float yVal = (float)Math.Min(maxMem, Math.Max(0.0, memList[i]));
                float y = topOffset + graphH - (yVal / (float)maxMem) * graphH;
                memPoints.Add(new PointF(x, y));
            }

            using (var penMem = new Pen(Color.FromArgb(192, 132, 252), 2.2f))
            {
                g.DrawLines(penMem, memPoints.ToArray());
            }

            // Current metrics display
            double lastCpu = cpuList.Count > 0 ? cpuList[cpuList.Count - 1] : 0.0;
            double lastMem = memList.Count > 0 ? memList[memList.Count - 1] : 0.0;
            string liveLabel = string.Format("CPU: {0:F1}% | RAM: {1:F1} MB", lastCpu, lastMem);
            using (var brushLive = new SolidBrush(Color.FromArgb(248, 250, 252)))
            {
                var sz = g.MeasureString(liveLabel, new Font("Segoe UI", 8.2f, FontStyle.Bold));
                g.DrawString(liveLabel, new Font("Segoe UI", 8.2f, FontStyle.Bold), brushLive, rect.Width - sz.Width - 14, 6);
            }
        }

        private Panel CreateCard(string title, Color headerColor)
        {
            var pnl = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 41, 59),
                Margin = new Padding(4),
                Padding = new Padding(12, 10, 12, 10),
                Cursor = Cursors.Hand
            };
            pnl.Click += (s, e) => TriggerFocusAndClose();

            var lblT = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
                ForeColor = headerColor,
                Location = new Point(10, 8),
                AutoSize = true,
                Cursor = Cursors.Hand
            };
            lblT.Click += (s, e) => TriggerFocusAndClose();
            pnl.Controls.Add(lblT);

            return pnl;
        }

        private void AddField(Panel parent, string prefix, string val, bool isBold, Color color, int topY, int height = 22)
        {
            var lbl = new Label
            {
                Text = prefix + " " + val,
                Font = new Font("Segoe UI", isBold ? 9f : 8.3f, isBold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = color,
                Location = new Point(10, topY),
                AutoSize = false,
                Width = Math.Max(120, parent.Width - 20),
                Height = height,
                AutoEllipsis = true,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            lbl.Click += (s, e) => TriggerFocusAndClose();
            parent.Controls.Add(lbl);
        }

        private void TriggerFocusAndClose()
        {
            this.FocusRequested = true;
            this.Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (graphRefreshTimer != null) graphRefreshTimer.Stop();
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
