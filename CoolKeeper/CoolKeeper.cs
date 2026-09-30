using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;
using System.Diagnostics;
using System.Text.RegularExpressions;

[assembly: AssemblyTitle("CoolKeeper - 쿨메신저 상시 자리비움")]
[assembly: AssemblyDescription("교사를 위한 쿨메신저 수업·업무 집중모드 유틸리티")]
[assembly: AssemblyCompany("수지샘 (lemrlog@gmail.com)")]
[assembly: AssemblyProduct("CoolKeeper")]
[assembly: AssemblyCopyright("Copyright © 2026 수지샘 (lemrlog@gmail.com) All rights reserved.")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace CoolKeeper
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "CoolKeeper_SingleInstance_Mutex_fa02c1", out createdNew))
            {
                if (!createdNew)
                {
                    // If already running, notify user
                    MessageBox.Show("CoolKeeper가 이미 실행 중입니다.\n작업표시줄 우측 하단 트레이 아이콘 또는 실행 중인 창을 확인해주세요.", "CoolKeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }

    public class MainForm : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem menuActive;
        private ToolStripMenuItem menuAutoStart;
        private ToolStripMenuItem menuStatusInfo;

        // UI Controls
        private Label lblHeaderTitle;
        private Label lblStatusBadge;
        private Panel panelInfo;
        private Label lblMessengerState;
        private Label lblCurrentStatus;
        private Label lblCount;
        private Button btnToggle;
        private Button btnForceNow;
        private CheckBox chkAutoStart;
        private Button btnMinimizeToTray;

        private Thread workerThread;
        private volatile bool isStopping = false;
        private volatile bool isEnforcing = true;
        private bool isExiting = false;

        private int fixCount = 0;
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "CoolKeeper";

        // Win32 APIs
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint DESKTOP_ALL = 0x01FF;

        private string logFilePath;
        private string lastLoggedStatus = "";

        public MainForm()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            logFilePath = Path.Combine(appDir, "CoolKeeper.log");
            Log("=== CoolKeeper 시작됨 ===");

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log("치명적 오류 (Unhandled): " + e.ExceptionObject.ToString());
            };

            BuildUI();
            InitializeTray();
            StartWorker();
        }

        private void BuildUI()
        {
            this.Text = "CoolKeeper v1.1.0 - 쿨메신저 상시 자리비움 (수지샘)";
            this.Size = new Size(420, 395);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(248, 249, 250);
            this.Icon = GenerateAppIcon();

            // Header Icon PictureBox
            PictureBox picIcon = new PictureBox();
            picIcon.Location = new Point(20, 15);
            picIcon.Size = new Size(36, 36);
            picIcon.SizeMode = PictureBoxSizeMode.Zoom;
            try { picIcon.Image = GenerateAppIcon().ToBitmap(); } catch { }
            this.Controls.Add(picIcon);

            // Header Title
            lblHeaderTitle = new Label();
            lblHeaderTitle.Text = "CoolKeeper v1.1.0";
            lblHeaderTitle.Font = new Font("Malgun Gothic", 14F, FontStyle.Bold);
            lblHeaderTitle.Location = new Point(62, 13);
            lblHeaderTitle.AutoSize = true;
            lblHeaderTitle.ForeColor = Color.FromArgb(30, 41, 59);
            this.Controls.Add(lblHeaderTitle);

            // Subtitle
            Label lblSubTitle = new Label();
            lblSubTitle.Text = "교사를 위한 쿨메신저 수업·업무 집중모드";
            lblSubTitle.Font = new Font("Malgun Gothic", 8.5F);
            lblSubTitle.ForeColor = Color.FromArgb(100, 116, 139);
            lblSubTitle.Location = new Point(64, 38);
            lblSubTitle.AutoSize = true;
            this.Controls.Add(lblSubTitle);

            // Status Badge
            lblStatusBadge = new Label();
            lblStatusBadge.Text = "● 상시 유지 작동 중";
            lblStatusBadge.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            lblStatusBadge.Location = new Point(245, 18);
            lblStatusBadge.AutoSize = true;
            lblStatusBadge.ForeColor = Color.FromArgb(16, 185, 129); // Green
            this.Controls.Add(lblStatusBadge);

            // Info Panel
            panelInfo = new Panel();
            panelInfo.Location = new Point(20, 68);
            panelInfo.Size = new Size(365, 112);
            panelInfo.BackColor = Color.White;
            panelInfo.BorderStyle = BorderStyle.FixedSingle;
            this.Controls.Add(panelInfo);

            lblMessengerState = new Label();
            lblMessengerState.Text = "쿨메신저 연결: 감지 중...";
            lblMessengerState.Font = new Font("Malgun Gothic", 9.5F);
            lblMessengerState.Location = new Point(15, 12);
            lblMessengerState.AutoSize = true;
            panelInfo.Controls.Add(lblMessengerState);

            lblCurrentStatus = new Label();
            lblCurrentStatus.Text = "현재 메신저 상태: 확인 중...";
            lblCurrentStatus.Font = new Font("Malgun Gothic", 9.5F);
            lblCurrentStatus.Location = new Point(15, 42);
            lblCurrentStatus.AutoSize = true;
            panelInfo.Controls.Add(lblCurrentStatus);

            lblCount = new Label();
            lblCount.Text = "자동 자리비움 방어: 0 회";
            lblCount.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            lblCount.ForeColor = Color.FromArgb(245, 158, 11);
            lblCount.Location = new Point(15, 72);
            lblCount.AutoSize = true;
            panelInfo.Controls.Add(lblCount);

            // Toggle Button
            btnToggle = new Button();
            btnToggle.Text = "상시 유지 일시정지";
            btnToggle.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            btnToggle.Location = new Point(20, 192);
            btnToggle.Size = new Size(175, 38);
            btnToggle.BackColor = Color.FromArgb(241, 245, 249);
            btnToggle.FlatStyle = FlatStyle.Flat;
            btnToggle.Click += OnToggleActive;
            this.Controls.Add(btnToggle);

            // Force Away Button
            btnForceNow = new Button();
            btnForceNow.Text = "지금 즉시 자리비움";
            btnForceNow.Font = new Font("Malgun Gothic", 9.5F);
            btnForceNow.Location = new Point(210, 192);
            btnForceNow.Size = new Size(175, 38);
            btnForceNow.BackColor = Color.FromArgb(241, 245, 249);
            btnForceNow.FlatStyle = FlatStyle.Flat;
            btnForceNow.Click += OnForceAway;
            this.Controls.Add(btnForceNow);

            // Auto-start Checkbox
            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "윈도우 시작 시 자동 실행";
            chkAutoStart.Font = new Font("Malgun Gothic", 9F);
            chkAutoStart.Location = new Point(22, 242);
            chkAutoStart.AutoSize = true;
            chkAutoStart.Checked = IsAutoStartEnabled();
            chkAutoStart.CheckedChanged += OnAutoStartCheckedChanged;
            this.Controls.Add(chkAutoStart);

            // Minimize to Tray Button
            btnMinimizeToTray = new Button();
            btnMinimizeToTray.Text = "트레이로 숨기기 (백그라운드 유지)";
            btnMinimizeToTray.Font = new Font("Malgun Gothic", 9F);
            btnMinimizeToTray.Location = new Point(20, 275);
            btnMinimizeToTray.Size = new Size(365, 30);
            btnMinimizeToTray.BackColor = Color.FromArgb(226, 232, 240);
            btnMinimizeToTray.FlatStyle = FlatStyle.Flat;
            btnMinimizeToTray.Click += delegate(object s, EventArgs e)
            {
                this.Hide();
                trayIcon.ShowBalloonTip(1500, "CoolKeeper", "작업표시줄 트레이로 숨겨졌습니다.\n계속 백그라운드에서 자리비움을 유지합니다.", ToolTipIcon.Info);
            };
            this.Controls.Add(btnMinimizeToTray);

            // Creator Credit Footer
            LinkLabel linkCreator = new LinkLabel();
            linkCreator.Text = "제작: 수지샘 | 문의: lemrlog@gmail.com";
            linkCreator.Font = new Font("Malgun Gothic", 8.5F);
            linkCreator.Location = new Point(20, 318);
            linkCreator.AutoSize = true;
            linkCreator.LinkColor = Color.FromArgb(79, 70, 229);
            linkCreator.LinkClicked += delegate(object s, LinkLabelLinkClickedEventArgs e)
            {
                try { System.Diagnostics.Process.Start("mailto:lemrlog@gmail.com?subject=[CoolKeeper] 문의 및 피드백"); } catch { }
            };
            this.Controls.Add(linkCreator);

            Button btnCheckUpdate = new Button();
            btnCheckUpdate.Text = "🔄 업데이트 확인";
            btnCheckUpdate.Font = new Font("Malgun Gothic", 8F);
            btnCheckUpdate.Location = new Point(275, 314);
            btnCheckUpdate.Size = new Size(110, 26);
            btnCheckUpdate.BackColor = Color.FromArgb(241, 245, 249);
            btnCheckUpdate.FlatStyle = FlatStyle.Flat;
            btnCheckUpdate.Click += delegate { AutoUpdater.CheckForUpdates(true, this); };
            this.Controls.Add(btnCheckUpdate);
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();

            menuStatusInfo = new ToolStripMenuItem("쿨메신저: 감지 중...");
            menuStatusInfo.Enabled = false;
            trayMenu.Items.Add(menuStatusInfo);

            ToolStripMenuItem menuCredit = new ToolStripMenuItem("제작: 수지샘 (lemrlog@gmail.com)");
            menuCredit.Enabled = false;
            menuCredit.Font = new Font(trayMenu.Font, FontStyle.Italic);
            trayMenu.Items.Add(menuCredit);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem menuOpen = new ToolStripMenuItem("상태창 열기", null, delegate(object s, EventArgs e)
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.BringToFront();
            });
            menuOpen.Font = new Font(trayMenu.Font, FontStyle.Bold);
            trayMenu.Items.Add(menuOpen);

            menuActive = new ToolStripMenuItem("상시 자리비움 유지 활성화", null, OnToggleActive);
            menuActive.Checked = true;
            trayMenu.Items.Add(menuActive);

            menuAutoStart = new ToolStripMenuItem("윈도우 시작 시 자동 실행", null, delegate(object s, EventArgs e)
            {
                chkAutoStart.Checked = !chkAutoStart.Checked;
            });
            menuAutoStart.Checked = IsAutoStartEnabled();
            trayMenu.Items.Add(menuAutoStart);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem menuCheckUpdate = new ToolStripMenuItem("최신 버전 업데이트 확인...", null, delegate(object s, EventArgs e)
            {
                AutoUpdater.CheckForUpdates(true, this);
            });
            trayMenu.Items.Add(menuCheckUpdate);

            ToolStripMenuItem menuLog = new ToolStripMenuItem("동작 로그 확인 (CoolKeeper.log)", null, OnOpenLog);
            trayMenu.Items.Add(menuLog);

            ToolStripMenuItem menuExit = new ToolStripMenuItem("완전 종료", null, OnExit);
            trayMenu.Items.Add(menuExit);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "CoolKeeper - 상시 자리비움 유지 중";
            trayIcon.Icon = GenerateAppIcon();
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate(object s, EventArgs e)
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
                trayIcon.ShowBalloonTip(1500, "CoolKeeper", "트레이로 숨겨졌습니다.\n트레이 아이콘을 더블클릭하면 다시 열립니다.", ToolTipIcon.Info);
                return;
            }
            base.OnFormClosing(e);
        }

        private Icon GenerateAppIcon()
        {
            try
            {
                Icon exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null) return exeIcon;
            }
            catch { }

            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CoolKeeper.ico");
                if (File.Exists(icoPath))
                {
                    return new Icon(icoPath);
                }
            }
            catch { }

            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                using (SolidBrush brushBg = new SolidBrush(Color.FromArgb(245, 158, 11)))
                {
                    g.FillEllipse(brushBg, 2, 2, 28, 28);
                }

                using (Pen penBorder = new Pen(Color.White, 2))
                {
                    g.DrawEllipse(penBorder, 4, 4, 24, 24);
                }

                using (Pen penHand = new Pen(Color.White, 3))
                {
                    penHand.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    penHand.EndCap = System.Drawing.Drawing2D.LineCap.Round;

                    g.DrawLine(penHand, 16, 16, 16, 9);
                    g.DrawLine(penHand, 16, 16, 22, 16);
                }
            }

            IntPtr hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        private void StartWorker()
        {
            workerThread = new Thread(WorkerProc);
            workerThread.IsBackground = true;
            workerThread.Start();
        }

        private void WorkerProc()
        {
            try
            {
                IntPtr hDesk = OpenDesktop("Default", 0, false, DESKTOP_ALL);
                if (hDesk != IntPtr.Zero)
                {
                    SetThreadDesktop(hDesk);
                }
            }
            catch { }

            while (!isStopping)
            {
                try
                {
                    if (isEnforcing)
                    {
                        CheckAndEnforceAway();
                    }
                }
                catch (Exception ex)
                {
                    Log("감시 오류: " + ex.Message);
                }

                Thread.Sleep(700);
            }
        }

        private void CheckAndEnforceAway()
        {
            IntPtr mainHwnd = FindWindow("#32770", "COOLMESSENGER");
            if (mainHwnd == IntPtr.Zero)
            {
                lastLoggedStatus = "";
                UpdateUI(false, "미실행 (대기 중)", "");
                return;
            }

            IntPtr btnHwnd = GetDlgItem(mainHwnd, 3017);
            if (btnHwnd == IntPtr.Zero)
            {
                lastLoggedStatus = "";
                UpdateUI(true, "로그인 대기 중", "");
                return;
            }

            StringBuilder sbText = new StringBuilder(256);
            GetWindowText(btnHwnd, sbText, 256);
            string currentStatus = sbText.ToString().Trim();

            if (!string.IsNullOrEmpty(currentStatus))
            {
                if (!currentStatus.Contains("비움"))
                {
                    if (lastLoggedStatus != currentStatus)
                    {
                        Log(string.Format("상태 변경 감지: '{0}' -> '잠시 비움'으로 자동 전환 시도", currentStatus));
                        lastLoggedStatus = currentStatus;
                    }

                    bool ok = TriggerAwayClick(btnHwnd);
                    if (ok)
                    {
                        fixCount++;
                        Log("자리비움 전환 완료");
                        UpdateUI(true, "정상 감지됨", "잠시 비움 (방어됨)");
                    }
                    else
                    {
                        UpdateUI(true, "정상 감지됨", currentStatus);
                    }
                }
                else
                {
                    lastLoggedStatus = currentStatus;
                    UpdateUI(true, "정상 감지됨", "잠시 비움 (유지 중)");
                }
            }
        }

        private const uint BM_CLICK = 0x00F5;

        private bool TriggerAwayClick(IntPtr btnHwnd)
        {
            IntPtr menuHwnd = FindWindow("#32770", "CMenuEx");
            
            // 쿨메신저 재부팅/재로그인 직후에는 CMenuEx 메뉴 윈도우가 아직 메모리에 생성되어 있지 않습니다.
            // 이때 상태 버튼(ID 3017)에 클릭 메시지를 보내 CMenuEx를 즉시 생성시킵니다!
            if (menuHwnd == IntPtr.Zero && btnHwnd != IntPtr.Zero)
            {
                PostMessage(btnHwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                PostMessage(btnHwnd, WM_LBUTTONDOWN, (IntPtr)1, IntPtr.Zero);
                PostMessage(btnHwnd, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(40);
                    menuHwnd = FindWindow("#32770", "CMenuEx");
                    if (menuHwnd != IntPtr.Zero) break;
                }
            }

            if (menuHwnd == IntPtr.Zero)
            {
                return false;
            }

            IntPtr awayItemHwnd = IntPtr.Zero;

            EnumChildWindows(menuHwnd, delegate(IntPtr hwnd, IntPtr lparam)
            {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string text = sb.ToString();

                if (text.Contains("(W)"))
                {
                    awayItemHwnd = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (awayItemHwnd != IntPtr.Zero)
            {
                PostMessage(awayItemHwnd, WM_LBUTTONDOWN, (IntPtr)1, IntPtr.Zero);
                PostMessage(awayItemHwnd, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                return true;
            }

            return false;
        }

        private void UpdateUI(bool connected, string messengerState, string statusText)
        {
            if (this.IsDisposed) return;

            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    lblMessengerState.Text = "쿨메신저 연결: " + messengerState;
                    if (!string.IsNullOrEmpty(statusText))
                    {
                        lblCurrentStatus.Text = "현재 메신저 상태: " + statusText;
                    }
                    lblCount.Text = "자동 자리비움 방어: " + fixCount + " 회";

                    if (menuStatusInfo != null)
                    {
                        menuStatusInfo.Text = "쿨메신저: " + (string.IsNullOrEmpty(statusText) ? messengerState : statusText);
                    }
                });
            }
            catch { }
        }

        private void Log(string msg)
        {
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, msg);
                File.AppendAllText(logFilePath, line, Encoding.UTF8);
            }
            catch { }
        }

        private void OnToggleActive(object sender, EventArgs e)
        {
            isEnforcing = !isEnforcing;
            menuActive.Checked = isEnforcing;

            if (isEnforcing)
            {
                btnToggle.Text = "상시 유지 일시정지";
                lblStatusBadge.Text = "● 상시 유지 작동 중";
                lblStatusBadge.ForeColor = Color.FromArgb(16, 185, 129);
                Log("상시 자리비움 유지가 [활성화]되었습니다.");
            }
            else
            {
                btnToggle.Text = "상시 유지 다시 켜기";
                lblStatusBadge.Text = "● 일시정지됨";
                lblStatusBadge.ForeColor = Color.FromArgb(239, 68, 68);
                Log("상시 자리비움 유지가 [일시정지]되었습니다.");
            }
        }

        private void OnForceAway(object sender, EventArgs e)
        {
            IntPtr mainHwnd = FindWindow("#32770", "COOLMESSENGER");
            IntPtr btnHwnd = mainHwnd != IntPtr.Zero ? GetDlgItem(mainHwnd, 3017) : IntPtr.Zero;
            bool ok = TriggerAwayClick(btnHwnd);
            if (ok)
            {
                fixCount++;
                Log("수동으로 즉시 자리비움 설정함");
                lblCurrentStatus.Text = "현재 메신저 상태: 잠시 비움 (수동 설정됨)";
                lblCount.Text = "자동 자리비움 방어: " + fixCount + " 회";
                MessageBox.Show("쿨메신저 상태를 '잠시 비움'으로 즉시 변경했습니다.", "CoolKeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("쿨메신저를 찾을 수 없거나 아직 로그인되지 않았습니다.", "CoolKeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnAutoStartCheckedChanged(object sender, EventArgs e)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null)
                    {
                        if (!chkAutoStart.Checked)
                        {
                            key.DeleteValue(AppName, false);
                            menuAutoStart.Checked = false;
                            Log("시작 프로그램 등록 해제됨");
                        }
                        else
                        {
                            string exePath = Application.ExecutablePath;
                            key.SetValue(AppName, "\"" + exePath + "\"");
                            menuAutoStart.Checked = true;
                            Log("시작 프로그램으로 등록됨");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("시작 프로그램 설정 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOpenLog(object sender, EventArgs e)
        {
            try
            {
                if (File.Exists(logFilePath))
                {
                    System.Diagnostics.Process.Start("notepad.exe", logFilePath);
                }
                else
                {
                    MessageBox.Show("로그 파일이 아직 생성되지 않았습니다.", "CoolKeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("로그 열기 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        private void OnExit(object sender, EventArgs e)
        {
            isExiting = true;
            isStopping = true;
            Log("=== CoolKeeper 정상 종료 ===");

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }

            Application.Exit();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(3000);
                AutoUpdater.CheckForUpdates(false, this);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (trayIcon != null) trayIcon.Dispose();
                if (trayMenu != null) trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    #region Auto-Updater System
    public static class AutoUpdater
    {
        public static string CurrentVersion = "1.1.0";
        public static string AppDisplayName = "CoolKeeper";
        public static string DefaultCheckUrl = "https://raw.githubusercontent.com/gssg100/CoolSuite/main/updates/coolkeeper.json";

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
