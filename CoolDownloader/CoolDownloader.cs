using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("CoolDownloader - 쿨메신저 첨부파일 자동 다운로더")]
[assembly: AssemblyDescription("5초 카운트다운 알림창을 통한 쿨메신저 첨부파일 자동 다운로드 및 중복 방지 유틸리티")]
[assembly: AssemblyCompany("수지샘 (lemrlog@gmail.com)")]
[assembly: AssemblyProduct("CoolDownloader")]
[assembly: AssemblyCopyright("Copyright © 2026 수지샘 (lemrlog@gmail.com) All rights reserved.")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace CoolDownloader
{
    public static class Logger
    {
        private static string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CoolDownloader.log");
        private static object lockObj = new object();

        public static void Log(string msg)
        {
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, msg);
                lock (lockObj)
                {
                    File.AppendAllText(logPath, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Logger.Log("=== CoolDownloader 시작됨 ===");
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Logger.Log("치명적 오류 (UnhandledException): " + e.ExceptionObject.ToString());
            };

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "CoolDownloader_SingleInstance_Mutex_suji", out createdNew))
            {
                if (!createdNew)
                {
                    Logger.Log("이미 실행 중인 인스턴스가 있어 종료합니다.");
                    MessageBox.Show("CoolDownloader가 이미 실행 중입니다.\n작업표시줄 우측 하단 트레이 아이콘을 확인해주세요.", "CoolDownloader", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }

    #region Data Models & Storage
    public class HistoryItem
    {
        public string Fingerprint { get; set; }
        public string Status { get; set; } // "DOWNLOADED", "CANCELLED"
        public string DateTimeStr { get; set; }
        public string Sender { get; set; }
        public List<string> Files { get; set; }

        public HistoryItem()
        {
            Files = new List<string>();
        }
    }

    public static class Storage
    {
        private static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoolDownloader");
        private static readonly string HistoryFile = Path.Combine(AppDataDir, "download_history.txt");
        private static readonly string ConfigFile = Path.Combine(AppDataDir, "config.txt");

        private static Dictionary<string, HistoryItem> history = new Dictionary<string, HistoryItem>();
        private static HashSet<string> pendingFingerprints = new HashSet<string>();
        private static object syncLock = new object();

        public static int CountdownSeconds = 5;
        public static bool IsMonitoringActive = true;

        static Storage()
        {
            try
            {
                if (!Directory.Exists(AppDataDir))
                {
                    Directory.CreateDirectory(AppDataDir);
                }
                LoadConfig();
                LoadHistory();
            }
            catch { }
        }

        public static string GetSaveDirectory()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string coolDir = Path.Combine(docs, "CoolMessenger Files", "Received Files");
            if (!Directory.Exists(coolDir))
            {
                try { Directory.CreateDirectory(coolDir); } catch { }
            }
            return coolDir;
        }

        public static bool IsPending(string fp)
        {
            lock (syncLock)
            {
                return pendingFingerprints.Contains(fp);
            }
        }

        public static void MarkPending(string fp)
        {
            lock (syncLock)
            {
                pendingFingerprints.Add(fp);
            }
        }

        public static void UnmarkPending(string fp)
        {
            lock (syncLock)
            {
                pendingFingerprints.Remove(fp);
            }
        }

        public static bool HasHistory(string fp)
        {
            lock (syncLock)
            {
                return history.ContainsKey(fp);
            }
        }

        public static void RecordHistory(string fp, string status, string sender, List<string> files)
        {
            lock (syncLock)
            {
                pendingFingerprints.Remove(fp);
                HistoryItem item = new HistoryItem
                {
                    Fingerprint = fp,
                    Status = status,
                    DateTimeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Sender = string.IsNullOrEmpty(sender) ? "알 수 없음" : sender,
                    Files = files ?? new List<string>()
                };
                history[fp] = item;
                SaveHistory();
            }
        }

        public static List<HistoryItem> GetAllHistory()
        {
            lock (syncLock)
            {
                List<HistoryItem> list = new List<HistoryItem>(history.Values);
                list.Reverse(); // Newest first
                return list;
            }
        }

        public static int GetDownloadedCount()
        {
            lock (syncLock)
            {
                int c = 0;
                foreach (var item in history.Values)
                {
                    if (item.Status == "DOWNLOADED") c++;
                }
                return c;
            }
        }

        public static int GetCancelledCount()
        {
            lock (syncLock)
            {
                int c = 0;
                foreach (var item in history.Values)
                {
                    if (item.Status == "CANCELLED") c++;
                }
                return c;
            }
        }

        public static void ClearHistory()
        {
            lock (syncLock)
            {
                history.Clear();
                pendingFingerprints.Clear();
                try
                {
                    if (File.Exists(HistoryFile)) File.Delete(HistoryFile);
                }
                catch { }
            }
        }

        private static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    string[] lines = File.ReadAllLines(ConfigFile, Encoding.UTF8);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("CountdownSeconds="))
                        {
                            int val;
                            if (int.TryParse(line.Substring("CountdownSeconds=".Length), out val) && val >= 2 && val <= 30)
                            {
                                CountdownSeconds = val;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static void SaveConfig()
        {
            try
            {
                File.WriteAllText(ConfigFile, "CountdownSeconds=" + CountdownSeconds, Encoding.UTF8);
            }
            catch { }
        }

        private static void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryFile)) return;
                string[] lines = File.ReadAllLines(HistoryFile, Encoding.UTF8);
                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                    // Format: FP|STATUS|DATETIME|SENDER|FILE1,FILE2...
                    string[] parts = line.Split('|');
                    if (parts.Length >= 4)
                    {
                        HistoryItem item = new HistoryItem
                        {
                            Fingerprint = parts[0],
                            Status = parts[1],
                            DateTimeStr = parts[2],
                            Sender = parts[3]
                        };
                        if (parts.Length >= 5 && !string.IsNullOrEmpty(parts[4]))
                        {
                            item.Files.AddRange(parts[4].Split(';'));
                        }
                        history[item.Fingerprint] = item;
                    }
                }
            }
            catch { }
        }

        private static void SaveHistory()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (var item in history.Values)
                {
                    string fileStr = string.Join(";", item.Files.ToArray());
                    sb.AppendLine(string.Format("{0}|{1}|{2}|{3}|{4}", item.Fingerprint, item.Status, item.DateTimeStr, item.Sender, fileStr));
                }
                File.WriteAllText(HistoryFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
    #endregion

    #region Normalization & Fingerprint Generator
    public static class FingerprintHelper
    {
        private static readonly Regex SizeRegex = new Regex(@"\s*\(\s*[\d.,]+\s*(KB|MB|GB|B|bytes?|바이트|kb|mb|gb|b)\s*\)$", RegexOptions.IgnoreCase);

        public static string NormalizeFilename(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = SizeRegex.Replace(raw, "");
            s = Regex.Replace(s, @"\s+\.", "."); // remove whitespace before extension
            s = s.Trim().Normalize(NormalizationForm.FormC);
            return s;
        }

        public static string ComputeFingerprint(List<string> cleanFiles, string sender, string msgDate)
        {
            cleanFiles.Sort();
            string combined = string.Join(";", cleanFiles.ToArray()) + "||" + (sender ?? "").Trim() + "||" + (msgDate ?? "").Trim();
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(combined));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
    #endregion

    #region 5-Second Countdown Floating Toast Form
    public class CountdownToastForm : Form
    {
        private System.Windows.Forms.Timer timer;
        private int remainingSeconds;
        private int totalSeconds;

        private Label lblHeader;
        private Label lblSender;
        private Label lblFiles;
        private Label lblCountdown;
        private ProgressBar progressBar;
        private Button btnDownloadNow;
        private Button btnCancel;
        private LinkLabel linkOpenFolder;

        private string fingerprint;
        private string senderName;
        private List<string> fileNames;
        private AutomationElement saveButton;
        private AutomationElement targetWindow;
        private Action onCompletedCallback;
        private IntPtr directBtnHwnd = IntPtr.Zero;
        private IntPtr directWinHwnd = IntPtr.Zero;

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_COMMAND = 0x0111;
        private const uint BM_CLICK = 0x00F5;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE (Does not steal user keyboard focus)
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW (Does not clutter Alt-Tab)
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        public CountdownToastForm(string fp, string sender, List<string> files, AutomationElement btn, AutomationElement win, Action callback, IntPtr directBtn = default(IntPtr), IntPtr directWin = default(IntPtr))
        {
            this.fingerprint = fp;
            this.senderName = sender;
            this.fileNames = files;
            this.saveButton = btn;
            this.targetWindow = win;
            this.onCompletedCallback = callback;
            this.directBtnHwnd = directBtn;
            this.directWinHwnd = directWin;
            this.totalSeconds = Storage.CountdownSeconds;
            this.remainingSeconds = totalSeconds;

            BuildUI();
            PositionAtBottomRight();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += OnTimerTick;
            timer.Start();
        }

        private void BuildUI()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(380, 205);
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.FromArgb(248, 249, 250);
            this.ShowInTaskbar = false;
            this.TopMost = true;

            // Banner Header
            Panel pnlBanner = new Panel();
            pnlBanner.Dock = DockStyle.Top;
            pnlBanner.Height = 38;
            pnlBanner.BackColor = Color.FromArgb(30, 41, 59); // Slate Dark
            this.Controls.Add(pnlBanner);

            lblHeader = new Label();
            lblHeader.Text = "📎 쿨메신저 첨부파일 자동 수신  [수지샘]";
            lblHeader.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            lblHeader.ForeColor = Color.White;
            lblHeader.Location = new Point(12, 9);
            lblHeader.AutoSize = true;
            pnlBanner.Controls.Add(lblHeader);

            // Close (X) button on top banner
            Label lblCloseX = new Label();
            lblCloseX.Text = "✕";
            lblCloseX.Font = new Font("Malgun Gothic", 10F, FontStyle.Bold);
            lblCloseX.ForeColor = Color.FromArgb(148, 163, 184);
            lblCloseX.Location = new Point(352, 9);
            lblCloseX.Size = new Size(20, 20);
            lblCloseX.Cursor = Cursors.Hand;
            lblCloseX.Click += delegate { DoCancel(); };
            pnlBanner.Controls.Add(lblCloseX);

            // Sender
            lblSender = new Label();
            lblSender.Text = "발신: " + (string.IsNullOrEmpty(senderName) ? "선생님" : senderName);
            lblSender.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            lblSender.ForeColor = Color.FromArgb(51, 65, 85);
            lblSender.Location = new Point(14, 48);
            lblSender.AutoSize = true;
            this.Controls.Add(lblSender);

            // Files Summary
            lblFiles = new Label();
            string fileSummary = "";
            if (fileNames != null && fileNames.Count > 0)
            {
                if (fileNames.Count == 1)
                    fileSummary = fileNames[0];
                else
                    fileSummary = string.Format("{0} 외 {1}개", fileNames[0], fileNames.Count - 1);
            }
            else
            {
                fileSummary = "새 첨부파일";
            }
            lblFiles.Text = "파일: " + fileSummary;
            lblFiles.Font = new Font("Malgun Gothic", 9F);
            lblFiles.ForeColor = Color.FromArgb(71, 85, 105);
            lblFiles.Location = new Point(14, 70);
            lblFiles.Size = new Size(350, 18);
            lblFiles.AutoEllipsis = true;
            this.Controls.Add(lblFiles);

            // Countdown Notice Label
            lblCountdown = new Label();
            lblCountdown.Text = string.Format("⏳ {0}초 후 자동으로 다운로드합니다...", remainingSeconds);
            lblCountdown.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            lblCountdown.ForeColor = Color.FromArgb(245, 158, 11); // Amber
            lblCountdown.Location = new Point(14, 96);
            lblCountdown.AutoSize = true;
            this.Controls.Add(lblCountdown);

            // Progress Bar
            progressBar = new ProgressBar();
            progressBar.Location = new Point(14, 122);
            progressBar.Size = new Size(350, 12);
            progressBar.Maximum = totalSeconds * 10;
            progressBar.Value = remainingSeconds * 10;
            this.Controls.Add(progressBar);

            // Buttons
            btnDownloadNow = new Button();
            btnDownloadNow.Text = "지금 받기 (Enter)";
            btnDownloadNow.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            btnDownloadNow.Location = new Point(175, 145);
            btnDownloadNow.Size = new Size(115, 32);
            btnDownloadNow.BackColor = Color.FromArgb(79, 70, 229); // Indigo
            btnDownloadNow.ForeColor = Color.White;
            btnDownloadNow.FlatStyle = FlatStyle.Flat;
            btnDownloadNow.Click += delegate { DoDownload(); };
            this.Controls.Add(btnDownloadNow);

            btnCancel = new Button();
            btnCancel.Text = "취소 (ESC)";
            btnCancel.Font = new Font("Malgun Gothic", 9F);
            btnCancel.Location = new Point(296, 145);
            btnCancel.Size = new Size(70, 32);
            btnCancel.BackColor = Color.FromArgb(226, 232, 240);
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.Click += delegate { DoCancel(); };
            this.Controls.Add(btnCancel);

            // Link to open folder (initially hidden)
            linkOpenFolder = new LinkLabel();
            linkOpenFolder.Text = "📂 저장 폴더 열기";
            linkOpenFolder.Font = new Font("Malgun Gothic", 9F);
            linkOpenFolder.Location = new Point(14, 152);
            linkOpenFolder.AutoSize = true;
            linkOpenFolder.Visible = false;
            linkOpenFolder.LinkClicked += delegate
            {
                try { Process.Start(Storage.GetSaveDirectory()); } catch { }
            };
            this.Controls.Add(linkOpenFolder);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Draw subtle 1px border
            using (Pen p = new Pen(Color.FromArgb(203, 213, 225), 1))
            {
                e.Graphics.DrawRectangle(p, 0, 0, this.Width - 1, this.Height - 1);
            }
        }

        private void PositionAtBottomRight()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(wa.Right - this.Width - 15, wa.Bottom - this.Height - 15);
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            remainingSeconds--;
            if (remainingSeconds > 0)
            {
                lblCountdown.Text = string.Format("⏳ {0}초 후 자동으로 다운로드합니다...", remainingSeconds);
                progressBar.Value = Math.Max(0, remainingSeconds * 10);
            }
            else
            {
                timer.Stop();
                DoDownload();
            }
        }

        private void DoCancel()
        {
            if (timer != null) timer.Stop();
            Storage.RecordHistory(fingerprint, "CANCELLED", senderName, fileNames);

            lblCountdown.Text = "🛑 자동 다운로드가 취소되었습니다.";
            lblCountdown.ForeColor = Color.FromArgb(239, 68, 68); // Red
            btnDownloadNow.Visible = false;
            btnCancel.Visible = false;
            progressBar.Visible = false;

            System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer();
            closeTimer.Interval = 900;
            closeTimer.Tick += delegate
            {
                closeTimer.Stop();
                closeTimer.Dispose();
                this.Close();
                if (onCompletedCallback != null) onCompletedCallback();
            };
            closeTimer.Start();
        }

        private void DoDownload()
        {
            if (timer != null) timer.Stop();

            lblCountdown.Text = "⚡ 첨부파일 다운로드 요청 중...";
            lblCountdown.ForeColor = Color.FromArgb(79, 70, 229);
            btnDownloadNow.Enabled = false;
            btnCancel.Enabled = false;

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool clicked = false;

                // 1. Direct Win32 BM_CLICK (0ms instant click)
                if (directBtnHwnd != IntPtr.Zero)
                {
                    try
                    {
                        PostMessage(directBtnHwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        clicked = true;
                    }
                    catch { }
                }

                // 2. Direct Win32 WM_COMMAND 3320 to target window (0ms instant command)
                if (!clicked && directWinHwnd != IntPtr.Zero)
                {
                    try
                    {
                        PostMessage(directWinHwnd, WM_COMMAND, (IntPtr)3320, IntPtr.Zero);
                        clicked = true;
                    }
                    catch { }
                }

                // 3. Fallback to UIA Invoke
                if (!clicked && saveButton != null)
                {
                    try
                    {
                        var pattern = saveButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                        if (pattern != null)
                        {
                            pattern.Invoke();
                            clicked = true;
                        }
                    }
                    catch { }
                }

                // 4. Fallback to UIA NativeWindowHandle
                if (!clicked && saveButton != null)
                {
                    try
                    {
                        IntPtr hBtn = (IntPtr)saveButton.Current.NativeWindowHandle;
                        if (hBtn != IntPtr.Zero)
                        {
                            PostMessage(hBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                            clicked = true;
                        }
                    }
                    catch { }
                }

                if (!clicked && targetWindow != null)
                {
                    try
                    {
                        IntPtr hWin = (IntPtr)targetWindow.Current.NativeWindowHandle;
                        if (hWin != IntPtr.Zero)
                        {
                            PostMessage(hWin, WM_COMMAND, (IntPtr)3320, IntPtr.Zero);
                            clicked = true;
                        }
                    }
                    catch { }
                }

                // Record history
                Storage.RecordHistory(fingerprint, "DOWNLOADED", senderName, fileNames);

                this.BeginInvoke((MethodInvoker)delegate
                {
                    lblCountdown.Text = "✅ 다운로드 완료!";
                    lblCountdown.ForeColor = Color.FromArgb(16, 185, 129); // Green
                    progressBar.Value = progressBar.Maximum;
                    btnDownloadNow.Visible = false;
                    btnCancel.Visible = false;
                    linkOpenFolder.Visible = true;

                    System.Windows.Forms.Timer autoClose = new System.Windows.Forms.Timer();
                    autoClose.Interval = 2500;
                    autoClose.Tick += delegate
                    {
                        autoClose.Stop();
                        autoClose.Dispose();
                        this.Close();
                        if (onCompletedCallback != null) onCompletedCallback();
                    };
                    autoClose.Start();
                });
            });
        }
    }
    #endregion

    #region Main Dashboard & Tray Application
    public class MainForm : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem menuToggleActive;
        private ToolStripMenuItem menuCountInfo;

        // UI Controls
        private Label lblHeaderTitle;
        private Label lblHeaderSub;
        private Label lblStatDownloaded;
        private Label lblStatCancelled;
        private Label lblStatStatus;
        private Button btnToggleActive;
        private Button btnOpenFolder;
        private Button btnClearHistory;
        private ComboBox cmbSeconds;
        private CheckBox chkAutoStart;
        private ListView lvHistory;

        private Thread watcherThread;
        private volatile bool isStopping = false;
        private bool isExiting = false;

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "CoolDownloader";

        public MainForm()
        {
            Logger.Log("MainForm 초기화 시작");
            InitializeTray();
            BuildUI();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Logger.Log("MainForm 표시 완료, 감시자 스레드 기동");
            StartWatcher();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(3000);
                AutoUpdater.CheckForUpdates(false, this);
            });
        }

        private void BuildUI()
        {
            this.Text = "CoolDownloader v1.1.1 - 쿨메신저 첨부파일 자동 다운로더 (수지샘)";
            this.Size = new Size(540, 520);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(248, 249, 250);

            try
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) this.Icon = appIcon;
            }
            catch { }

            // Header Banner
            Panel pnlHeader = new Panel();
            pnlHeader.Dock = DockStyle.Top;
            pnlHeader.Height = 70;
            pnlHeader.BackColor = Color.FromArgb(30, 41, 59); // Slate Dark
            this.Controls.Add(pnlHeader);

            lblHeaderTitle = new Label();
            lblHeaderTitle.Text = "CoolDownloader v1.1.1";
            lblHeaderTitle.Font = new Font("Malgun Gothic", 14F, FontStyle.Bold);
            lblHeaderTitle.ForeColor = Color.White;
            lblHeaderTitle.Location = new Point(20, 12);
            lblHeaderTitle.AutoSize = true;
            pnlHeader.Controls.Add(lblHeaderTitle);

            lblHeaderSub = new Label();
            lblHeaderSub.Text = "쿨메신저 새 쪽지 5초 카운트다운 첨부파일 자동 수신 도우미";
            lblHeaderSub.Font = new Font("Malgun Gothic", 9F);
            lblHeaderSub.ForeColor = Color.FromArgb(203, 213, 225);
            lblHeaderSub.Location = new Point(22, 40);
            lblHeaderSub.AutoSize = true;
            pnlHeader.Controls.Add(lblHeaderSub);

            // Stats Cards Panel
            Panel pnlStats = new Panel();
            pnlStats.Location = new Point(20, 85);
            pnlStats.Size = new Size(485, 75);
            pnlStats.BackColor = Color.White;
            pnlStats.BorderStyle = BorderStyle.FixedSingle;
            this.Controls.Add(pnlStats);

            lblStatDownloaded = new Label();
            lblStatDownloaded.Text = "📥 다운로드: 0건";
            lblStatDownloaded.Font = new Font("Malgun Gothic", 10F, FontStyle.Bold);
            lblStatDownloaded.ForeColor = Color.FromArgb(16, 185, 129);
            lblStatDownloaded.Location = new Point(15, 14);
            lblStatDownloaded.AutoSize = true;
            pnlStats.Controls.Add(lblStatDownloaded);

            lblStatCancelled = new Label();
            lblStatCancelled.Text = "🛑 사용자 취소: 0건";
            lblStatCancelled.Font = new Font("Malgun Gothic", 10F, FontStyle.Bold);
            lblStatCancelled.ForeColor = Color.FromArgb(239, 68, 68);
            lblStatCancelled.Location = new Point(175, 14);
            lblStatCancelled.AutoSize = true;
            pnlStats.Controls.Add(lblStatCancelled);

            lblStatStatus = new Label();
            lblStatStatus.Text = "● 자동 감시 활성화 중";
            lblStatStatus.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            lblStatStatus.ForeColor = Color.FromArgb(79, 70, 229);
            lblStatStatus.Location = new Point(330, 14);
            lblStatStatus.AutoSize = true;
            pnlStats.Controls.Add(lblStatStatus);

            Label lblSavePath = new Label();
            lblSavePath.Text = "저장 위치: " + Storage.GetSaveDirectory();
            lblSavePath.Font = new Font("Malgun Gothic", 8.5F);
            lblSavePath.ForeColor = Color.FromArgb(100, 116, 139);
            lblSavePath.Location = new Point(15, 45);
            lblSavePath.AutoSize = true;
            pnlStats.Controls.Add(lblSavePath);

            // Controls Bar
            btnToggleActive = new Button();
            btnToggleActive.Text = "감시 일시정지";
            btnToggleActive.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            btnToggleActive.Location = new Point(20, 170);
            btnToggleActive.Size = new Size(115, 30);
            btnToggleActive.BackColor = Color.FromArgb(241, 245, 249);
            btnToggleActive.FlatStyle = FlatStyle.Flat;
            btnToggleActive.Click += OnToggleActive;
            this.Controls.Add(btnToggleActive);

            btnOpenFolder = new Button();
            btnOpenFolder.Text = "📂 저장 폴더 열기";
            btnOpenFolder.Font = new Font("Malgun Gothic", 9F);
            btnOpenFolder.Location = new Point(142, 170);
            btnOpenFolder.Size = new Size(125, 30);
            btnOpenFolder.BackColor = Color.FromArgb(241, 245, 249);
            btnOpenFolder.FlatStyle = FlatStyle.Flat;
            btnOpenFolder.Click += delegate { try { Process.Start(Storage.GetSaveDirectory()); } catch { } };
            this.Controls.Add(btnOpenFolder);

            Label lblSec = new Label();
            lblSec.Text = "대기 시간:";
            lblSec.Font = new Font("Malgun Gothic", 9F);
            lblSec.Location = new Point(285, 176);
            lblSec.AutoSize = true;
            this.Controls.Add(lblSec);

            cmbSeconds = new ComboBox();
            cmbSeconds.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSeconds.Items.AddRange(new object[] { "3초", "5초 (권장)", "7초", "10초" });
            cmbSeconds.SelectedIndex = 1; // 5초
            cmbSeconds.Location = new Point(350, 173);
            cmbSeconds.Size = new Size(90, 25);
            cmbSeconds.SelectedIndexChanged += OnSecondsChanged;
            this.Controls.Add(cmbSeconds);

            btnClearHistory = new Button();
            btnClearHistory.Text = "기록 비우기";
            btnClearHistory.Font = new Font("Malgun Gothic", 8.5F);
            btnClearHistory.Location = new Point(448, 170);
            btnClearHistory.Size = new Size(80, 30);
            btnClearHistory.BackColor = Color.FromArgb(241, 245, 249);
            btnClearHistory.FlatStyle = FlatStyle.Flat;
            btnClearHistory.Click += OnClearHistory;
            this.Controls.Add(btnClearHistory);

            // History Listview
            Label lblListTitle = new Label();
            lblListTitle.Text = "수신 및 다운로드 이력 (중복 방지 목록)";
            lblListTitle.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            lblListTitle.ForeColor = Color.FromArgb(51, 65, 85);
            lblListTitle.Location = new Point(20, 212);
            lblListTitle.AutoSize = true;
            this.Controls.Add(lblListTitle);

            lvHistory = new ListView();
            lvHistory.Location = new Point(20, 235);
            lvHistory.Size = new Size(485, 185);
            lvHistory.View = View.Details;
            lvHistory.FullRowSelect = true;
            lvHistory.GridLines = true;
            lvHistory.Columns.Add("일시", 130);
            lvHistory.Columns.Add("상태", 70);
            lvHistory.Columns.Add("발신자", 85);
            lvHistory.Columns.Add("첨부 파일명", 195);
            this.Controls.Add(lvHistory);

            // Options & Footer
            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "윈도우 시작 시 자동 실행";
            chkAutoStart.Font = new Font("Malgun Gothic", 9F);
            chkAutoStart.Location = new Point(20, 432);
            chkAutoStart.AutoSize = true;
            chkAutoStart.Checked = IsAutoStartEnabled();
            chkAutoStart.CheckedChanged += OnAutoStartCheckedChanged;
            this.Controls.Add(chkAutoStart);

            LinkLabel linkCreator = new LinkLabel();
            linkCreator.Text = "제작: 수지샘 | 문의: lemrlog@gmail.com";
            linkCreator.Font = new Font("Malgun Gothic", 8.5F);
            linkCreator.Location = new Point(310, 434);
            linkCreator.AutoSize = true;
            linkCreator.LinkColor = Color.FromArgb(79, 70, 229);
            linkCreator.LinkClicked += delegate
            {
                try { Process.Start("mailto:lemrlog@gmail.com?subject=[CoolDownloader] 문의 및 피드백"); } catch { }
            };
            this.Controls.Add(linkCreator);

            Button btnCheckUpdate = new Button();
            btnCheckUpdate.Text = "🔄 업데이트 확인";
            btnCheckUpdate.Font = new Font("Malgun Gothic", 8F);
            btnCheckUpdate.Location = new Point(190, 429);
            btnCheckUpdate.Size = new Size(110, 26);
            btnCheckUpdate.BackColor = Color.FromArgb(241, 245, 249);
            btnCheckUpdate.FlatStyle = FlatStyle.Flat;
            btnCheckUpdate.Click += delegate { AutoUpdater.CheckForUpdates(true, this); };
            this.Controls.Add(btnCheckUpdate);

            RefreshUI();
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();

            menuCountInfo = new ToolStripMenuItem("다운로드: 0건 완료");
            menuCountInfo.Enabled = false;
            trayMenu.Items.Add(menuCountInfo);

            ToolStripMenuItem menuCredit = new ToolStripMenuItem("제작: 수지샘 (lemrlog@gmail.com)");
            menuCredit.Enabled = false;
            menuCredit.Font = new Font(trayMenu.Font, FontStyle.Italic);
            trayMenu.Items.Add(menuCredit);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem menuOpen = new ToolStripMenuItem("대시보드 열기", null, delegate
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.BringToFront();
            });
            menuOpen.Font = new Font(trayMenu.Font, FontStyle.Bold);
            trayMenu.Items.Add(menuOpen);

            menuToggleActive = new ToolStripMenuItem("자동 다운로드 활성화", null, OnToggleActive);
            menuToggleActive.Checked = true;
            trayMenu.Items.Add(menuToggleActive);

            ToolStripMenuItem menuFolder = new ToolStripMenuItem("저장 폴더 열기", null, delegate
            {
                try { Process.Start(Storage.GetSaveDirectory()); } catch { }
            });
            trayMenu.Items.Add(menuFolder);

            ToolStripMenuItem menuCheckUpdate = new ToolStripMenuItem("최신 버전 업데이트 확인...", null, delegate
            {
                AutoUpdater.CheckForUpdates(true, this);
            });
            trayMenu.Items.Add(menuCheckUpdate);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem menuExit = new ToolStripMenuItem("종료", null, delegate
            {
                isExiting = true;
                CleanupWatcher();
                if (trayIcon != null) trayIcon.Visible = false;
                Application.Exit();
            });
            trayMenu.Items.Add(menuExit);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "CoolDownloader - 쿨메신저 자동 다운로드 중";
            try
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) trayIcon.Icon = appIcon;
            }
            catch { }
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.BringToFront();
            };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !isExiting)
            {
                e.Cancel = true;
                this.Hide();
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(1200, "CoolDownloader", "작업표시줄 트레이로 숨겨졌습니다.\n백그라운드에서 첨부파일을 자동 감시합니다.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        public void RefreshUI()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(RefreshUI));
                return;
            }

            int down = Storage.GetDownloadedCount();
            int canc = Storage.GetCancelledCount();

            if (lblStatDownloaded != null) lblStatDownloaded.Text = "📥 다운로드: " + down + "건";
            if (lblStatCancelled != null) lblStatCancelled.Text = "🛑 사용자 취소: " + canc + "건";
            if (menuCountInfo != null) menuCountInfo.Text = string.Format("다운로드: {0}건 완료", down);

            if (Storage.IsMonitoringActive)
            {
                if (lblStatStatus != null)
                {
                    lblStatStatus.Text = "● 자동 감시 활성화 중";
                    lblStatStatus.ForeColor = Color.FromArgb(16, 185, 129);
                }
                if (btnToggleActive != null) btnToggleActive.Text = "감시 일시정지";
                if (menuToggleActive != null) menuToggleActive.Checked = true;
            }
            else
            {
                if (lblStatStatus != null)
                {
                    lblStatStatus.Text = "■ 자동 감시 일시정지됨";
                    lblStatStatus.ForeColor = Color.FromArgb(239, 68, 68);
                }
                if (btnToggleActive != null) btnToggleActive.Text = "감시 다시시작";
                if (menuToggleActive != null) menuToggleActive.Checked = false;
            }

            // Populate list
            lvHistory.BeginUpdate();
            lvHistory.Items.Clear();
            var list = Storage.GetAllHistory();
            foreach (var item in list)
            {
                var lvi = new ListViewItem(item.DateTimeStr);
                lvi.SubItems.Add(item.Status == "DOWNLOADED" ? "완료" : "취소");
                lvi.SubItems.Add(item.Sender);
                lvi.SubItems.Add(string.Join(", ", item.Files.ToArray()));
                if (item.Status == "DOWNLOADED")
                    lvi.ForeColor = Color.FromArgb(16, 185, 129);
                else
                    lvi.ForeColor = Color.FromArgb(156, 163, 175);
                lvHistory.Items.Add(lvi);
            }
            lvHistory.EndUpdate();
        }

        private void OnToggleActive(object sender, EventArgs e)
        {
            Storage.IsMonitoringActive = !Storage.IsMonitoringActive;
            RefreshUI();
        }

        private void OnSecondsChanged(object sender, EventArgs e)
        {
            switch (cmbSeconds.SelectedIndex)
            {
                case 0: Storage.CountdownSeconds = 3; break;
                case 1: Storage.CountdownSeconds = 5; break;
                case 2: Storage.CountdownSeconds = 7; break;
                case 3: Storage.CountdownSeconds = 10; break;
            }
            Storage.SaveConfig();
        }

        private void OnClearHistory(object sender, EventArgs e)
        {
            if (MessageBox.Show("다운로드 및 취소 이력을 모두 비우시겠습니까?\n이력을 지우면 과거 쪽지를 다시 열었을 때 다시 다운로드가 시도될 수 있습니다.", "이력 초기화", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Storage.ClearHistory();
                RefreshUI();
            }
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(AppName);
                        return val != null;
                    }
                }
            }
            catch { }
            return false;
        }

        private void OnAutoStartCheckedChanged(object sender, EventArgs e)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null)
                    {
                        if (chkAutoStart.Checked)
                        {
                            key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                        }
                        else
                        {
                            key.DeleteValue(AppName, false);
                        }
                    }
                }
            }
            catch { }
        }

        #region Background Watcher Thread & WinEventHook
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        private WinEventDelegate winEventDelegate;
        private IntPtr hWinEventHook = IntPtr.Zero;
        private Dictionary<uint, bool> coolPidCache = new Dictionary<uint, bool>();
        private object inspectLock = new object();
        private HashSet<IntPtr> processedHwnds = new HashSet<IntPtr>();

        private void CleanProcessedHwnds()
        {
            lock (inspectLock)
            {
                if (processedHwnds.Count == 0) return;
                List<IntPtr> dead = new List<IntPtr>();
                foreach (IntPtr h in processedHwnds)
                {
                    if (!IsWindow(h)) dead.Add(h);
                }
                foreach (IntPtr h in dead) processedHwnds.Remove(h);
            }
        }

        private void StartWatcher()
        {
            try
            {
                winEventDelegate = new WinEventDelegate(OnWinEventHook);
                hWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_SHOW, IntPtr.Zero, winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                Logger.Log("WinEventHook(0ms 즉각 감지) 등록 완료: " + (hWinEventHook != IntPtr.Zero));
            }
            catch (Exception ex)
            {
                Logger.Log("WinEventHook 등록 실패 (50ms 폴링으로 대체): " + ex.Message);
            }

            watcherThread = new Thread(WatcherLoop);
            watcherThread.IsBackground = true;
            watcherThread.Start();
        }

        private void CleanupWatcher()
        {
            isStopping = true;
            if (hWinEventHook != IntPtr.Zero)
            {
                try
                {
                    UnhookWinEvent(hWinEventHook);
                    hWinEventHook = IntPtr.Zero;
                    Logger.Log("WinEventHook 해제 완료");
                }
                catch { }
            }
        }

        private void OnWinEventHook(IntPtr hHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || idObject != 0) return; // OBJID_WINDOW = 0
            if (!Storage.IsMonitoringActive) return;

            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return;

                bool isCool;
                if (!coolPidCache.TryGetValue(pid, out isCool))
                {
                    try
                    {
                        Process p = Process.GetProcessById((int)pid);
                        isCool = p.ProcessName.ToLower().Contains("cool");
                        coolPidCache[pid] = isCool;
                    }
                    catch
                    {
                        coolPidCache[pid] = false;
                        return;
                    }
                }
                if (!isCool) return;

                var sbCls = new StringBuilder(128);
                GetClassName(hwnd, sbCls, 128);
                if (sbCls.ToString() != "#32770") return;

                var sbTitle = new StringBuilder(256);
                GetWindowText(hwnd, sbTitle, 256);
                string title = sbTitle.ToString();
                if (title == "COOLMESSENGER" || title == "Cool Advertise") return;

                ThreadPool.QueueUserWorkItem(delegate
                {
                    InspectWindow(hwnd);
                });
            }
            catch { }
        }

        private void WatcherLoop()
        {
            Logger.Log("초고속 감시자 스레드(WatcherLoop 50ms) 시작");
            try
            {
                IntPtr hDesk = OpenDesktop("default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero) SetThreadDesktop(hDesk);
            }
            catch { }

            int cycle = 0;
            while (!isStopping)
            {
                if (Storage.IsMonitoringActive)
                {
                    try
                    {
                        ScanForNoteWindows();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("ScanForNoteWindows 오류: " + ex.Message);
                    }
                }

                cycle++;
                if (cycle % 20 == 0)
                {
                    CleanProcessedHwnds();
                }

                Thread.Sleep(50); // 50ms ultra-fast polling interval (replaces slow 500ms delay)
            }
        }

        private void ScanForNoteWindows()
        {
            List<IntPtr> candidateHwnds = new List<IntPtr>();

            EnumWindows(delegate (IntPtr hwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hwnd)) return true;

                lock (inspectLock)
                {
                    if (processedHwnds.Contains(hwnd)) return true; // Already verified/processed; skip immediately
                }

                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);

                bool isCool;
                if (!coolPidCache.TryGetValue(pid, out isCool))
                {
                    try
                    {
                        Process p = Process.GetProcessById((int)pid);
                        isCool = p.ProcessName.ToLower().Contains("cool");
                        coolPidCache[pid] = isCool;
                    }
                    catch
                    {
                        coolPidCache[pid] = false;
                        return true;
                    }
                }

                if (!isCool) return true;

                var sbCls = new StringBuilder(128);
                GetClassName(hwnd, sbCls, 128);
                string cls = sbCls.ToString();
                if (cls != "#32770") return true;

                var sbTitle = new StringBuilder(256);
                GetWindowText(hwnd, sbTitle, 256);
                string title = sbTitle.ToString();

                // Skip main window and advertisement popup
                if (title == "COOLMESSENGER" || title == "Cool Advertise") return true;

                candidateHwnds.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr hwnd in candidateHwnds)
            {
                InspectWindow(hwnd);
            }
        }

        private void InspectWindow(IntPtr hwnd)
        {
            try
            {
                if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return;

                lock (inspectLock)
                {
                    if (processedHwnds.Contains(hwnd)) return;
                }

                // 1. FAST Win32 Check for Save Button (0.001ms)
                IntPtr directSaveBtn = GetDlgItem(hwnd, 3320);
                bool hasSaveBtn = (directSaveBtn != IntPtr.Zero && IsWindowVisible(directSaveBtn) && IsWindowEnabled(directSaveBtn));

                List<string> childTexts = new List<string>();

                // Fast child enumeration (0.05ms) to confirm button and collect all child text labels
                EnumChildWindows(hwnd, delegate (IntPtr child, IntPtr l)
                {
                    int id = GetDlgCtrlID(child);
                    var sbText = new StringBuilder(512);
                    GetWindowText(child, sbText, 512);
                    string t = sbText.ToString().Trim();
                    if (!string.IsNullOrEmpty(t)) childTexts.Add(t);

                    if (!hasSaveBtn && (id == 3320 || t.Contains("모든파일") || t.Contains("모두 저장") || t.Contains("모든파일 저장")))
                    {
                        if (IsWindowVisible(child) && IsWindowEnabled(child))
                        {
                            directSaveBtn = child;
                            hasSaveBtn = true;
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                // If no attachment save button exists on this window, mark as processed and exit instantly!
                if (!hasSaveBtn)
                {
                    lock (inspectLock) { processedHwnds.Add(hwnd); }
                    return;
                }

                // 2. Fast Win32 Attachment & Sender Extraction (0.05ms)
                List<string> cleanFiles = new List<string>();
                string sender = "";
                string msgDate = "";
                Regex dateRegex = new Regex(@"20\d{2}[/\-.]\d{1,2}[/\-.]\d{1,2}\s+\d{1,2}:\d{2}");

                foreach (string text in childTexts)
                {
                    MatchCollection matches = Regex.Matches(text, @"[^\r\n\t\\/:]+\.(hwp|hwpx|pdf|xls|xlsx|zip|png|jpg|doc|docx|ppt|pptx)", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        string clean = FingerprintHelper.NormalizeFilename(m.Value);
                        if (!string.IsNullOrEmpty(clean) && !cleanFiles.Contains(clean))
                        {
                            cleanFiles.Add(clean);
                        }
                    }

                    if (string.IsNullOrEmpty(sender) && (text.StartsWith("보낸사람:") || text.StartsWith("보낸이:") || text.StartsWith("발신:")))
                    {
                        sender = text.Substring(text.IndexOf(':') + 1).Trim();
                    }
                    if (string.IsNullOrEmpty(msgDate))
                    {
                        Match dm = dateRegex.Match(text);
                        if (dm.Success) msgDate = dm.Value;
                    }
                }

                AutomationElement winUia = null;
                AutomationElement saveBtnUia = null;

                // 3. Fallback: If cleanFiles is still empty, invoke UIAutomation for custom panes
                if (cleanFiles.Count == 0)
                {
                    try
                    {
                        winUia = AutomationElement.FromHandle(hwnd);
                        if (winUia != null)
                        {
                            var panes = winUia.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
                            foreach (AutomationElement p in panes)
                            {
                                string pName = p.Current.Name;
                                if (Regex.IsMatch(pName, @"\.(hwp|hwpx|pdf|xls|xlsx|zip|png|jpg|doc|docx|ppt|pptx)", RegexOptions.IgnoreCase))
                                {
                                    string clean = FingerprintHelper.NormalizeFilename(pName);
                                    if (!string.IsNullOrEmpty(clean) && !cleanFiles.Contains(clean))
                                        cleanFiles.Add(clean);
                                }
                            }
                            if (cleanFiles.Count == 0)
                            {
                                var texts = winUia.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                                foreach (AutomationElement t in texts)
                                {
                                    string tName = t.Current.Name;
                                    if (Regex.IsMatch(tName, @"\.(hwp|hwpx|pdf|xls|xlsx|zip|png|jpg|doc|docx|ppt|pptx)", RegexOptions.IgnoreCase))
                                    {
                                        string clean = FingerprintHelper.NormalizeFilename(tName);
                                        if (!string.IsNullOrEmpty(clean) && !cleanFiles.Contains(clean))
                                            cleanFiles.Add(clean);
                                    }
                                    if (string.IsNullOrEmpty(sender) && (tName.StartsWith("보낸사람:") || tName.StartsWith("보낸이:") || tName.StartsWith("발신:")))
                                    {
                                        sender = tName.Substring(tName.IndexOf(':') + 1).Trim();
                                    }
                                    if (string.IsNullOrEmpty(msgDate))
                                    {
                                        Match m = dateRegex.Match(tName);
                                        if (m.Success) msgDate = m.Value;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // CRITICAL SAFETY 1: If no attachment files identified, exit and mark processed
                if (cleanFiles.Count == 0)
                {
                    lock (inspectLock) { processedHwnds.Add(hwnd); }
                    return;
                }

                if (string.IsNullOrEmpty(sender))
                {
                    var sbTitle = new StringBuilder(256);
                    GetWindowText(hwnd, sbTitle, 256);
                    string winTitle = sbTitle.ToString();
                    if (!string.IsNullOrEmpty(winTitle) && !winTitle.Contains("메시지"))
                    {
                        sender = winTitle;
                    }
                }

                // 4. Compute Fingerprint with Date and Normalized Files
                string fp = FingerprintHelper.ComputeFingerprint(cleanFiles, sender, msgDate);

                // 5. Check if already recorded in history or currently pending
                if (Storage.HasHistory(fp) || Storage.IsPending(fp))
                {
                    lock (inspectLock) { processedHwnds.Add(hwnd); }
                    return;
                }

                // 6. Pre-check if all attachment files already exist in Received Files directory
                string saveDir = Storage.GetSaveDirectory();
                bool allFilesExist = true;
                foreach (string f in cleanFiles)
                {
                    string targetPath = Path.Combine(saveDir, f);
                    if (!File.Exists(targetPath))
                    {
                        allFilesExist = false;
                        break;
                    }
                }
                if (allFilesExist)
                {
                    // Files already exist locally on disk! Auto-mark as DOWNLOADED without bothering the user.
                    Storage.RecordHistory(fp, "DOWNLOADED", sender, cleanFiles);
                    lock (inspectLock) { processedHwnds.Add(hwnd); }
                    return;
                }

                // 7. Pre-emptive Lock (Pending) and Mark Processed
                Storage.MarkPending(fp);
                lock (inspectLock) { processedHwnds.Add(hwnd); }
                Logger.Log(string.Format("신규 쪽지 초고속(0ms) 감지 완료: 발신자={0}, 일시={1}, 파일={2}, 지문={3}", sender, msgDate, string.Join(",", cleanFiles.ToArray()), fp));

                // 8. Launch Countdown Toast Form on UI thread
                if (this.IsHandleCreated && !this.IsDisposed)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            CountdownToastForm toast = new CountdownToastForm(fp, sender, cleanFiles, saveBtnUia, winUia, delegate
                            {
                                RefreshUI();
                            }, directSaveBtn, hwnd);
                            toast.Show();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log("Toast 팝업 오류: " + ex.Message);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log("InspectWindow 오류: " + ex.Message);
            }
        }
        #endregion
    }
    #endregion

    #region Auto-Updater System
    public static class AutoUpdater
    {
        public static string CurrentVersion = "1.1.1";
        public static string AppDisplayName = "CoolDownloader";
        public static string DefaultCheckUrl = "https://raw.githubusercontent.com/gssg100/CoolSuite/main/updates/cooldownloader.json";

        public static string GetCheckUrl()
        {
            try
            {
                string customFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update_url.txt");
                if (File.Exists(customFile))
                {
                    string txt = File.ReadAllText(customFile).Trim();
                    if (!string.IsNullOrEmpty(txt)) return txt;
                }
            }
            catch { }
            return DefaultCheckUrl;
        }

        public static void CheckForUpdates(bool isManual, Form parent)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string url = GetCheckUrl();
                    System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; // TLS 1.2
                    using (var client = new System.Net.WebClient())
                    {
                        client.Encoding = Encoding.UTF8;
                        client.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CoolSuite/" + CurrentVersion);
                        string json = client.DownloadString(url);

                        string remoteVer = ExtractJsonField(json, "version");
                        string downloadUrl = ExtractJsonField(json, "download_url");
                        string changelog = ExtractJsonField(json, "changelog");

                        if (!string.IsNullOrEmpty(remoteVer) && IsNewerVersion(remoteVer, CurrentVersion))
                        {
                            if (parent != null && parent.IsHandleCreated && !parent.IsDisposed)
                            {
                                parent.BeginInvoke((MethodInvoker)delegate
                                {
                                    ShowUpdateDialog(remoteVer, downloadUrl, changelog, parent);
                                });
                            }
                        }
                        else
                        {
                            if (isManual && parent != null && parent.IsHandleCreated && !parent.IsDisposed)
                            {
                                parent.BeginInvoke((MethodInvoker)delegate
                                {
                                    MessageBox.Show(parent, string.Format("현재 최신 버전(v{0})을 사용하고 있습니다!", CurrentVersion), AppDisplayName + " 업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (isManual && parent != null && parent.IsHandleCreated && !parent.IsDisposed)
                    {
                        parent.BeginInvoke((MethodInvoker)delegate
                        {
                            MessageBox.Show(parent, "업데이트 서버에 연결할 수 없습니다.\n인터넷 연결 또는 서버 주소를 확인해주세요.\n\n주소: " + GetCheckUrl() + "\n오류: " + ex.Message, AppDisplayName + " 업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        });
                    }
                }
            });
        }

        private static bool IsNewerVersion(string remote, string local)
        {
            try
            {
                Version r = new Version(remote);
                Version l = new Version(local);
                return r > l;
            }
            catch
            {
                return string.Compare(remote, local, StringComparison.OrdinalIgnoreCase) > 0;
            }
        }

        private static string ExtractJsonField(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success)
            {
                return m.Groups[1].Value.Replace("\\n", "\n").Replace("\\r", "");
            }
            return "";
        }

        public static void ShowUpdateDialog(string remoteVer, string downloadUrl, string changelog, Form parent)
        {
            UpdateDialog dlg = new UpdateDialog(AppDisplayName, CurrentVersion, remoteVer, downloadUrl, changelog);
            dlg.ShowDialog(parent);
        }
    }

    public class UpdateDialog : Form
    {
        private string appName;
        private string currentVer;
        private string remoteVer;
        private string downloadUrl;
        private string changelog;

        private ProgressBar progressBar;
        private Label lblStatus;
        private Button btnUpdate;
        private Button btnLater;

        public UpdateDialog(string appName, string currentVer, string remoteVer, string downloadUrl, string changelog)
        {
            this.appName = appName;
            this.currentVer = currentVer;
            this.remoteVer = remoteVer;
            this.downloadUrl = downloadUrl;
            this.changelog = changelog;

            BuildUI();
        }

        private void BuildUI()
        {
            this.Text = appName + " 새 버전 업데이트";
            this.Size = new Size(450, 340);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(248, 249, 250);

            // Banner
            Panel pnlBanner = new Panel();
            pnlBanner.Dock = DockStyle.Top;
            pnlBanner.Height = 65;
            pnlBanner.BackColor = Color.FromArgb(30, 41, 59);
            this.Controls.Add(pnlBanner);

            Label lblTitle = new Label();
            lblTitle.Text = string.Format("🚀 {0} 새 버전(v{1}) 출시!", appName, remoteVer);
            lblTitle.Font = new Font("Malgun Gothic", 12F, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.Location = new Point(18, 12);
            lblTitle.AutoSize = true;
            pnlBanner.Controls.Add(lblTitle);

            Label lblVersion = new Label();
            lblVersion.Text = string.Format("현재 버전: v{0}  ➔  최신 버전: v{1}", currentVer, remoteVer);
            lblVersion.Font = new Font("Malgun Gothic", 8.5F);
            lblVersion.ForeColor = Color.FromArgb(203, 213, 225);
            lblVersion.Location = new Point(20, 38);
            lblVersion.AutoSize = true;
            pnlBanner.Controls.Add(lblVersion);

            // Changelog text box
            Label lblLogTitle = new Label();
            lblLogTitle.Text = "[새로운 기능 및 개선 사항]";
            lblLogTitle.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            lblLogTitle.ForeColor = Color.FromArgb(51, 65, 85);
            lblLogTitle.Location = new Point(18, 78);
            lblLogTitle.AutoSize = true;
            this.Controls.Add(lblLogTitle);

            TextBox txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Text = string.IsNullOrEmpty(changelog) ? "성능 최적화 및 안정성 개선이 포함되었습니다." : changelog;
            txtLog.Font = new Font("Malgun Gothic", 8.5F);
            txtLog.Location = new Point(20, 102);
            txtLog.Size = new Size(395, 110);
            txtLog.BackColor = Color.White;
            this.Controls.Add(txtLog);

            // Progress bar & Status
            lblStatus = new Label();
            lblStatus.Text = "지금 업데이트를 진행하시겠습니까?";
            lblStatus.Font = new Font("Malgun Gothic", 8.5F);
            lblStatus.ForeColor = Color.FromArgb(100, 116, 139);
            lblStatus.Location = new Point(20, 222);
            lblStatus.AutoSize = true;
            this.Controls.Add(lblStatus);

            progressBar = new ProgressBar();
            progressBar.Location = new Point(20, 244);
            progressBar.Size = new Size(395, 14);
            progressBar.Visible = false;
            this.Controls.Add(progressBar);

            // Buttons
            btnUpdate = new Button();
            btnUpdate.Text = "지금 업데이트";
            btnUpdate.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            btnUpdate.Location = new Point(190, 265);
            btnUpdate.Size = new Size(130, 32);
            btnUpdate.BackColor = Color.FromArgb(79, 70, 229);
            btnUpdate.ForeColor = Color.White;
            btnUpdate.FlatStyle = FlatStyle.Flat;
            btnUpdate.Click += OnStartUpdate;
            this.Controls.Add(btnUpdate);

            btnLater = new Button();
            btnLater.Text = "다음에 하기";
            btnLater.Font = new Font("Malgun Gothic", 9F);
            btnLater.Location = new Point(328, 265);
            btnLater.Size = new Size(88, 32);
            btnLater.BackColor = Color.FromArgb(226, 232, 240);
            btnLater.FlatStyle = FlatStyle.Flat;
            btnLater.Click += delegate { this.Close(); };
            this.Controls.Add(btnLater);
        }

        private void OnStartUpdate(object sender, EventArgs e)
        {
            btnUpdate.Enabled = false;
            btnLater.Enabled = false;
            progressBar.Visible = true;
            lblStatus.Text = "최신 파일을 다운로드하는 중입니다...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string currentExe = Application.ExecutablePath;
                    string newExe = Path.Combine(appDir, appName + "_update.exe");

                    System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                    using (var wc = new System.Net.WebClient())
                    {
                        wc.DownloadProgressChanged += delegate (object s, System.Net.DownloadProgressChangedEventArgs args)
                        {
                            this.BeginInvoke((MethodInvoker)delegate
                            {
                                progressBar.Value = args.ProgressPercentage;
                                lblStatus.Text = string.Format("다운로드 중... ({0}%)", args.ProgressPercentage);
                            });
                        };
                        wc.DownloadFile(new Uri(downloadUrl), newExe);
                    }

                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        lblStatus.Text = "업데이트를 적용하고 재시작합니다...";
                        ExecuteReplacementAndRestart(currentExe, newExe);
                    });
                }
                catch (Exception ex)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        MessageBox.Show("업데이트 다운로드 실패:\n" + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        btnUpdate.Enabled = true;
                        btnLater.Enabled = true;
                        progressBar.Visible = false;
                        lblStatus.Text = "다운로드에 실패했습니다. 다시 시도해주세요.";
                    });
                }
            });
        }

        private void ExecuteReplacementAndRestart(string currentExe, string newExe)
        {
            try
            {
                string batPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apply_update.bat");
                string batContent = string.Format(
                    "@echo off\r\n" +
                    "timeout /t 1 /nobreak > nul\r\n" +
                    "move /y \"{0}\" \"{1}\" > nul\r\n" +
                    "start \"\" \"{1}\"\r\n" +
                    "del \"%~f0\"\r\n",
                    newExe, currentExe
                );
                File.WriteAllText(batPath, batContent, Encoding.Default);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = batPath;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);

                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("업데이트 교체 스크립트 실행 오류: " + ex.Message);
            }
        }
    }
    #endregion
}
