using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using QRCoder;

[assembly: AssemblyTitle("AI Process Monitor")]
[assembly: AssemblyDescription("Monitor de Processos de IA e Ação Humana em Tempo Real")]
[assembly: AssemblyCompany("Julianmel")]
[assembly: AssemblyProduct("AI Process Monitor")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("1.7.0.0")]
[assembly: AssemblyFileVersion("1.7.0.0")]
[assembly: AssemblyInformationalVersion("1.7.0")]

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

    #region Windows Startup & System Integration
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "AIProcessMonitor";

        public static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    var val = key.GetValue(AppName) as string;
                    return !string.IsNullOrEmpty(val);
                }
            }
            catch { return false; }
        }

        public static bool SetStartup(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;
                    if (enable)
                    {
                        key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                    return true;
                }
            }
            catch { return false; }
        }
    }
    #endregion

    #region Configuration & Preferences Manager
    public class AppConfig
    {
        public string WebhookUrl { get; set; }
        public bool WebhookEnabled { get; set; }
        public int WebhookCooldownSec { get; set; }
        public string SoundMode { get; set; } // "VoiceMaria", "Beep", "Silent"
        public int AlertIntervalSec { get; set; } // 2, 4, 8, 15 (default 4)
        public bool TrayNotificationsEnabled { get; set; }
        public List<string> CustomProcessNames { get; set; }

        public AppConfig()
        {
            WebhookUrl = "";
            WebhookEnabled = false;
            WebhookCooldownSec = 60;
            SoundMode = "VoiceMaria";
            AlertIntervalSec = 4;
            TrayNotificationsEnabled = true;
            CustomProcessNames = new List<string>();
        }
    }

    public static class ConfigManager
    {
        private static readonly object configLock = new object();
        private static AppConfig currentConfig = null;

        public static string ConfigDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIProcessMonitor");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDir, "config.json"); }
        }

        public static AppConfig GetConfig()
        {
            lock (configLock)
            {
                if (currentConfig != null) return currentConfig;

                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                        var ser = new JavaScriptSerializer();
                        currentConfig = ser.Deserialize<AppConfig>(json) ?? new AppConfig();
                        if (currentConfig.CustomProcessNames == null) currentConfig.CustomProcessNames = new List<string>();
                        if (string.IsNullOrEmpty(currentConfig.SoundMode)) currentConfig.SoundMode = "VoiceMaria";
                        if (currentConfig.AlertIntervalSec <= 0) currentConfig.AlertIntervalSec = 4;
                        return currentConfig;
                    }
                }
                catch { }

                currentConfig = new AppConfig();
                return currentConfig;
            }
        }

        public static void SaveConfig(AppConfig cfg)
        {
            lock (configLock)
            {
                currentConfig = cfg;
                try
                {
                    var ser = new JavaScriptSerializer();
                    string json = ser.Serialize(cfg);
                    File.WriteAllText(ConfigPath, json, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    public static class WebhookManager
    {
        private static DateTime lastSentTime = DateTime.MinValue;
        private static string lastSentKey = null;
        private static readonly object webhookLock = new object();

        public static void SendAlertNotification(string processName, int pid, string reason)
        {
            var cfg = ConfigManager.GetConfig();
            if (!cfg.WebhookEnabled || string.IsNullOrEmpty(cfg.WebhookUrl)) return;

            string key = string.Format("{0}|{1}|{2}", processName, pid, reason);
            var now = DateTime.Now;

            lock (webhookLock)
            {
                if (key == lastSentKey && (now - lastSentTime).TotalSeconds < Math.Max(15, cfg.WebhookCooldownSec))
                {
                    return;
                }
                lastSentKey = key;
                lastSentTime = now;
            }

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    PostWebhookPayload(cfg.WebhookUrl, processName, pid, reason);
                }
                catch { }
            });
        }

        private static void PostWebhookPayload(string url, string processName, int pid, string reason)
        {
            var ser = new JavaScriptSerializer();
            var payload = new
            {
                username = "AI Process Monitor",
                content = string.Format("🚨 **Atenção:** `{0}` (PID {1}) aguarda sua ação humana no computador!\n> **Motivo:** {2}", processName, pid, reason),
                embeds = new[]
                {
                    new
                    {
                        title = "🚨 Ação Humana Necessária: " + processName,
                        description = reason,
                        color = 15682620, // Red
                        fields = new[]
                        {
                            new { name = "Processo / Agente", value = processName, @inline = true },
                            new { name = "PID", value = pid.ToString(), @inline = true },
                            new { name = "Horário", value = DateTime.Now.ToString("HH:mm:ss"), @inline = true }
                        },
                        footer = new { text = "AI Process Monitor v" + Program.AppVersion }
                    }
                }
            };

            string json = ser.Serialize(payload);
            byte[] data = Encoding.UTF8.GetBytes(json);

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.UserAgent = "AIProcessMonitor-WebhookAgent";
            req.Timeout = 10000;
            req.ContentLength = data.Length;

            using (var stream = req.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                // OK
            }
        }

        public static bool TestWebhook(string url, out string message)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var payload = new
                {
                    username = "AI Process Monitor",
                    content = "🔔 **Teste de Notificação Externa:** O Webhook foi configurado e conectado com sucesso ao **AI Process Monitor v" + Program.AppVersion + "**!",
                    embeds = new[]
                    {
                        new
                        {
                            title = "✅ Conexão Estabelecida com Sucesso",
                            description = "Você receberá alertas neste canal sempre que um processo de inteligência artificial solicitar ação humana.",
                            color = 2278772, // Green
                            fields = new[]
                            {
                                new { name = "Status", value = "Online / Operacional", @inline = true },
                                new { name = "Data e Hora", value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), @inline = true }
                            },
                            footer = new { text = "AI Process Monitor • Teste de Webhook" }
                        }
                    }
                };

                string json = ser.Serialize(payload);
                byte[] data = Encoding.UTF8.GetBytes(json);

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.UserAgent = "AIProcessMonitor-WebhookTest";
                req.Timeout = 8000;
                req.ContentLength = data.Length;

                using (var stream = req.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    message = "Webhook disparado com sucesso (HTTP " + (int)resp.StatusCode + ").";
                    return true;
                }
            }
            catch (Exception ex)
            {
                message = "Erro ao enviar webhook: " + ex.Message;
                return false;
            }
        }
    }
    #endregion

    #region Alert History & Audit Logging Manager
    public class AlertEvent
    {
        public string Id { get; set; }
        public int Pid { get; set; }
        public string ProcessName { get; set; }
        public string FriendlyName { get; set; }
        public string Reason { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? ResolvedTime { get; set; }
        public double? DurationSeconds { get; set; }
        public bool IsResolved { get; set; }

        public AlertEvent()
        {
            Id = Guid.NewGuid().ToString("N");
            StartTime = DateTime.Now;
            IsResolved = false;
        }

        public string FormattedDuration
        {
            get
            {
                if (!IsResolved)
                {
                    double wait = (DateTime.Now - StartTime).TotalSeconds;
                    if (wait < 60) return string.Format("{0:0}s (aguardando)", wait);
                    return string.Format("{0}m {1}s (aguardando)", (int)(wait / 60), (int)(wait % 60));
                }
                if (!DurationSeconds.HasValue) return "--";
                double s = DurationSeconds.Value;
                if (s < 60) return string.Format("{0:0} seg", s);
                int m = (int)(s / 60);
                int sec = (int)(s % 60);
                return string.Format("{0}m {1}s", m, sec);
            }
        }
    }

    public static class AlertHistoryManager
    {
        private static readonly object historyLock = new object();
        private static List<AlertEvent> events = null;
        private static readonly Dictionary<int, AlertEvent> openAlerts = new Dictionary<int, AlertEvent>();

        public static string HistoryPath
        {
            get { return Path.Combine(ConfigManager.ConfigDir, "alert_history.json"); }
        }

        private static void LoadIfNeeded()
        {
            if (events != null) return;
            events = new List<AlertEvent>();
            try
            {
                if (File.Exists(HistoryPath))
                {
                    string json = File.ReadAllText(HistoryPath, Encoding.UTF8);
                    var ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    var loaded = ser.Deserialize<List<AlertEvent>>(json);
                    if (loaded != null) events = loaded;
                }
            }
            catch { }
        }

        private static void SaveInternal()
        {
            try
            {
                var ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                string json = ser.Serialize(events);
                File.WriteAllText(HistoryPath, json, Encoding.UTF8);
            }
            catch { }
        }

        public static void RecordAlertState(List<ProcessInfo> currentProcs)
        {
            lock (historyLock)
            {
                LoadIfNeeded();
                bool changed = false;

                var activePids = new HashSet<int>();

                if (currentProcs != null)
                {
                    foreach (var p in currentProcs)
                    {
                        if (p.needsHumanInput)
                        {
                            activePids.Add(p.pid);
                            if (!openAlerts.ContainsKey(p.pid))
                            {
                                var ev = new AlertEvent
                                {
                                    Pid = p.pid,
                                    ProcessName = p.processName,
                                    FriendlyName = p.friendlyName,
                                    Reason = p.statusReason,
                                    StartTime = DateTime.Now,
                                    IsResolved = false
                                };
                                openAlerts[p.pid] = ev;
                                events.Insert(0, ev);
                                if (events.Count > 500) events.RemoveAt(events.Count - 1);
                                changed = true;

                                // Trigger Webhook notification
                                WebhookManager.SendAlertNotification(p.friendlyName, p.pid, p.statusReason);
                            }
                        }
                    }
                }

                // Check alerts that were resolved
                var pidsToClose = new List<int>();
                foreach (var kvp in openAlerts)
                {
                    if (!activePids.Contains(kvp.Key))
                    {
                        pidsToClose.Add(kvp.Key);
                    }
                }

                foreach (int pid in pidsToClose)
                {
                    var ev = openAlerts[pid];
                    ev.ResolvedTime = DateTime.Now;
                    ev.DurationSeconds = Math.Max(0, (ev.ResolvedTime.Value - ev.StartTime).TotalSeconds);
                    ev.IsResolved = true;
                    openAlerts.Remove(pid);
                    changed = true;
                }

                if (changed)
                {
                    SaveInternal();
                }
            }
        }

        public static List<AlertEvent> GetEvents()
        {
            lock (historyLock)
            {
                LoadIfNeeded();
                return new List<AlertEvent>(events);
            }
        }

        public static void ClearHistory()
        {
            lock (historyLock)
            {
                LoadIfNeeded();
                events.Clear();
                openAlerts.Clear();
                SaveInternal();
            }
        }
    }
    #endregion

    public static class Program
    {
        public const string AppVersion = "1.7.0";
        public const string BuildDate = "2026-09-14";

        public static int port = 3333;
        public static string LocalIp = "127.0.0.1";
        private static TcpListener tcpServer;
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
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                try
                {
                    string resName = new AssemblyName(eventArgs.Name).Name + ".dll";
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
                    {
                        if (s == null) return null;
                        byte[] d = new byte[s.Length];
                        s.Read(d, 0, d.Length);
                        return Assembly.Load(d);
                    }
                }
                catch { return null; }
            };

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
                try { if (tcpServer != null) tcpServer.Stop(); } catch { }
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

        #region Network IP & QR Code Generation (Fase 4)
        public static string GetLocalIpAddress()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    var ep = socket.LocalEndPoint as IPEndPoint;
                    if (ep != null) return ep.Address.ToString();
                }
            }
            catch { }

            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip) &&
                        !ip.ToString().StartsWith("169.254."))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }

            return "127.0.0.1";
        }

        public static Bitmap GenerateQrCodeBitmap(string text, int pixelsPerModule = 8)
        {
            using (var qrGen = new QRCodeGenerator())
            using (var qrData = qrGen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M))
            using (var qrCode = new QRCode(qrData))
            {
                return qrCode.GetGraphic(pixelsPerModule, Color.Black, Color.White, true);
            }
        }

        public static byte[] GenerateQrCodePng(string text, int pixelsPerModule = 8)
        {
            using (var bmp = GenerateQrCodeBitmap(text, pixelsPerModule))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        #region Audio WAV Generation for Background Mobile Playback
        public static byte[] GenerateSilenceWav()
        {
            int sampleRate = 8000;
            int numSamples = 800; // 0.1s silence
            byte[] wav = new byte[44 + numSamples];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            BitConverter.GetBytes(36 + numSamples).CopyTo(wav, 4);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);
            BitConverter.GetBytes((short)1).CopyTo(wav, 22);
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 28);
            BitConverter.GetBytes((short)1).CopyTo(wav, 32);
            BitConverter.GetBytes((short)8).CopyTo(wav, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
            BitConverter.GetBytes(numSamples).CopyTo(wav, 40);
            for (int i = 0; i < numSamples; i++) wav[44 + i] = 128;
            return wav;
        }

        public static byte[] GenerateAlertChimeWav()
        {
            int sampleRate = 16000;
            double duration = 0.6;
            int numSamples = (int)(sampleRate * duration);
            byte[] wav = new byte[44 + numSamples];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            BitConverter.GetBytes(36 + numSamples).CopyTo(wav, 4);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);
            BitConverter.GetBytes((short)1).CopyTo(wav, 22);
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 28);
            BitConverter.GetBytes((short)1).CopyTo(wav, 32);
            BitConverter.GetBytes((short)8).CopyTo(wav, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
            BitConverter.GetBytes(numSamples).CopyTo(wav, 40);

            int half = numSamples / 2;
            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / sampleRate;
                double freq = i < half ? 660.0 : 880.0;
                int subIdx = i % half;
                double decay = Math.Exp(-3.0 * subIdx / half);
                double sample = Math.Sin(2.0 * Math.PI * freq * t) * decay;
                wav[44 + i] = (byte)(128 + (int)(sample * 120));
            }
            return wav;
        }
        #endregion
        #endregion

        private static void StartServer()
        {
            LocalIp = GetLocalIpAddress();
            bool started = false;
            while (!started && port < 3400)
            {
                try
                {
                    tcpServer = new TcpListener(IPAddress.Any, port);
                    tcpServer.Start();
                    started = true;
                }
                catch
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
            while (tcpServer != null)
            {
                try
                {
                    var client = tcpServer.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(c => HandleClientConnection((TcpClient)c), client);
                }
                catch
                {
                    break;
                }
            }
        }

        private static void HandleClientConnection(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 6000;
                client.SendTimeout = 6000;
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[8192];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return;

                    string raw = Encoding.UTF8.GetString(buffer, 0, read);
                    int firstLineEnd = raw.IndexOf("\r\n");
                    if (firstLineEnd < 0) firstLineEnd = raw.IndexOf("\n");
                    if (firstLineEnd < 0) return;

                    string reqLine = raw.Substring(0, firstLineEnd).Trim();
                    string[] parts = reqLine.Split(' ');
                    if (parts.Length < 2) return;

                    string method = parts[0].ToUpperInvariant();
                    string urlStr = parts[1];

                    string path = urlStr;
                    string query = "";
                    int qIdx = urlStr.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        path = urlStr.Substring(0, qIdx);
                        query = urlStr.Substring(qIdx + 1);
                    }

                    var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(query))
                    {
                        string[] pairs = query.Split('&');
                        foreach (var pair in pairs)
                        {
                            int eq = pair.IndexOf('=');
                            if (eq >= 0)
                            {
                                string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                                string v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                                queryParams[k] = v;
                            }
                            else
                            {
                                queryParams[Uri.UnescapeDataString(pair)] = "";
                            }
                        }
                    }

                    if (method == "OPTIONS")
                    {
                        SendHttpResponse(stream, 204, "text/plain", new byte[0]);
                        return;
                    }

                    if (path == "/" || path == "/index.html")
                    {
                        byte[] buf = Encoding.UTF8.GetBytes(embeddedHtml);
                        SendHttpResponse(stream, 200, "text/html; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/processes")
                    {
                        string json = GetProcessesJson();
                        byte[] buf = Encoding.UTF8.GetBytes(json);
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/network")
                    {
                        var ser = new JavaScriptSerializer();
                        string localUrl = "http://" + LocalIp + ":" + port;
                        string json = ser.Serialize(new
                        {
                            ip = LocalIp,
                            port = port,
                            url = localUrl,
                            localhost = "http://localhost:" + port
                        });
                        byte[] buf = Encoding.UTF8.GetBytes(json);
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/qr")
                    {
                        string urlToEncode = "http://" + LocalIp + ":" + port;
                        byte[] qrBytes = GenerateQrCodePng(urlToEncode, 8);
                        SendHttpResponse(stream, 200, "image/png", qrBytes);
                        return;
                    }

                    if (path == "/api/audio/silence.wav")
                    {
                        byte[] wav = GenerateSilenceWav();
                        SendHttpResponse(stream, 200, "audio/wav", wav);
                        return;
                    }

                    if (path == "/api/audio/alert.wav")
                    {
                        byte[] wav = GenerateAlertChimeWav();
                        SendHttpResponse(stream, 200, "audio/wav", wav);
                        return;
                    }

                    if (path == "/manifest.json")
                    {
                        string manifest = "{\"name\":\"AI Process Monitor\",\"short_name\":\"AI Monitor\",\"start_url\":\"/\",\"display\":\"standalone\",\"background_color\":\"#0a0f1d\",\"theme_color\":\"#6366f1\"}";
                        byte[] buf = Encoding.UTF8.GetBytes(manifest);
                        SendHttpResponse(stream, 200, "application/manifest+json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/focus")
                    {
                        string pidStr;
                        queryParams.TryGetValue("pid", out pidStr);
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
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/kill")
                    {
                        string pidStr;
                        queryParams.TryGetValue("pid", out pidStr);
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
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/mute")
                    {
                        string duration;
                        queryParams.TryGetValue("duration", out duration);
                        if (string.IsNullOrEmpty(duration)) queryParams.TryGetValue("mode", out duration);

                        if (duration == "15m") MuteManager.MuteFor(TimeSpan.FromMinutes(15));
                        else if (duration == "30m") MuteManager.MuteFor(TimeSpan.FromMinutes(30));
                        else if (duration == "1h") MuteManager.MuteFor(TimeSpan.FromHours(1));
                        else if (duration == "indefinite") MuteManager.MuteIndefinitely();
                        else if (duration == "unmute") MuteManager.Unmute();

                        var ser = new JavaScriptSerializer();
                        string json = ser.Serialize(new
                        {
                            isMuted = MuteManager.IsMuted,
                            isIndefinite = MuteManager.IsMutedIndefinitely,
                            muteUntilUtc = MuteManager.MuteUntilUtc.ToString("o")
                        });

                        byte[] buf = Encoding.UTF8.GetBytes(json);
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/export")
                    {
                        string fmt;
                        queryParams.TryGetValue("format", out fmt);
                        if (string.IsNullOrEmpty(fmt)) fmt = "json";

                        var list = ScanProcesses();
                        if (fmt.ToLowerInvariant() == "csv")
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine("PID,FriendlyName,ProcessName,Category,CpuPercent,MemoryMB,NeedsHumanInput,StatusReason,WindowTitle,Uptime");
                            foreach (var p in list)
                            {
                                sb.AppendLine(string.Format("\"{0}\",\"{1}\",\"{2}\",\"{3}\",\"{4:0.0}\",\"{5:0.0}\",\"{6}\",\"{7}\",\"{8}\",\"{9}\"",
                                    p.pid,
                                    (p.friendlyName ?? "").Replace("\"", "\"\""),
                                    (p.processName ?? "").Replace("\"", "\"\""),
                                    (p.category ?? "").Replace("\"", "\"\""),
                                    p.cpuPercent,
                                    p.memoryMB,
                                    p.needsHumanInput,
                                    (p.statusReason ?? "").Replace("\"", "\"\""),
                                    (p.windowTitle ?? "").Replace("\"", "\"\""),
                                    (p.uptimeHuman ?? "").Replace("\"", "\"\"")
                                ));
                            }
                            byte[] buf = Encoding.UTF8.GetBytes(sb.ToString());
                            SendHttpResponse(stream, 200, "text/csv; charset=utf-8", buf,
                                "attachment; filename=\"ai_processes_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv\"");
                            return;
                        }
                        else
                        {
                            var ser = new JavaScriptSerializer();
                            ser.MaxJsonLength = int.MaxValue;
                            string json = ser.Serialize(new
                            {
                                version = AppVersion,
                                exportedAt = DateTime.UtcNow.ToString("o"),
                                count = list.Count,
                                processes = list
                            });
                            byte[] buf = Encoding.UTF8.GetBytes(json);
                            SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf,
                                "attachment; filename=\"ai_processes_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json\"");
                            return;
                        }
                    }

                    if (path == "/api/health")
                    {
                        var ser = new JavaScriptSerializer();
                        string json = ser.Serialize(new
                        {
                            status = "ok",
                            version = AppVersion,
                            buildDate = BuildDate,
                            localIp = LocalIp,
                            port = port,
                            timestamp = DateTime.UtcNow.ToString("o")
                        });

                        byte[] buf = Encoding.UTF8.GetBytes(json);
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/history")
                    {
                        var evs = AlertHistoryManager.GetEvents();
                        var resolved = evs.Where(e => e.IsResolved && e.DurationSeconds.HasValue).ToList();
                        double avgWait = resolved.Count > 0 ? Math.Round(resolved.Average(e => e.DurationSeconds.Value), 1) : 0.0;

                        var ser = new JavaScriptSerializer();
                        ser.MaxJsonLength = int.MaxValue;
                        string json = ser.Serialize(new
                        {
                            totalAlerts = evs.Count,
                            resolvedAlerts = resolved.Count,
                            averageResponseSeconds = avgWait,
                            events = evs
                        });

                        byte[] buf = Encoding.UTF8.GetBytes(json);
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/config")
                    {
                        var cfg = ConfigManager.GetConfig();
                        if (method == "POST")
                        {
                            string wUrl;
                            queryParams.TryGetValue("webhookUrl", out wUrl);
                            string wEnabled;
                            queryParams.TryGetValue("webhookEnabled", out wEnabled);
                            string sMode;
                            queryParams.TryGetValue("soundMode", out sMode);
                            string intervalStr;
                            queryParams.TryGetValue("alertInterval", out intervalStr);

                            if (wUrl != null) cfg.WebhookUrl = wUrl.Trim();
                            if (wEnabled != null) cfg.WebhookEnabled = (wEnabled == "true" || wEnabled == "1");
                            if (sMode != null) cfg.SoundMode = sMode;
                            int interval;
                            if (int.TryParse(intervalStr, out interval) && interval > 0) cfg.AlertIntervalSec = interval;

                            ConfigManager.SaveConfig(cfg);
                        }

                        var ser = new JavaScriptSerializer();
                        string json = ser.Serialize(new
                        {
                            webhookUrl = cfg.WebhookUrl,
                            webhookEnabled = cfg.WebhookEnabled,
                            webhookCooldownSec = cfg.WebhookCooldownSec,
                            soundMode = cfg.SoundMode,
                            alertIntervalSec = cfg.AlertIntervalSec,
                            trayNotificationsEnabled = cfg.TrayNotificationsEnabled,
                            customProcessNames = cfg.CustomProcessNames
                        });

                        byte[] buf = Encoding.UTF8.GetBytes(json);
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    if (path == "/api/webhook/test")
                    {
                        string targetUrl;
                        queryParams.TryGetValue("url", out targetUrl);
                        if (string.IsNullOrEmpty(targetUrl))
                        {
                            var cfg = ConfigManager.GetConfig();
                            targetUrl = cfg.WebhookUrl;
                        }

                        string testMsg;
                        bool ok = false;
                        if (!string.IsNullOrEmpty(targetUrl))
                        {
                            ok = WebhookManager.TestWebhook(targetUrl, out testMsg);
                        }
                        else
                        {
                            testMsg = "Nenhuma URL de Webhook informada.";
                        }

                        var ser = new JavaScriptSerializer();
                        string json = ser.Serialize(new
                        {
                            success = ok,
                            message = testMsg
                        });

                        byte[] buf = Encoding.UTF8.GetBytes(json);
                        SendHttpResponse(stream, 200, "application/json; charset=utf-8", buf);
                        return;
                    }

                    byte[] notFoundBuf = Encoding.UTF8.GetBytes("{\"error\":\"Endpoint não encontrado\"}");
                    SendHttpResponse(stream, 404, "application/json; charset=utf-8", notFoundBuf);
                }
            }
            catch { }
        }

        private static void SendHttpResponse(Stream stream, int statusCode, string contentType, byte[] body, string contentDisposition = null)
        {
            try
            {
                string statusText = statusCode == 200 ? "OK" : (statusCode == 204 ? "No Content" : (statusCode == 404 ? "Not Found" : "Error"));
                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 ").Append(statusCode).Append(" ").Append(statusText).Append("\r\n");
                sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
                sb.Append("Content-Length: ").Append(body != null ? body.Length : 0).Append("\r\n");
                sb.Append("Access-Control-Allow-Origin: *\r\n");
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                sb.Append("Connection: close\r\n");
                if (!string.IsNullOrEmpty(contentDisposition))
                {
                    sb.Append("Content-Disposition: ").Append(contentDisposition).Append("\r\n");
                }
                sb.Append("\r\n");

                byte[] headBytes = Encoding.ASCII.GetBytes(sb.ToString());
                stream.Write(headBytes, 0, headBytes.Length);
                if (body != null && body.Length > 0)
                {
                    stream.Write(body, 0, body.Length);
                }
                stream.Flush();
            }
            catch { }
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
                else if (pCmd.IndexOf("aider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pName.IndexOf("aider", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Aider AI Pair Programmer";
                    category = "Agente de IA / CLI";
                }
                else if (pName.IndexOf("cursor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("cursor.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Cursor IDE (AI Editor)";
                    category = "Assistente de IDE";
                }
                else if (pName.IndexOf("windsurf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("windsurf", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Windsurf IDE (Cascade AI)";
                    category = "Assistente de IDE";
                }
                else if (pCmd.IndexOf("cline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("roo-cline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("roo-code", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Cline / Roo Code Agent";
                    category = "Agente de IA / IDE";
                }
                else if (pCmd.IndexOf("opendevin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("all-hands", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "OpenDevin Autonomous Agent";
                    category = "Agente de IA / CLI";
                }
                else if (pCmd.IndexOf("continue", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (pCmd.IndexOf("extension", StringComparison.OrdinalIgnoreCase) >= 0 || pCmd.IndexOf("node", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    friendly = "Continue.dev Assistant";
                    category = "Assistente de IDE";
                }
                else if (pName.IndexOf("lm studio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("lmstudio", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "LM Studio Local LLM";
                    category = "Servidor de IA Local";
                }
                else if (pCmd.IndexOf("vllm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pCmd.IndexOf("localai", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "vLLM / LocalAI Server";
                    category = "Servidor de IA Local";
                }
                else if (pName.IndexOf("llama-server", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         pName.IndexOf("llama-cli", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (pName.IndexOf("main.exe", StringComparison.OrdinalIgnoreCase) >= 0 && pCmd.IndexOf("-m ", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    friendly = "llama.cpp Inference Engine";
                    category = "Servidor de IA Local";
                }

                if (friendly == null)
                {
                    var customProcs = ConfigManager.GetConfig().CustomProcessNames;
                    if (customProcs != null && customProcs.Count > 0)
                    {
                        foreach (var cp in customProcs)
                        {
                            if (string.IsNullOrEmpty(cp)) continue;
                            string cleanCp = cp.Trim();
                            if (pName.IndexOf(cleanCp, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                pCmd.IndexOf(cleanCp, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                friendly = cleanCp;
                                category = "Processo Personalizado";
                                break;
                            }
                        }
                    }
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

        // Search, Filter, Export, History, Mobile & Preferences (Fase 2, 3 & 4)
        private TextBox txtSearch;
        private ComboBox cmbStatusFilter;
        private Button btnExport;
        private Button btnHistory;
        private Button btnMobile;
        private Button btnSettings;
        private bool minimizeToTrayOnClose = true;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern Int32 SendMessage(IntPtr hWnd, int msg, IntPtr wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam);
        private const int EM_SETCUEBANNER = 0x1501;

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

            // Search Input (Fase 2)
            txtSearch = new TextBox
            {
                Size = new Size(200, 26),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.2f)
            };
            txtSearch.TextChanged += (s, e) => RenderFilteredGrid();
            txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    txtSearch.Text = "";
                    e.SuppressKeyPress = true;
                }
            };

            // Status & Category Filter ComboBox (Fase 2)
            cmbStatusFilter = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Size = new Size(165, 26),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.8f)
            };
            cmbStatusFilter.Items.AddRange(new object[] {
                "🌐 Todos os Processos",
                "🔴 Requer Atenção (Alerta)",
                "🟢 Apenas Ativos",
                "🤖 Agentes CLI",
                "💻 Assistentes IDE",
                "🧠 LLMs Locais"
            });
            cmbStatusFilter.SelectedIndex = 0;
            cmbStatusFilter.SelectedIndexChanged += (s, e) => RenderFilteredGrid();

            // Export Button (Fase 2)
            btnExport = new Button
            {
                Text = "📥 Exportar ▼",
                Size = new Size(115, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            };
            btnExport.FlatAppearance.BorderSize = 0;
            btnExport.Click += (s, e) => ShowExportMenu();

            // History Button (Fase 3)
            btnHistory = new Button
            {
                Text = "📋 Histórico",
                Size = new Size(100, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            };
            btnHistory.FlatAppearance.BorderSize = 0;
            btnHistory.Click += (s, e) =>
            {
                using (var hf = new AlertHistoryForm())
                {
                    hf.ShowDialog(this);
                }
            };

            // Mobile QR Button (Fase 4)
            btnMobile = new Button
            {
                Text = "📱 Mobile / QR",
                Size = new Size(118, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            };
            btnMobile.FlatAppearance.BorderSize = 0;
            btnMobile.Click += (s, e) =>
            {
                using (var mf = new MobileAccessForm(Program.LocalIp, apiPort))
                {
                    mf.ShowDialog(this);
                }
            };

            // Settings & Preferences Button (Fase 4)
            btnSettings = new Button
            {
                Text = "⚙️ Config.",
                Size = new Size(88, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            };
            btnSettings.FlatAppearance.BorderSize = 0;
            btnSettings.Click += (s, e) =>
            {
                using (var pf = new PreferencesConfigForm())
                {
                    pf.ShowDialog(this);
                    ApplyPreferences();
                }
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
            headerPanel.Controls.Add(txtSearch);
            headerPanel.Controls.Add(cmbStatusFilter);
            headerPanel.Controls.Add(btnExport);
            headerPanel.Controls.Add(btnHistory);
            headerPanel.Controls.Add(btnMobile);
            headerPanel.Controls.Add(btnSettings);

            Action layoutControls = () =>
            {
                btnSoundToggle.Location = new Point(headerPanel.Width - 180, 14);
                btnOpenBrowser.Location = new Point(headerPanel.Width - 305, 14);
                btnRefreshNow.Location = new Point(headerPanel.Width - 410, 14);
                cmbInterval.Location = new Point(headerPanel.Width - 515, 18);
                lblInterval.Location = new Point(headerPanel.Width - 560, 22);

                int settingsX = headerPanel.Width - 98;
                int mobileX = settingsX - 126;
                int historyX = mobileX - 108;
                int exportX = historyX - 122;
                int filterX = exportX - 170;
                int searchX = filterX - 210;
                if (searchX < 450) searchX = 450;

                btnSettings.Location = new Point(settingsX, 68);
                btnMobile.Location = new Point(mobileX, 68);
                btnHistory.Location = new Point(historyX, 68);
                btnExport.Location = new Point(exportX, 68);
                cmbStatusFilter.Location = new Point(filterX, 69);
                txtSearch.Location = new Point(searchX, 69);
            };

            layoutControls();
            headerPanel.Resize += (s, e) => layoutControls();

            try
            {
                SendMessage(txtSearch.Handle, EM_SETCUEBANNER, (IntPtr)1, "🔍 Buscar por nome, PID...");
            }
            catch { }

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
                Text = "● Monitor Ativo | Local: http://localhost:" + apiPort + " | LAN: http://" + Program.LocalIp + ":" + apiPort,
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

                var exportMenu = new ToolStripMenuItem("📥 Exportar Relatório");
                exportMenu.DropDownItems.Add("📄 Salvar como JSON (.json)", null, (s, e) => ExportProcesses("json"));
                exportMenu.DropDownItems.Add("📊 Salvar como CSV (.csv)", null, (s, e) => ExportProcesses("csv"));
                exportMenu.DropDownItems.Add("📋 Copiar Resumo para Área de Transferência", null, (s, e) => ExportProcesses("clipboard"));
                contextMenu.Items.Add(exportMenu);

                contextMenu.Items.Add("-");

                var startupItem = new ToolStripMenuItem("⚙️ Iniciar com o Windows");
                startupItem.CheckOnClick = true;
                startupItem.Checked = StartupManager.IsStartupEnabled();
                startupItem.Click += (s, e) =>
                {
                    StartupManager.SetStartup(startupItem.Checked);
                    actionLabel.Text = startupItem.Checked ? "✅ Inicialização com o Windows ATIVADA" : "ℹ️ Inicialização com o Windows DESATIVADA";
                    actionLabel.ForeColor = Color.FromArgb(34, 197, 94);
                };
                contextMenu.Items.Add(startupItem);

                var minTrayItem = new ToolStripMenuItem("⚙️ Minimizar para a Bandeja ao Fechar");
                minTrayItem.CheckOnClick = true;
                minTrayItem.Checked = minimizeToTrayOnClose;
                minTrayItem.Click += (s, e) =>
                {
                    minimizeToTrayOnClose = minTrayItem.Checked;
                    actionLabel.Text = minimizeToTrayOnClose ? "✅ Minimizar para a bandeja ao fechar ATIVADO" : "ℹ️ Fechar aplicação ao sair ATIVADO";
                    actionLabel.ForeColor = Color.FromArgb(34, 197, 94);
                };
                contextMenu.Items.Add(minTrayItem);

                contextMenu.Items.Add("-");
                contextMenu.Items.Add("📱 Conectar Celular (QR Code)...", null, (s, e) =>
                {
                    this.Show();
                    this.WindowState = FormWindowState.Normal;
                    this.BringToFront();
                    using (var mf = new MobileAccessForm(Program.LocalIp, apiPort))
                    {
                        mf.ShowDialog(this);
                    }
                });
                contextMenu.Items.Add("⚙️ Configurações & Preferências...", null, (s, e) =>
                {
                    this.Show();
                    this.WindowState = FormWindowState.Normal;
                    this.BringToFront();
                    using (var pf = new PreferencesConfigForm())
                    {
                        pf.ShowDialog(this);
                        ApplyPreferences();
                    }
                });
                contextMenu.Items.Add("📋 Histórico de Alertas & Auditoria...", null, (s, e) =>
                {
                    this.Show();
                    this.WindowState = FormWindowState.Normal;
                    this.BringToFront();
                    using (var hf = new AlertHistoryForm())
                    {
                        hf.ShowDialog(this);
                    }
                });
                contextMenu.Items.Add("🔔 Configurar Notificações (Webhook)...", null, (s, e) =>
                {
                    this.Show();
                    this.WindowState = FormWindowState.Normal;
                    this.BringToFront();
                    using (var wf = new WebhookConfigForm())
                    {
                        wf.ShowDialog(this);
                    }
                });

                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Sair", null, (s, e) =>
                {
                    minimizeToTrayOnClose = false;
                    Application.Exit();
                });

                trayIcon.ContextMenuStrip = contextMenu;
                trayIcon.DoubleClick += (s, e) =>
                {
                    this.Show();
                    this.WindowState = FormWindowState.Normal;
                    this.BringToFront();
                };

                trayIcon.BalloonTipClicked += (s, e) =>
                {
                    this.Show();
                    this.WindowState = FormWindowState.Normal;
                    this.BringToFront();
                    this.Activate();

                    var alertProc = currentProcesses.FirstOrDefault(p => p.needsHumanInput);
                    if (alertProc != null)
                    {
                        string msg;
                        Program.FocusProcessWindow(alertProc.pid, out msg);
                    }
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
            AlertHistoryManager.RecordAlertState(list);

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

                var cfg = ConfigManager.GetConfig();
                if (!hadAlertBefore && trayIcon != null && cfg.TrayNotificationsEnabled)
                {
                    try
                    {
                        trayIcon.ShowBalloonTip(3500, "🚨 Ação Humana Necessária: " + firstAlert.friendlyName,
                            firstAlert.statusReason + "\n(Clique aqui para focar no aplicativo)", ToolTipIcon.Warning);
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

            RenderFilteredGrid();
        }

        private void RenderFilteredGrid()
        {
            if (currentProcesses == null) return;

            string query = (txtSearch != null ? txtSearch.Text : "").Trim().ToLowerInvariant();
            int filterIdx = (cmbStatusFilter != null ? cmbStatusFilter.SelectedIndex : 0);

            var filtered = currentProcesses.Where(p =>
            {
                if (filterIdx == 1 && !p.needsHumanInput) return false;
                if (filterIdx == 2 && p.needsHumanInput) return false;
                if (filterIdx == 3 && (p.category == null || p.category.IndexOf("CLI", StringComparison.OrdinalIgnoreCase) < 0)) return false;
                if (filterIdx == 4 && (p.category == null || p.category.IndexOf("IDE", StringComparison.OrdinalIgnoreCase) < 0)) return false;
                if (filterIdx == 5 && (p.category == null || (p.category.IndexOf("LLM", StringComparison.OrdinalIgnoreCase) < 0 && p.category.IndexOf("Ollama", StringComparison.OrdinalIgnoreCase) < 0))) return false;

                if (!string.IsNullOrEmpty(query))
                {
                    bool match = (p.friendlyName != null && p.friendlyName.ToLowerInvariant().Contains(query))
                              || (p.processName != null && p.processName.ToLowerInvariant().Contains(query))
                              || p.pid.ToString().Contains(query)
                              || (p.category != null && p.category.ToLowerInvariant().Contains(query))
                              || (p.statusReason != null && p.statusReason.ToLowerInvariant().Contains(query))
                              || (p.windowTitle != null && p.windowTitle.ToLowerInvariant().Contains(query));
                    if (!match) return false;
                }

                return true;
            }).ToList();

            int selectedPid = -1;
            if (dgv.SelectedRows.Count > 0)
            {
                try { selectedPid = Convert.ToInt32(dgv.SelectedRows[0].Cells["colPid"].Value); } catch { }
            }

            dgv.Rows.Clear();
            for (int i = 0; i < filtered.Count; i++)
            {
                var p = filtered[i];
                string statusDisplay = p.needsHumanInput ? "🔴 AÇÃO HUMANA" : "🟢 Ativo";
                int rowIndex = dgv.Rows.Add(
                    statusDisplay,
                    p.friendlyName,
                    p.category,
                    p.pid,
                    p.cpuPercent.ToString("0.0"),
                    p.memoryMB.ToString("0.0") + " MB",
                    p.uptimeHuman,
                    p.statusReason
                );

                if (p.pid == selectedPid)
                {
                    dgv.Rows[rowIndex].Selected = true;
                }
            }

            if (!string.IsNullOrEmpty(query) || filterIdx > 0)
            {
                statusLabel.Text = string.Format("● Filtrados: {0} de {1} processos | Local: http://localhost:{2} | LAN: http://{3}:{2}", filtered.Count, currentProcesses.Count, apiPort, Program.LocalIp);
            }
            else
            {
                statusLabel.Text = string.Format("● Monitor Ativo ({0} processos) | Local: http://localhost:{1} | LAN: http://{2}:{1}", currentProcesses.Count, apiPort, Program.LocalIp);
            }
        }

        private void ShowExportMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("📄 Salvar Relatório em JSON (.json)", null, (s, e) => ExportProcesses("json"));
            menu.Items.Add("📊 Salvar Relatório em CSV (.csv)", null, (s, e) => ExportProcesses("csv"));
            menu.Items.Add("📋 Copiar Resumo para Área de Transferência", null, (s, e) => ExportProcesses("clipboard"));
            menu.Items.Add("-");

            var startupItem = new ToolStripMenuItem("⚙️ Iniciar com o Windows");
            startupItem.CheckOnClick = true;
            startupItem.Checked = StartupManager.IsStartupEnabled();
            startupItem.Click += (s, e) =>
            {
                StartupManager.SetStartup(startupItem.Checked);
                actionLabel.Text = startupItem.Checked ? "✅ Inicialização com o Windows ATIVADA" : "ℹ️ Inicialização com o Windows DESATIVADA";
                actionLabel.ForeColor = Color.FromArgb(34, 197, 94);
            };
            menu.Items.Add(startupItem);

            var minTrayItem = new ToolStripMenuItem("⚙️ Minimizar para a Bandeja ao Fechar");
            minTrayItem.CheckOnClick = true;
            minTrayItem.Checked = minimizeToTrayOnClose;
            minTrayItem.Click += (s, e) =>
            {
                minimizeToTrayOnClose = minTrayItem.Checked;
                actionLabel.Text = minimizeToTrayOnClose ? "✅ Minimizar para a bandeja ao fechar ATIVADO" : "ℹ️ Fechar aplicação ao sair ATIVADO";
                actionLabel.ForeColor = Color.FromArgb(34, 197, 94);
            };
            menu.Items.Add(minTrayItem);

            menu.Items.Add("-");
            menu.Items.Add("📋 Histórico de Alertas & Auditoria...", null, (s, e) =>
            {
                using (var hf = new AlertHistoryForm())
                {
                    hf.ShowDialog(this);
                }
            });
            menu.Items.Add("🔔 Configurar Notificações (Webhook)...", null, (s, e) =>
            {
                using (var wf = new WebhookConfigForm())
                {
                    wf.ShowDialog(this);
                }
            });

            menu.Show(btnExport, new Point(0, btnExport.Height + 2));
        }

        private void ExportProcesses(string format)
        {
            if (currentProcesses == null || currentProcesses.Count == 0)
            {
                MessageBox.Show(this, "Nenhum processo monitorado para exportar no momento.", "AI Process Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (format == "clipboard")
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== AI Process Monitor - Relatório de Processos de IA ===");
                sb.AppendLine("Data: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Total de Processos: " + currentProcesses.Count);
                sb.AppendLine(new string('-', 70));
                foreach (var p in currentProcesses)
                {
                    sb.AppendLine(string.Format("[{0}] {1} (PID {2}) | CPU: {3:0.0}% | RAM: {4:0.0}MB | Status: {5} ({6})",
                        p.category, p.friendlyName, p.pid, p.cpuPercent, p.memoryMB,
                        (p.needsHumanInput ? "AÇÃO HUMANA" : "Ativo"), p.statusReason));
                }
                try
                {
                    Clipboard.SetText(sb.ToString());
                    actionLabel.Text = "📋 Resumo copiado para a Área de Transferência com sucesso!";
                    actionLabel.ForeColor = Color.FromArgb(34, 197, 94);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Erro ao copiar para a área de transferência: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                if (format == "csv")
                {
                    sfd.Filter = "Arquivo CSV (*.csv)|*.csv|Todos os Arquivos (*.*)|*.*";
                    sfd.FileName = "ai_processes_" + timestamp + ".csv";
                }
                else
                {
                    sfd.Filter = "Arquivo JSON (*.json)|*.json|Todos os Arquivos (*.*)|*.*";
                    sfd.FileName = "ai_processes_" + timestamp + ".json";
                }

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        if (format == "csv")
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine("PID,FriendlyName,ProcessName,Category,CpuPercent,MemoryMB,NeedsHumanInput,StatusReason,WindowTitle,Uptime");
                            foreach (var p in currentProcesses)
                            {
                                sb.AppendLine(string.Format("\"{0}\",\"{1}\",\"{2}\",\"{3}\",\"{4:0.0}\",\"{5:0.0}\",\"{6}\",\"{7}\",\"{8}\",\"{9}\"",
                                    p.pid,
                                    (p.friendlyName ?? "").Replace("\"", "\"\""),
                                    (p.processName ?? "").Replace("\"", "\"\""),
                                    (p.category ?? "").Replace("\"", "\"\""),
                                    p.cpuPercent,
                                    p.memoryMB,
                                    p.needsHumanInput,
                                    (p.statusReason ?? "").Replace("\"", "\"\""),
                                    (p.windowTitle ?? "").Replace("\"", "\"\""),
                                    (p.uptimeHuman ?? "").Replace("\"", "\"\"")
                                ));
                            }
                            File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                        }
                        else
                        {
                            var ser = new JavaScriptSerializer();
                            ser.MaxJsonLength = int.MaxValue;
                            string json = ser.Serialize(new
                            {
                                version = Program.AppVersion,
                                exportedAt = DateTime.UtcNow.ToString("o"),
                                count = currentProcesses.Count,
                                processes = currentProcesses
                            });
                            File.WriteAllText(sfd.FileName, json, Encoding.UTF8);
                        }

                        actionLabel.Text = "💾 Relatório salvo em: " + Path.GetFileName(sfd.FileName);
                        actionLabel.ForeColor = Color.FromArgb(34, 197, 94);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Erro ao salvar arquivo: " + ex.Message, "Erro ao Exportar", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
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

        public void ApplyPreferences()
        {
            var cfg = ConfigManager.GetConfig();
            if (audioAlertTimer != null)
            {
                int sec = cfg.AlertIntervalSec > 0 ? cfg.AlertIntervalSec : 4;
                audioAlertTimer.Interval = sec * 1000;
            }
        }

        private void PlayHumanAlertSound()
        {
            if (MuteManager.IsMuted) return;

            var cfg = ConfigManager.GetConfig();
            string mode = cfg.SoundMode ?? "VoiceMaria";
            if (mode == "Silent") return;

            if (mode == "Beep")
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
            else // VoiceMaria / Default
            {
                try
                {
                    System.Media.SystemSounds.Exclamation.Play();
                }
                catch { }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (minimizeToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                if (trayIcon != null)
                {
                    try
                    {
                        trayIcon.ShowBalloonTip(2500, "AI Process Monitor",
                            "O monitor continua em execução na bandeja do sistema.", ToolTipIcon.Info);
                    }
                    catch { }
                }
                return;
            }

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

    #region Alert History & Audit SubWindow (Fase 3)
    public class AlertHistoryForm : Form
    {
        private DataGridView dgvHistory;
        private Label lblTotalAlerts;
        private Label lblResolvedAlerts;
        private Label lblAvgDuration;

        public AlertHistoryForm()
        {
            this.Text = "📋 Histórico de Alertas & Auditoria de Ação Humana";
            this.Size = new Size(940, 560);
            this.MinimumSize = new Size(800, 440);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.FromArgb(248, 250, 252);
            this.Font = new Font("Segoe UI", 9.5f);
            this.KeyPreview = true;
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) this.Close(); };

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 110,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(20, 12, 20, 10)
            };

            var lblTitle = new Label
            {
                Text = "📋 HISTÓRICO DE ALERTAS & AUDITORIA DE AÇÃO HUMANA",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(20, 12),
                AutoSize = true
            };

            var lblSub = new Label
            {
                Text = "Registro cronológico de pausas de agentes de IA, motivos de solicitação e tempo de resposta do operador",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(22, 38),
                AutoSize = true
            };

            var pnlCard1 = CreateSummaryCard("TOTAL DE EVENTOS", "0", Color.FromArgb(56, 189, 248), 20, 62, out lblTotalAlerts);
            var pnlCard2 = CreateSummaryCard("ALERTAS ATENDIDOS", "0", Color.FromArgb(34, 197, 94), 195, 62, out lblResolvedAlerts);
            var pnlCard3 = CreateSummaryCard("TEMPO MÉDIO DE RESPOSTA", "0s", Color.FromArgb(250, 204, 21), 370, 62, out lblAvgDuration);

            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSub);
            header.Controls.Add(pnlCard1);
            header.Controls.Add(pnlCard2);
            header.Controls.Add(pnlCard3);

            var bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(15, 8, 15, 8)
            };

            var btnRefresh = new Button
            {
                Text = "🔄 Atualizar",
                Size = new Size(110, 32),
                Location = new Point(15, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => LoadData();

            var btnExportCsv = new Button
            {
                Text = "📊 Exportar CSV",
                Size = new Size(130, 32),
                Location = new Point(135, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnExportCsv.FlatAppearance.BorderSize = 0;
            btnExportCsv.Click += (s, e) => ExportCsv();

            var btnClear = new Button
            {
                Text = "🗑️ Limpar Histórico",
                Size = new Size(140, 32),
                Location = new Point(275, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(127, 29, 29),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f)
            };
            btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Deseja realmente limpar todo o histórico de alertas gravado?", "Confirmar Limpeza", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    AlertHistoryManager.ClearHistory();
                    LoadData();
                }
            };

            var btnClose = new Button
            {
                Text = "Fechar (ESC)",
                Size = new Size(110, 32),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(bottomBar.Width - 125, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(71, 85, 105),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f)
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => this.Close();

            bottomBar.Controls.Add(btnRefresh);
            bottomBar.Controls.Add(btnExportCsv);
            bottomBar.Controls.Add(btnClear);
            bottomBar.Controls.Add(btnClose);
            bottomBar.Resize += (s, e) => { btnClose.Location = new Point(bottomBar.Width - 125, 9); };

            dgvHistory = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(15, 23, 42),
                GridColor = Color.FromArgb(30, 41, 59),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                EnableHeadersVisualStyles = false,
                Font = new Font("Segoe UI", 9f),
                RowTemplate = { Height = 32 }
            };

            dgvHistory.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(30, 41, 59),
                SelectionForeColor = Color.FromArgb(148, 163, 184)
            };

            dgvHistory.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(248, 250, 252),
                SelectionBackColor = Color.FromArgb(15, 23, 42),
                SelectionForeColor = Color.FromArgb(56, 189, 248)
            };

            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Início", Width = 140 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Agente / Processo", Width = 180 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PID", Width = 70 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Motivo da Ação Humana", Width = 260, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tempo de Resposta", Width = 140 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", Width = 110 });

            this.Controls.Add(dgvHistory);
            this.Controls.Add(bottomBar);
            this.Controls.Add(header);

            LoadData();
        }

        private Panel CreateSummaryCard(string title, string val, Color accent, int x, int y, out Label lblVal)
        {
            var pnl = new Panel { Size = new Size(165, 40), Location = new Point(x, y), BackColor = Color.FromArgb(15, 23, 42) };
            var lblT = new Label { Text = title, Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = Color.FromArgb(148, 163, 184), Location = new Point(6, 3), AutoSize = true };
            var lblV = new Label { Text = val, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = accent, Location = new Point(6, 18), AutoSize = true };
            pnl.Controls.Add(lblT);
            pnl.Controls.Add(lblV);
            lblVal = lblV;
            return pnl;
        }

        private void LoadData()
        {
            var evs = AlertHistoryManager.GetEvents();
            lblTotalAlerts.Text = evs.Count.ToString();
            var resolved = evs.Where(e => e.IsResolved && e.DurationSeconds.HasValue).ToList();
            lblResolvedAlerts.Text = resolved.Count.ToString();

            if (resolved.Count > 0)
            {
                double avg = resolved.Average(e => e.DurationSeconds.Value);
                if (avg < 60) lblAvgDuration.Text = string.Format("{0:0.0}s", avg);
                else lblAvgDuration.Text = string.Format("{0}m {1}s", (int)(avg / 60), (int)(avg % 60));
            }
            else
            {
                lblAvgDuration.Text = "--";
            }

            dgvHistory.Rows.Clear();
            foreach (var ev in evs)
            {
                string statusText = ev.IsResolved ? "🟢 Atendido" : "🔴 Em Aberto";
                dgvHistory.Rows.Add(
                    ev.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    ev.FriendlyName,
                    ev.Pid,
                    ev.Reason,
                    ev.FormattedDuration,
                    statusText
                );
            }
        }

        private void ExportCsv()
        {
            var evs = AlertHistoryManager.GetEvents();
            if (evs.Count == 0)
            {
                MessageBox.Show(this, "Nenhum histórico disponível para exportação.", "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "Arquivo CSV (*.csv)|*.csv|Todos os Arquivos (*.*)|*.*";
                sfd.FileName = "alert_audit_history_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("ID,StartTime,ResolvedTime,DurationSeconds,ProcessName,FriendlyName,PID,Reason,IsResolved");
                        foreach (var e in evs)
                        {
                            sb.AppendLine(string.Format("\"{0}\",\"{1:yyyy-MM-dd HH:mm:ss}\",\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\",\"{7}\",\"{8}\"",
                                e.Id,
                                e.StartTime,
                                (e.ResolvedTime.HasValue ? e.ResolvedTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : ""),
                                (e.DurationSeconds.HasValue ? e.DurationSeconds.Value.ToString("0.0") : ""),
                                (e.ProcessName ?? "").Replace("\"", "\"\""),
                                (e.FriendlyName ?? "").Replace("\"", "\"\""),
                                e.Pid,
                                (e.Reason ?? "").Replace("\"", "\"\""),
                                e.IsResolved
                            ));
                        }
                        File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                        MessageBox.Show(this, "Histórico exportado com sucesso para:\n" + sfd.FileName, "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Erro ao exportar CSV: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
    }
    #endregion

    #region Webhook Configuration SubWindow (Fase 3)
    public class WebhookConfigForm : Form
    {
        private TextBox txtUrl;
        private CheckBox chkEnabled;
        private Label lblStatus;
        private Button btnTest;
        private Button btnSave;

        public WebhookConfigForm()
        {
            this.Text = "🔔 Configurar Notificações Externas (Webhook)";
            this.Size = new Size(620, 310);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.FromArgb(248, 250, 252);
            this.Font = new Font("Segoe UI", 9.5f);
            this.KeyPreview = true;
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) this.Close(); };

            var lblTitle = new Label
            {
                Text = "🔔 NOTIFICAÇÕES EXTERNAS (DISCORD / SLACK / WEBHOOK)",
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(20, 15),
                AutoSize = true
            };

            var lblSub = new Label
            {
                Text = "Envie alertas automáticos para seu canal ou automação externa quando a IA solicitar sua intervenção.",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(22, 40),
                AutoSize = true
            };

            chkEnabled = new CheckBox
            {
                Text = "Ativar notificações via Webhook ao detectar solicitações de ação humana",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                Location = new Point(24, 75),
                AutoSize = true
            };

            var lblUrlPrompt = new Label
            {
                Text = "Webhook URL (Discord, Slack, n8n, Zapier ou endpoint HTTP):",
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = Color.FromArgb(203, 213, 225),
                Location = new Point(22, 115),
                AutoSize = true
            };

            txtUrl = new TextBox
            {
                Location = new Point(24, 138),
                Size = new Size(555, 28),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f)
            };

            lblStatus = new Label
            {
                Text = "",
                Location = new Point(24, 175),
                Size = new Size(555, 24),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 8.5f)
            };

            btnTest = new Button
            {
                Text = "🔔 Testar Envio",
                Location = new Point(24, 215),
                Size = new Size(130, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnTest.FlatAppearance.BorderSize = 0;
            btnTest.Click += (s, e) =>
            {
                string url = txtUrl.Text.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    lblStatus.Text = "⚠️ Insira a URL do Webhook antes de testar.";
                    lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
                    return;
                }

                btnTest.Enabled = false;
                lblStatus.Text = "⏳ Enviando mensagem de teste...";
                lblStatus.ForeColor = Color.FromArgb(250, 204, 21);

                ThreadPool.QueueUserWorkItem(st =>
                {
                    string msg;
                    bool ok = WebhookManager.TestWebhook(url, out msg);
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        btnTest.Enabled = true;
                        lblStatus.Text = ok ? "✅ " + msg : "❌ " + msg;
                        lblStatus.ForeColor = ok ? Color.FromArgb(34, 197, 94) : Color.FromArgb(239, 68, 68);
                    });
                });
            };

            btnSave = new Button
            {
                Text = "💾 Salvar Configurações",
                Location = new Point(410, 215),
                Size = new Size(170, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += (s, e) =>
            {
                var cfg = ConfigManager.GetConfig();
                cfg.WebhookUrl = txtUrl.Text.Trim();
                cfg.WebhookEnabled = chkEnabled.Checked;
                ConfigManager.SaveConfig(cfg);
                lblStatus.Text = "✅ Configurações salvas com sucesso!";
                lblStatus.ForeColor = Color.FromArgb(34, 197, 94);
                var t = new System.Windows.Forms.Timer { Interval = 800 };
                t.Tick += (st, ev) => { t.Stop(); t.Dispose(); this.Close(); };
                t.Start();
            };

            var btnCancel = new Button
            {
                Text = "Cancelar",
                Location = new Point(310, 215),
                Size = new Size(90, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f)
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += (s, e) => this.Close();

            this.Controls.Add(lblTitle);
            this.Controls.Add(lblSub);
            this.Controls.Add(chkEnabled);
            this.Controls.Add(lblUrlPrompt);
            this.Controls.Add(txtUrl);
            this.Controls.Add(lblStatus);
            this.Controls.Add(btnTest);
            this.Controls.Add(btnCancel);
            this.Controls.Add(btnSave);

            var curr = ConfigManager.GetConfig();
            txtUrl.Text = curr.WebhookUrl ?? "";
            chkEnabled.Checked = curr.WebhookEnabled;
        }
    }
    #endregion

    #region Mobile Access SubWindow (Fase 4)
    public class MobileAccessForm : Form
    {
        public MobileAccessForm(string localIp, int port)
        {
            this.Text = "📱 Acesso no Celular / Tablet (QR Code & Wi-Fi)";
            this.Size = new Size(490, 530);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.FromArgb(248, 250, 252);
            this.Font = new Font("Segoe UI", 9f);

            string fullUrl = "http://" + localIp + ":" + port;

            var lblTitle = new Label
            {
                Text = "📱 CONECTAR CELULAR / TABLET VIA WI-FI",
                Location = new Point(24, 18),
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };

            var lblSub = new Label
            {
                Text = "Aponte a câmera do seu celular conectado ao mesmo Wi-Fi para abrir o painel:",
                Location = new Point(24, 46),
                Size = new Size(430, 28),
                ForeColor = Color.FromArgb(148, 163, 184)
            };

            var qrBox = new PictureBox
            {
                Location = new Point(135, 80),
                Size = new Size(210, 210),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            try
            {
                qrBox.Image = Program.GenerateQrCodeBitmap(fullUrl, 8);
            }
            catch (Exception ex)
            {
                lblSub.Text = "Erro ao gerar QR Code: " + ex.Message;
            }

            var cardUrl = new Panel
            {
                Location = new Point(24, 305),
                Size = new Size(430, 46),
                BackColor = Color.FromArgb(30, 41, 59),
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblUrl = new Label
            {
                Text = fullUrl,
                Location = new Point(10, 10),
                Size = new Size(410, 24),
                Font = new Font("Consolas", 11.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                TextAlign = ContentAlignment.MiddleCenter
            };
            cardUrl.Controls.Add(lblUrl);

            var btnCopy = new Button
            {
                Text = "📋 Copiar URL",
                Location = new Point(60, 365),
                Size = new Size(130, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnCopy.FlatAppearance.BorderSize = 0;
            btnCopy.Click += (s, e) =>
            {
                Clipboard.SetText(fullUrl);
                btnCopy.Text = "✅ Copiado!";
                var t = new System.Windows.Forms.Timer { Interval = 2000 };
                t.Tick += (st, ev) => { t.Stop(); t.Dispose(); btnCopy.Text = "📋 Copiar URL"; };
                t.Start();
            };

            var btnBrowser = new Button
            {
                Text = "🌐 Abrir no PC",
                Location = new Point(200, 365),
                Size = new Size(120, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnBrowser.FlatAppearance.BorderSize = 0;
            btnBrowser.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(fullUrl) { UseShellExecute = true }); } catch { }
            };

            var btnClose = new Button
            {
                Text = "Fechar",
                Location = new Point(330, 365),
                Size = new Size(95, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(203, 213, 225),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f)
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => this.Close();

            var lblTip = new Label
            {
                Text = "💡 Dica: No celular, toque no botão '🔊 Alarme Celular' para receber alertas sonoros e vibração no bolso quando uma IA precisar de atenção!",
                Location = new Point(24, 418),
                Size = new Size(430, 50),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 8.5f)
            };

            this.Controls.Add(lblTitle);
            this.Controls.Add(lblSub);
            this.Controls.Add(qrBox);
            this.Controls.Add(cardUrl);
            this.Controls.Add(btnCopy);
            this.Controls.Add(btnBrowser);
            this.Controls.Add(btnClose);
            this.Controls.Add(lblTip);
        }
    }
    #endregion

    #region Preferences SubWindow (Fase 4)
    public class PreferencesConfigForm : Form
    {
        private ComboBox cmbSoundMode;
        private ComboBox cmbInterval;
        private CheckBox chkTrayNotify;
        private TextBox txtCustomProcs;
        private Label lblStatus;

        public PreferencesConfigForm()
        {
            this.Text = "⚙️ Configurações & Preferências do Monitor";
            this.Size = new Size(580, 560);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.FromArgb(248, 250, 252);
            this.Font = new Font("Segoe UI", 9f);

            var lblTitle = new Label
            {
                Text = "⚙️ PREFERÊNCIAS DO AI PROCESS MONITOR",
                Location = new Point(24, 18),
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };

            var lblSub = new Label
            {
                Text = "Ajuste o comportamento sonoro, notificações e processos monitorados:",
                Location = new Point(24, 44),
                Size = new Size(520, 20),
                ForeColor = Color.FromArgb(148, 163, 184)
            };

            // Group 1: Som e Voz
            var grpSound = new GroupBox
            {
                Text = "🔊 Alertas Sonoros e Sintetizador de Voz",
                Location = new Point(24, 75),
                Size = new Size(520, 110),
                ForeColor = Color.FromArgb(148, 163, 184)
            };

            var lblSoundMode = new Label
            {
                Text = "Modo de Alerta Sonoro:",
                Location = new Point(16, 30),
                AutoSize = true,
                ForeColor = Color.White
            };

            cmbSoundMode = new ComboBox
            {
                Location = new Point(190, 26),
                Size = new Size(310, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White
            };
            cmbSoundMode.Items.Add("Voz Sintetizada pt-BR (Microsoft Maria)");
            cmbSoundMode.Items.Add("Bipe Clássico do Sistema");
            cmbSoundMode.Items.Add("Silencioso (Apenas Alerta Visual)");

            var lblInterval = new Label
            {
                Text = "Intervalo de Repetição:",
                Location = new Point(16, 68),
                AutoSize = true,
                ForeColor = Color.White
            };

            cmbInterval = new ComboBox
            {
                Location = new Point(190, 64),
                Size = new Size(180, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White
            };
            cmbInterval.Items.Add("2 segundos");
            cmbInterval.Items.Add("4 segundos (Padrão)");
            cmbInterval.Items.Add("8 segundos");
            cmbInterval.Items.Add("15 segundos");

            grpSound.Controls.Add(lblSoundMode);
            grpSound.Controls.Add(cmbSoundMode);
            grpSound.Controls.Add(lblInterval);
            grpSound.Controls.Add(cmbInterval);

            // Group 2: Notificações
            var grpNotify = new GroupBox
            {
                Text = "🔔 Notificações do Sistema",
                Location = new Point(24, 195),
                Size = new Size(520, 75),
                ForeColor = Color.FromArgb(148, 163, 184)
            };

            chkTrayNotify = new CheckBox
            {
                Text = "Exibir balão de notificação (BalloonTip) no Windows ao detectar alerta",
                Location = new Point(16, 26),
                Size = new Size(490, 24),
                ForeColor = Color.White,
                Checked = true
            };

            var lblNotifyHint = new Label
            {
                Text = "💡 Clicar no balão restaura o monitor e foca diretamente no processo.",
                Location = new Point(34, 48),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.2f),
                ForeColor = Color.FromArgb(148, 163, 184)
            };

            grpNotify.Controls.Add(chkTrayNotify);
            grpNotify.Controls.Add(lblNotifyHint);

            // Group 3: Processos Customizados
            var grpCustom = new GroupBox
            {
                Text = "🤖 Processos Personalizados Adicionais",
                Location = new Point(24, 280),
                Size = new Size(520, 135),
                ForeColor = Color.FromArgb(148, 163, 184)
            };

            var lblCustomPrompt = new Label
            {
                Text = "Nomes de executáveis ou comandos adicionais (um por linha, ex: meu_agente.exe):",
                Location = new Point(16, 24),
                AutoSize = true,
                ForeColor = Color.FromArgb(203, 213, 225)
            };

            txtCustomProcs = new TextBox
            {
                Location = new Point(16, 48),
                Size = new Size(485, 72),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(248, 250, 252),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle
            };

            grpCustom.Controls.Add(lblCustomPrompt);
            grpCustom.Controls.Add(txtCustomProcs);

            lblStatus = new Label
            {
                Location = new Point(24, 425),
                Size = new Size(520, 24),
                Text = "",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(34, 197, 94)
            };

            var btnOpenWebhook = new Button
            {
                Text = "🔔 Configurar Webhook...",
                Location = new Point(24, 460),
                Size = new Size(180, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f)
            };
            btnOpenWebhook.FlatAppearance.BorderSize = 0;
            btnOpenWebhook.Click += (s, e) =>
            {
                using (var wf = new WebhookConfigForm())
                {
                    wf.ShowDialog(this);
                }
            };

            var btnCancel = new Button
            {
                Text = "Cancelar",
                Location = new Point(310, 460),
                Size = new Size(95, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f)
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += (s, e) => this.Close();

            var btnSave = new Button
            {
                Text = "💾 Salvar Preferências",
                Location = new Point(415, 460),
                Size = new Size(130, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += (s, e) =>
            {
                var cfg = ConfigManager.GetConfig();
                int smIdx = cmbSoundMode.SelectedIndex;
                if (smIdx == 0) cfg.SoundMode = "VoiceMaria";
                else if (smIdx == 1) cfg.SoundMode = "Beep";
                else cfg.SoundMode = "Silent";

                int ivIdx = cmbInterval.SelectedIndex;
                if (ivIdx == 0) cfg.AlertIntervalSec = 2;
                else if (ivIdx == 1) cfg.AlertIntervalSec = 4;
                else if (ivIdx == 2) cfg.AlertIntervalSec = 8;
                else cfg.AlertIntervalSec = 15;

                cfg.TrayNotificationsEnabled = chkTrayNotify.Checked;

                var lines = txtCustomProcs.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                cfg.CustomProcessNames = lines.Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList();

                ConfigManager.SaveConfig(cfg);

                lblStatus.Text = "✅ Preferências salvas com sucesso!";
                lblStatus.ForeColor = Color.FromArgb(34, 197, 94);

                var t = new System.Windows.Forms.Timer { Interval = 800 };
                t.Tick += (st, ev) => { t.Stop(); t.Dispose(); this.Close(); };
                t.Start();
            };

            this.Controls.Add(lblTitle);
            this.Controls.Add(lblSub);
            this.Controls.Add(grpSound);
            this.Controls.Add(grpNotify);
            this.Controls.Add(grpCustom);
            this.Controls.Add(lblStatus);
            this.Controls.Add(btnOpenWebhook);
            this.Controls.Add(btnCancel);
            this.Controls.Add(btnSave);

            // Load existing
            var curr = ConfigManager.GetConfig();
            if (curr.SoundMode == "Beep") cmbSoundMode.SelectedIndex = 1;
            else if (curr.SoundMode == "Silent") cmbSoundMode.SelectedIndex = 2;
            else cmbSoundMode.SelectedIndex = 0;

            if (curr.AlertIntervalSec == 2) cmbInterval.SelectedIndex = 0;
            else if (curr.AlertIntervalSec == 8) cmbInterval.SelectedIndex = 2;
            else if (curr.AlertIntervalSec == 15) cmbInterval.SelectedIndex = 3;
            else cmbInterval.SelectedIndex = 1;

            chkTrayNotify.Checked = curr.TrayNotificationsEnabled;
            if (curr.CustomProcessNames != null && curr.CustomProcessNames.Count > 0)
            {
                txtCustomProcs.Text = string.Join(Environment.NewLine, curr.CustomProcessNames.ToArray());
            }
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
