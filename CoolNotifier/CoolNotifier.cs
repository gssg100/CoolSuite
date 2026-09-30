using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("CoolNotifier - 쿨메신저 모바일 실시간 알림 전달")]
[assembly: AssemblyDescription("교사를 위한 쿨메신저 모바일(텔레그램·디스코드) 실시간 전달 도우미")]
[assembly: AssemblyCompany("수지샘 (lemrlog@gmail.com)")]
[assembly: AssemblyProduct("CoolNotifier")]
[assembly: AssemblyCopyright("Copyright © 2026 수지샘 (lemrlog@gmail.com) All rights reserved.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace CoolNotifier
{
    public static class Logger
    {
        private static string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CoolNotifier.log");
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
            Logger.Log("=== CoolNotifier 시작됨 ===");
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Logger.Log("치명적 오류 (UnhandledException): " + e.ExceptionObject.ToString());
            };

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "CoolNotifier_SingleInstance_Mutex_suji", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("CoolNotifier가 이미 실행 중입니다.\n작업표시줄 우측 하단 트레이 아이콘을 확인해주세요.", "CoolNotifier", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }

    #region SQLite Native P/Invoke (winsqlite3.dll)
    public static class WinSqlite
    {
        const string DLL = "winsqlite3.dll";

        [DllImport(DLL, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);

        [DllImport(DLL, EntryPoint = "sqlite3_prepare16_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string sql, int numBytes, out IntPtr stmt, IntPtr pzTail);

        [DllImport(DLL, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_step(IntPtr stmt);

        [DllImport(DLL, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        public static extern long sqlite3_column_int64(IntPtr stmt, int col);

        [DllImport(DLL, EntryPoint = "sqlite3_column_text16", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);

        [DllImport(DLL, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(DLL, EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport(DLL, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close(IntPtr db);

        public const int SQLITE_OPEN_READONLY = 0x00000001;
        public const int SQLITE_OPEN_URI = 0x00000040;
        public const int SQLITE_ROW = 100;
        public const int SQLITE_DONE = 101;
    }
    #endregion

    #region Config & History Models
    public class NotifierConfig
    {
        public bool IsActive = true;
        public string DatabasePath = "";

        // Telegram
        public bool TelegramEnabled = false;
        public string TelegramToken = "";
        public string TelegramChatId = "";

        // Discord
        public bool DiscordEnabled = false;
        public string DiscordWebhook = "";

        // Notification Mode: 0 = Full (전체 전문), 1 = Safe Summary (안심 요약: 제목+발신자만), 2 = Simple (단순 알림)
        public int NotificationMode = 0;

        // Filtering
        public string KeywordFilter = ""; // Comma separated (e.g. [긴급], [공지])
        public string SenderFilter = "";

        // DND (Do Not Disturb)
        public bool DndEnabled = false;
        public string DndStart = "18:00";
        public string DndEnd = "08:30";
        public bool DndWeekend = true;

        public long LastProcessedKey = 0;
        public int TotalSentCount = 0;
        public int TotalFailCount = 0;
    }

    public class HistoryRecord
    {
        public string TimeStr;
        public string Sender;
        public string Title;
        public string Channel;
        public string Status; // "성공", "실패"
    }
    #endregion

    #region Config Storage
    public static class Storage
    {
        private static string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CoolNotifierConfig.json");
        private static string historyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CoolNotifierHistory.txt");
        public static NotifierConfig Config = new NotifierConfig();
        public static List<HistoryRecord> History = new List<HistoryRecord>();
        private static object syncLock = new object();

        static Storage()
        {
            LoadConfig();
            LoadHistory();
        }

        public static void LoadConfig()
        {
            lock (syncLock)
            {
                try
                {
                    if (File.Exists(configPath))
                    {
                        string json = File.ReadAllText(configPath, Encoding.UTF8);
                        Config.IsActive = GetBool(json, "IsActive", true);
                        Config.DatabasePath = GetStr(json, "DatabasePath", "");
                        Config.TelegramEnabled = GetBool(json, "TelegramEnabled", false);
                        Config.TelegramToken = GetStr(json, "TelegramToken", "");
                        Config.TelegramChatId = GetStr(json, "TelegramChatId", "");
                        Config.DiscordEnabled = GetBool(json, "DiscordEnabled", false);
                        Config.DiscordWebhook = GetStr(json, "DiscordWebhook", "");
                        Config.NotificationMode = GetInt(json, "NotificationMode", 0);
                        Config.KeywordFilter = GetStr(json, "KeywordFilter", "");
                        Config.SenderFilter = GetStr(json, "SenderFilter", "");
                        Config.DndEnabled = GetBool(json, "DndEnabled", false);
                        Config.DndStart = GetStr(json, "DndStart", "18:00");
                        Config.DndEnd = GetStr(json, "DndEnd", "08:30");
                        Config.DndWeekend = GetBool(json, "DndWeekend", true);
                        Config.LastProcessedKey = GetLong(json, "LastProcessedKey", 0);
                        Config.TotalSentCount = GetInt(json, "TotalSentCount", 0);
                        Config.TotalFailCount = GetInt(json, "TotalFailCount", 0);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("설정 로드 실패: " + ex.Message);
                }
            }
        }

        public static void SaveConfig()
        {
            lock (syncLock)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("{");
                    sb.AppendLine(string.Format("  \"IsActive\": {0},", Config.IsActive.ToString().ToLower()));
                    sb.AppendLine(string.Format("  \"DatabasePath\": \"{0}\",", EscapeJson(Config.DatabasePath)));
                    sb.AppendLine(string.Format("  \"TelegramEnabled\": {0},", Config.TelegramEnabled.ToString().ToLower()));
                    sb.AppendLine(string.Format("  \"TelegramToken\": \"{0}\",", EscapeJson(Config.TelegramToken)));
                    sb.AppendLine(string.Format("  \"TelegramChatId\": \"{0}\",", EscapeJson(Config.TelegramChatId)));
                    sb.AppendLine(string.Format("  \"DiscordEnabled\": {0},", Config.DiscordEnabled.ToString().ToLower()));
                    sb.AppendLine(string.Format("  \"DiscordWebhook\": \"{0}\",", EscapeJson(Config.DiscordWebhook)));
                    sb.AppendLine(string.Format("  \"NotificationMode\": {0},", Config.NotificationMode));
                    sb.AppendLine(string.Format("  \"KeywordFilter\": \"{0}\",", EscapeJson(Config.KeywordFilter)));
                    sb.AppendLine(string.Format("  \"SenderFilter\": \"{0}\",", EscapeJson(Config.SenderFilter)));
                    sb.AppendLine(string.Format("  \"DndEnabled\": {0},", Config.DndEnabled.ToString().ToLower()));
                    sb.AppendLine(string.Format("  \"DndStart\": \"{0}\",", EscapeJson(Config.DndStart)));
                    sb.AppendLine(string.Format("  \"DndEnd\": \"{0}\",", EscapeJson(Config.DndEnd)));
                    sb.AppendLine(string.Format("  \"DndWeekend\": {0},", Config.DndWeekend.ToString().ToLower()));
                    sb.AppendLine(string.Format("  \"LastProcessedKey\": {0},", Config.LastProcessedKey));
                    sb.AppendLine(string.Format("  \"TotalSentCount\": {0},", Config.TotalSentCount));
                    sb.AppendLine(string.Format("  \"TotalFailCount\": {0}", Config.TotalFailCount));
                    sb.AppendLine("}");
                    File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Logger.Log("설정 저장 실패: " + ex.Message);
                }
            }
        }

        public static void AddHistory(string sender, string title, string channel, string status)
        {
            lock (syncLock)
            {
                try
                {
                    var item = new HistoryRecord
                    {
                        TimeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        Sender = sender,
                        Title = title,
                        Channel = channel,
                        Status = status
                    };
                    History.Insert(0, item);
                    if (History.Count > 100) History.RemoveAt(History.Count - 1);

                    string line = string.Format("{0}\t{1}\t{2}\t{3}\t{4}\r\n", item.TimeStr, item.Sender, item.Title.Replace("\t", " "), item.Channel, item.Status);
                    File.AppendAllText(historyPath, line, Encoding.UTF8);
                }
                catch { }
            }
        }

        private static void LoadHistory()
        {
            try
            {
                if (File.Exists(historyPath))
                {
                    string[] lines = File.ReadAllLines(historyPath, Encoding.UTF8);
                    for (int i = lines.Length - 1; i >= 0 && History.Count < 50; i--)
                    {
                        string[] parts = lines[i].Split('\t');
                        if (parts.Length >= 5)
                        {
                            History.Add(new HistoryRecord
                            {
                                TimeStr = parts[0],
                                Sender = parts[1],
                                Title = parts[2],
                                Channel = parts[3],
                                Status = parts[4]
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string GetStr(string json, string key, string def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\"", "\"").Replace("\\\\", "\\") : def;
        }

        private static int GetInt(string json, string key, int def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            int val;
            return (m.Success && int.TryParse(m.Groups[1].Value, out val)) ? val : def;
        }

        private static long GetLong(string json, string key, long def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            long val;
            return (m.Success && long.TryParse(m.Groups[1].Value, out val)) ? val : def;
        }

        private static bool GetBool(string json, string key, bool def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLower() == "true" : def;
        }
    }
    #endregion

    #region Forwarding Sender
    public static class MessageForwarder
    {
        public static bool SendTelegram(string botToken, string chatId, string text, out string err)
        {
            err = "";
            try
            {
                if (text != null && text.Length > 3800)
                {
                    text = text.Substring(0, 3800) + "\n\n... (내용이 길어 일부 생략됨: PC 쿨메신저에서 확인)";
                }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                string url = string.Format("https://api.telegram.org/bot{0}/sendMessage", botToken.Trim());
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    var values = new NameValueCollection();
                    values["chat_id"] = chatId.Trim();
                    values["text"] = text;
                    values["parse_mode"] = "HTML";

                    byte[] response = client.UploadValues(url, values);
                    string respStr = Encoding.UTF8.GetString(response);
                    if (respStr.Contains("\"ok\":true"))
                    {
                        return true;
                    }
                    else
                    {
                        err = respStr;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        public static bool SendDiscord(string webhookUrl, string title, string sender, string date, string body, string attach, out string err)
        {
            err = "";
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    client.Headers.Add(HttpRequestHeader.ContentType, "application/json");

                    string cleanTitle = EscapeJson(title);
                    string cleanSender = EscapeJson(sender);
                    string cleanDate = EscapeJson(date);
                    string cleanBody = EscapeJson(body.Length > 1000 ? body.Substring(0, 1000) + "..." : body);
                    string cleanAttach = EscapeJson(string.IsNullOrEmpty(attach) ? "없음" : attach);

                    string json = string.Format(@"{{
  ""username"": ""CoolNotifier (수지샘)"",
  ""embeds"": [
    {{
      ""title"": ""🔔 쿨메신저 새 쪽지"",
      ""description"": ""**{0}**"",
      ""color"": 5025616,
      ""fields"": [
        {{ ""name"": ""👤 보낸사람"", ""value"": ""{1}"", ""inline"": true }},
        {{ ""name"": ""📅 수신일시"", ""value"": ""{2}"", ""inline"": true }},
        {{ ""name"": ""💬 내용"", ""value"": ""{3}"" }},
        {{ ""name"": ""📎 첨부파일"", ""value"": ""{4}"" }}
      ],
      ""footer"": {{ ""text"": ""CoolSuite - 교사를 위한 쿨메신저 도우미"" }}
    }}
  ]
}}", cleanTitle, cleanSender, cleanDate, cleanBody, cleanAttach);

                    byte[] data = Encoding.UTF8.GetBytes(json);
                    byte[] resp = client.UploadData(webhookUrl.Trim(), "POST", data);
                    return true;
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }
    }
    #endregion

    #region MainForm UI
    public class MainForm : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private bool isExiting = false;

        private Label lblHeaderTitle;
        private Label lblStatusBadge;
        private Label lblStatSummary;

        private CheckBox chkActive;
        private CheckBox chkTelegram;
        private TextBox txtTelegramToken;
        private TextBox txtTelegramChatId;
        private Button btnTestTelegram;

        private CheckBox chkDiscord;
        private TextBox txtDiscordWebhook;
        private Button btnTestDiscord;

        private RadioButton rdoModeFull;
        private RadioButton rdoModeSafe;
        private RadioButton rdoModeSimple;

        private TextBox txtKeywords;
        private CheckBox chkDnd;
        private TextBox txtDndStart;
        private TextBox txtDndEnd;
        private CheckBox chkDndWeekend;

        private CheckBox chkAutoStart;
        private Button btnSave;
        private Button btnCheckUpdate;
        private ListView lvHistory;

        private Thread watcherThread;
        private volatile bool isStopping = false;

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "CoolNotifier";

        public MainForm()
        {
            BuildUI();
            InitializeTray();
            StartWatcher();
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

        private void BuildUI()
        {
            this.Text = "CoolNotifier v1.0.0 - 쿨메신저 모바일 실시간 알림 전달 (수지샘)";
            this.Size = new Size(680, 720);
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

            // 1. Header Banner
            Panel pnlHeader = new Panel();
            pnlHeader.Dock = DockStyle.Top;
            pnlHeader.Height = 70;
            pnlHeader.BackColor = Color.FromArgb(49, 46, 129); // Deep Indigo
            this.Controls.Add(pnlHeader);

            lblHeaderTitle = new Label();
            lblHeaderTitle.Text = "CoolNotifier v1.0.0";
            lblHeaderTitle.Font = new Font("Malgun Gothic", 14F, FontStyle.Bold);
            lblHeaderTitle.ForeColor = Color.White;
            lblHeaderTitle.Location = new Point(20, 12);
            lblHeaderTitle.AutoSize = true;
            pnlHeader.Controls.Add(lblHeaderTitle);

            Label lblHeaderSub = new Label();
            lblHeaderSub.Text = "쿨메신저 쪽지 수신 시 스마트폰(텔레그램·디스코드)으로 즉시 무결점 전달";
            lblHeaderSub.Font = new Font("Malgun Gothic", 9F);
            lblHeaderSub.ForeColor = Color.FromArgb(199, 210, 254);
            lblHeaderSub.Location = new Point(22, 40);
            lblHeaderSub.AutoSize = true;
            pnlHeader.Controls.Add(lblHeaderSub);

            lblStatusBadge = new Label();
            lblStatusBadge.Text = "● 모바일 전달 대기 중";
            lblStatusBadge.Font = new Font("Malgun Gothic", 10F, FontStyle.Bold);
            lblStatusBadge.ForeColor = Color.FromArgb(52, 211, 153);
            lblStatusBadge.Location = new Point(480, 24);
            lblStatusBadge.AutoSize = true;
            pnlHeader.Controls.Add(lblStatusBadge);

            // 2. Tab Control for organized layout
            TabControl tabCtrl = new TabControl();
            tabCtrl.Location = new Point(15, 80);
            tabCtrl.Size = new Size(635, 490);
            tabCtrl.Font = new Font("Malgun Gothic", 9.5F);
            this.Controls.Add(tabCtrl);

            // TAB 1: 모바일 전송 채널 설정
            TabPage tabChannels = new TabPage("📱 모바일 전송 설정");
            tabChannels.BackColor = Color.White;
            BuildTabChannels(tabChannels);
            tabCtrl.TabPages.Add(tabChannels);

            // TAB 2: 안심 모드 및 방해금지 필터
            TabPage tabPrivacy = new TabPage("🛡️ 안심 모드 & 방해금지");
            tabPrivacy.BackColor = Color.White;
            BuildTabPrivacy(tabPrivacy);
            tabCtrl.TabPages.Add(tabPrivacy);

            // TAB 3: 전송 이력
            TabPage tabHistory = new TabPage("📜 전송 이력");
            tabHistory.BackColor = Color.White;
            BuildTabHistory(tabHistory);
            tabCtrl.TabPages.Add(tabHistory);

            // 3. Bottom Stats & Buttons Panel
            lblStatSummary = new Label();
            lblStatSummary.Text = string.Format("📊 누적 전달: {0}건 | 실패: {1}건 | 상태 정상", Storage.Config.TotalSentCount, Storage.Config.TotalFailCount);
            lblStatSummary.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            lblStatSummary.ForeColor = Color.FromArgb(71, 85, 105);
            lblStatSummary.Location = new Point(20, 580);
            lblStatSummary.AutoSize = true;
            this.Controls.Add(lblStatSummary);

            chkActive = new CheckBox();
            chkActive.Text = "실시간 모바일 전달 활성화";
            chkActive.Checked = Storage.Config.IsActive;
            chkActive.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            chkActive.Location = new Point(20, 605);
            chkActive.AutoSize = true;
            chkActive.CheckedChanged += delegate { Storage.Config.IsActive = chkActive.Checked; Storage.SaveConfig(); RefreshBadge(); };
            this.Controls.Add(chkActive);

            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "윈도우 시작 시 자동 실행";
            chkAutoStart.Checked = IsAutoStartEnabled();
            chkAutoStart.Font = new Font("Malgun Gothic", 9F);
            chkAutoStart.Location = new Point(230, 607);
            chkAutoStart.AutoSize = true;
            chkAutoStart.CheckedChanged += OnAutoStartChanged;
            this.Controls.Add(chkAutoStart);

            btnSave = new Button();
            btnSave.Text = "💾 설정 저장하기";
            btnSave.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            btnSave.BackColor = Color.FromArgb(79, 70, 229);
            btnSave.ForeColor = Color.White;
            btnSave.FlatStyle = FlatStyle.Flat;
            btnSave.Location = new Point(480, 600);
            btnSave.Size = new Size(165, 36);
            btnSave.Click += OnSaveClicked;
            this.Controls.Add(btnSave);

            btnCheckUpdate = new Button();
            btnCheckUpdate.Text = "🔄 업데이트 확인";
            btnCheckUpdate.Font = new Font("Malgun Gothic", 8.5F);
            btnCheckUpdate.Location = new Point(20, 642);
            btnCheckUpdate.Size = new Size(125, 26);
            btnCheckUpdate.BackColor = Color.FromArgb(241, 245, 249);
            btnCheckUpdate.FlatStyle = FlatStyle.Flat;
            btnCheckUpdate.Click += delegate { AutoUpdater.CheckForUpdates(true, this); };
            this.Controls.Add(btnCheckUpdate);

            Label lblFooter = new Label();
            lblFooter.Text = "⭐ 공식 제작 인증 | 제작자: 수지샘 | 피드백: lemrlog@gmail.com";
            lblFooter.Font = new Font("Malgun Gothic", 8F);
            lblFooter.ForeColor = Color.FromArgb(148, 163, 184);
            lblFooter.Location = new Point(245, 647);
            lblFooter.AutoSize = true;
            this.Controls.Add(lblFooter);

            RefreshBadge();
        }

        private void BuildTabChannels(TabPage tab)
        {
            // Telegram Group
            GroupBox grpTelegram = new GroupBox();
            grpTelegram.Text = " 텔레그램 봇 (Telegram) 연동 [추천] ";
            grpTelegram.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            grpTelegram.Location = new Point(15, 15);
            grpTelegram.Size = new Size(600, 185);
            tab.Controls.Add(grpTelegram);

            chkTelegram = new CheckBox();
            chkTelegram.Text = "텔레그램으로 쪽지 수신 알림 받기";
            chkTelegram.Checked = Storage.Config.TelegramEnabled;
            chkTelegram.Location = new Point(20, 28);
            chkTelegram.AutoSize = true;
            grpTelegram.Controls.Add(chkTelegram);

            Label lblToken = new Label();
            lblToken.Text = "Bot Token:";
            lblToken.Font = new Font("Malgun Gothic", 9F);
            lblToken.Location = new Point(20, 60);
            lblToken.AutoSize = true;
            grpTelegram.Controls.Add(lblToken);

            txtTelegramToken = new TextBox();
            txtTelegramToken.Text = Storage.Config.TelegramToken;
            txtTelegramToken.Font = new Font("Consolas", 9F);
            txtTelegramToken.Location = new Point(105, 57);
            txtTelegramToken.Size = new Size(470, 23);
            grpTelegram.Controls.Add(txtTelegramToken);

            Label lblChatId = new Label();
            lblChatId.Text = "Chat ID:";
            lblChatId.Font = new Font("Malgun Gothic", 9F);
            lblChatId.Location = new Point(20, 95);
            lblChatId.AutoSize = true;
            grpTelegram.Controls.Add(lblChatId);

            txtTelegramChatId = new TextBox();
            txtTelegramChatId.Text = Storage.Config.TelegramChatId;
            txtTelegramChatId.Font = new Font("Consolas", 9F);
            txtTelegramChatId.Location = new Point(105, 92);
            txtTelegramChatId.Size = new Size(250, 23);
            grpTelegram.Controls.Add(txtTelegramChatId);

            btnTestTelegram = new Button();
            btnTestTelegram.Text = "🔔 텔레그램 테스트 전송";
            btnTestTelegram.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            btnTestTelegram.BackColor = Color.FromArgb(241, 245, 249);
            btnTestTelegram.FlatStyle = FlatStyle.Flat;
            btnTestTelegram.Location = new Point(370, 90);
            btnTestTelegram.Size = new Size(205, 28);
            btnTestTelegram.Click += OnTestTelegram;
            grpTelegram.Controls.Add(btnTestTelegram);

            Label lblTeleGuide = new Label();
            lblTeleGuide.Text = "💡 [1분 컷 가이드] 텔레그램에서 @BotFather 검색 후 /newbot 하여 Token 발급,\n    내 봇에 /start 누르고 @userinfobot 검색하여 내 Chat ID 확인 후 입력!";
            lblTeleGuide.Font = new Font("Malgun Gothic", 8F);
            lblTeleGuide.ForeColor = Color.FromArgb(100, 116, 139);
            lblTeleGuide.Location = new Point(20, 130);
            lblTeleGuide.AutoSize = true;
            grpTelegram.Controls.Add(lblTeleGuide);

            // Discord Group
            GroupBox grpDiscord = new GroupBox();
            grpDiscord.Text = " 디스코드 (Discord) 웹훅 연동 ";
            grpDiscord.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            grpDiscord.Location = new Point(15, 215);
            grpDiscord.Size = new Size(600, 155);
            tab.Controls.Add(grpDiscord);

            chkDiscord = new CheckBox();
            chkDiscord.Text = "디스코드로 쪽지 수신 알림 받기";
            chkDiscord.Checked = Storage.Config.DiscordEnabled;
            chkDiscord.Location = new Point(20, 28);
            chkDiscord.AutoSize = true;
            grpDiscord.Controls.Add(chkDiscord);

            Label lblWebhook = new Label();
            lblWebhook.Text = "Webhook URL:";
            lblWebhook.Font = new Font("Malgun Gothic", 9F);
            lblWebhook.Location = new Point(20, 60);
            lblWebhook.AutoSize = true;
            grpDiscord.Controls.Add(lblWebhook);

            txtDiscordWebhook = new TextBox();
            txtDiscordWebhook.Text = Storage.Config.DiscordWebhook;
            txtDiscordWebhook.Font = new Font("Consolas", 8.5F);
            txtDiscordWebhook.Location = new Point(115, 57);
            txtDiscordWebhook.Size = new Size(460, 23);
            grpDiscord.Controls.Add(txtDiscordWebhook);

            btnTestDiscord = new Button();
            btnTestDiscord.Text = "🔔 디스코드 테스트 전송";
            btnTestDiscord.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            btnTestDiscord.BackColor = Color.FromArgb(241, 245, 249);
            btnTestDiscord.FlatStyle = FlatStyle.Flat;
            btnTestDiscord.Location = new Point(115, 92);
            btnTestDiscord.Size = new Size(200, 28);
            btnTestDiscord.Click += OnTestDiscord;
            grpDiscord.Controls.Add(btnTestDiscord);

            Label lblDiscGuide = new Label();
            lblDiscGuide.Text = "💡 디스코드 채널 설정 ➡️ 연동 ➡️ 웹훅 만들기에서 URL 복사 후 붙여넣기!";
            lblDiscGuide.Font = new Font("Malgun Gothic", 8F);
            lblDiscGuide.ForeColor = Color.FromArgb(100, 116, 139);
            lblDiscGuide.Location = new Point(20, 128);
            lblDiscGuide.AutoSize = true;
            grpDiscord.Controls.Add(lblDiscGuide);
        }

        private void BuildTabPrivacy(TabPage tab)
        {
            // Mode Box
            GroupBox grpMode = new GroupBox();
            grpMode.Text = " 개인정보 보호 & 전송 모드 선택 ";
            grpMode.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            grpMode.Location = new Point(15, 15);
            grpMode.Size = new Size(600, 130);
            tab.Controls.Add(grpMode);

            rdoModeFull = new RadioButton();
            rdoModeFull.Text = "전체 모드: 발신자 + 수신일시 + 제목 + 본문 전문 + 첨부파일명 모두 전달";
            rdoModeFull.Location = new Point(20, 28);
            rdoModeFull.AutoSize = true;
            rdoModeFull.Checked = Storage.Config.NotificationMode == 0;
            grpMode.Controls.Add(rdoModeFull);

            rdoModeSafe = new RadioButton();
            rdoModeSafe.Text = "안심 모드 (추천): 발신자 + 일시 + 제목만 전달 (본문 제외 - 학생 개인정보 보호)";
            rdoModeSafe.Location = new Point(20, 58);
            rdoModeSafe.AutoSize = true;
            rdoModeSafe.Checked = Storage.Config.NotificationMode == 1;
            grpMode.Controls.Add(rdoModeSafe);

            rdoModeSimple = new RadioButton();
            rdoModeSimple.Text = "단순 알림 모드: '새 쪽지 도착 (발신자: ○○○)' 형태만 단순 전달";
            rdoModeSimple.Location = new Point(20, 88);
            rdoModeSimple.AutoSize = true;
            rdoModeSimple.Checked = Storage.Config.NotificationMode == 2;
            grpMode.Controls.Add(rdoModeSimple);

            // Filtering Box
            GroupBox grpFilter = new GroupBox();
            grpFilter.Text = " 중요 쪽지 키워드 필터링 (선택 사항) ";
            grpFilter.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            grpFilter.Location = new Point(15, 160);
            grpFilter.Size = new Size(600, 110);
            tab.Controls.Add(grpFilter);

            Label lblKw = new Label();
            lblKw.Text = "지정 키워드가 포함된 쪽지만 스마트폰으로 전송합니다. (비워두면 모든 쪽지 전송):";
            lblKw.Font = new Font("Malgun Gothic", 8.5F);
            lblKw.Location = new Point(20, 25);
            lblKw.AutoSize = true;
            grpFilter.Controls.Add(lblKw);

            txtKeywords = new TextBox();
            txtKeywords.Text = Storage.Config.KeywordFilter;
            txtKeywords.Font = new Font("Malgun Gothic", 9F);
            txtKeywords.Location = new Point(20, 50);
            txtKeywords.Size = new Size(560, 23);
            grpFilter.Controls.Add(txtKeywords);

            Label lblKwSub = new Label();
            lblKwSub.Text = "예시: [긴급], [공지], 교무, 출결, 회의, 협조 (쉼표로 구분)";
            lblKwSub.Font = new Font("Malgun Gothic", 8F);
            lblKwSub.ForeColor = Color.FromArgb(100, 116, 139);
            lblKwSub.Location = new Point(20, 80);
            lblKwSub.AutoSize = true;
            grpFilter.Controls.Add(lblKwSub);

            // DND Box
            GroupBox grpDnd = new GroupBox();
            grpDnd.Text = " 퇴근 후 & 주말 방해금지 시간대 ";
            grpDnd.Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            grpDnd.Location = new Point(15, 285);
            grpDnd.Size = new Size(600, 125);
            tab.Controls.Add(grpDnd);

            chkDnd = new CheckBox();
            chkDnd.Text = "지정된 시간대에는 스마트폰 알림 보내지 않기";
            chkDnd.Checked = Storage.Config.DndEnabled;
            chkDnd.Location = new Point(20, 28);
            chkDnd.AutoSize = true;
            grpDnd.Controls.Add(chkDnd);

            Label lblDndTime = new Label();
            lblDndTime.Text = "방해금지 시간대:";
            lblDndTime.Font = new Font("Malgun Gothic", 9F);
            lblDndTime.Location = new Point(40, 58);
            lblDndTime.AutoSize = true;
            grpDnd.Controls.Add(lblDndTime);

            txtDndStart = new TextBox();
            txtDndStart.Text = Storage.Config.DndStart;
            txtDndStart.Size = new Size(55, 23);
            txtDndStart.Location = new Point(145, 55);
            txtDndStart.TextAlign = HorizontalAlignment.Center;
            grpDnd.Controls.Add(txtDndStart);

            Label lblDndWave = new Label();
            lblDndWave.Text = "~";
            lblDndWave.Location = new Point(205, 58);
            lblDndWave.AutoSize = true;
            grpDnd.Controls.Add(lblDndWave);

            txtDndEnd = new TextBox();
            txtDndEnd.Text = Storage.Config.DndEnd;
            txtDndEnd.Size = new Size(55, 23);
            txtDndEnd.Location = new Point(225, 55);
            txtDndEnd.TextAlign = HorizontalAlignment.Center;
            grpDnd.Controls.Add(txtDndEnd);

            chkDndWeekend = new CheckBox();
            chkDndWeekend.Text = "토요일·일요일(주말) 종일 알림 끄기";
            chkDndWeekend.Checked = Storage.Config.DndWeekend;
            chkDndWeekend.Location = new Point(320, 56);
            chkDndWeekend.AutoSize = true;
            grpDnd.Controls.Add(chkDndWeekend);

            Label lblDndInfo = new Label();
            lblDndInfo.Text = "💡 예: 18:00 ~ 08:30으로 설정 시 퇴근 후 밤 시간에는 폰 알림이 울리지 않습니다.";
            lblDndInfo.Font = new Font("Malgun Gothic", 8F);
            lblDndInfo.ForeColor = Color.FromArgb(100, 116, 139);
            lblDndInfo.Location = new Point(40, 90);
            lblDndInfo.AutoSize = true;
            grpDnd.Controls.Add(lblDndInfo);
        }

        private void BuildTabHistory(TabPage tab)
        {
            lvHistory = new ListView();
            lvHistory.View = View.Details;
            lvHistory.FullRowSelect = true;
            lvHistory.GridLines = true;
            lvHistory.Location = new Point(15, 15);
            lvHistory.Size = new Size(600, 410);
            lvHistory.Font = new Font("Malgun Gothic", 8.5F);

            lvHistory.Columns.Add("수신시각", 130);
            lvHistory.Columns.Add("보낸이", 90);
            lvHistory.Columns.Add("제목", 240);
            lvHistory.Columns.Add("채널", 70);
            lvHistory.Columns.Add("결과", 60);

            tab.Controls.Add(lvHistory);

            Button btnClear = new Button();
            btnClear.Text = "이력 비우기";
            btnClear.Font = new Font("Malgun Gothic", 8.5F);
            btnClear.Location = new Point(515, 432);
            btnClear.Size = new Size(100, 26);
            btnClear.Click += delegate { Storage.History.Clear(); RefreshHistoryUI(); };
            tab.Controls.Add(btnClear);

            RefreshHistoryUI();
        }

        private void RefreshHistoryUI()
        {
            lvHistory.BeginUpdate();
            lvHistory.Items.Clear();
            foreach (var h in Storage.History)
            {
                var lvi = new ListViewItem(h.TimeStr);
                lvi.SubItems.Add(h.Sender);
                lvi.SubItems.Add(h.Title);
                lvi.SubItems.Add(h.Channel);
                lvi.SubItems.Add(h.Status);
                if (h.Status == "실패") lvi.ForeColor = Color.Red;
                lvHistory.Items.Add(lvi);
            }
            lvHistory.EndUpdate();
        }

        private void RefreshBadge()
        {
            if (Storage.Config.IsActive)
            {
                lblStatusBadge.Text = "● 모바일 전달 활성화 중";
                lblStatusBadge.ForeColor = Color.FromArgb(52, 211, 153);
            }
            else
            {
                lblStatusBadge.Text = "■ 모바일 전달 일시정지됨";
                lblStatusBadge.ForeColor = Color.FromArgb(248, 113, 113);
            }
            lblStatSummary.Text = string.Format("📊 누적 전달: {0}건 | 실패: {1}건 | 상태 정상", Storage.Config.TotalSentCount, Storage.Config.TotalFailCount);
        }

        private void OnTestTelegram(object sender, EventArgs e)
        {
            string token = txtTelegramToken.Text.Trim();
            string chatId = txtTelegramChatId.Text.Trim();
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId))
            {
                MessageBox.Show("텔레그램 Bot Token과 Chat ID를 모두 입력해주세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnTestTelegram.Enabled = false;
            btnTestTelegram.Text = "전송 중...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                string testMsg = string.Format("🔔 <b>[CoolNotifier 텔레그램 연동 성공!]</b>\n\n선생님, 쿨메신저 모바일 알림 연동이 성공적으로 완료되었습니다.\n수신 시각: {0:yyyy-MM-dd HH:mm:ss}\n제작: 수지샘 (lemrlog@gmail.com)", DateTime.Now);
                bool ok = MessageForwarder.SendTelegram(token, chatId, testMsg, out err);
                this.BeginInvoke((MethodInvoker)delegate
                {
                    btnTestTelegram.Enabled = true;
                    btnTestTelegram.Text = "🔔 텔레그램 테스트 전송";
                    if (ok)
                    {
                        MessageBox.Show("스마트폰 텔레그램으로 테스트 메시지가 성공적으로 전송되었습니다! 📱✨", "테스트 성공", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("전송 실패:\n" + err + "\n\nToken과 Chat ID가 올바른지 확인해주세요.", "테스트 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            });
        }

        private void OnTestDiscord(object sender, EventArgs e)
        {
            string url = txtDiscordWebhook.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("디스코드 Webhook URL을 입력해주세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnTestDiscord.Enabled = false;
            btnTestDiscord.Text = "전송 중...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                bool ok = MessageForwarder.SendDiscord(url, "CoolNotifier 디스코드 연동 테스트", "수지샘 (테스트)", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "디스코드 웹훅 알림이 성공적으로 연결되었습니다!", "테스트안내문.hwp", out err);
                this.BeginInvoke((MethodInvoker)delegate
                {
                    btnTestDiscord.Enabled = true;
                    btnTestDiscord.Text = "🔔 디스코드 테스트 전송";
                    if (ok)
                    {
                        MessageBox.Show("디스코드 채널로 테스트 카드가 성공적으로 전송되었습니다! 📱✨", "테스트 성공", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("전송 실패:\n" + err + "\n\nWebhook URL이 올바른지 확인해주세요.", "테스트 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            });
        }

        private void OnSaveClicked(object sender, EventArgs e)
        {
            Storage.Config.TelegramEnabled = chkTelegram.Checked;
            Storage.Config.TelegramToken = txtTelegramToken.Text.Trim();
            Storage.Config.TelegramChatId = txtTelegramChatId.Text.Trim();

            Storage.Config.DiscordEnabled = chkDiscord.Checked;
            Storage.Config.DiscordWebhook = txtDiscordWebhook.Text.Trim();

            if (rdoModeFull.Checked) Storage.Config.NotificationMode = 0;
            else if (rdoModeSafe.Checked) Storage.Config.NotificationMode = 1;
            else if (rdoModeSimple.Checked) Storage.Config.NotificationMode = 2;

            Storage.Config.KeywordFilter = txtKeywords.Text.Trim();
            Storage.Config.DndEnabled = chkDnd.Checked;
            Storage.Config.DndStart = txtDndStart.Text.Trim();
            Storage.Config.DndEnd = txtDndEnd.Text.Trim();
            Storage.Config.DndWeekend = chkDndWeekend.Checked;

            Storage.SaveConfig();
            RefreshBadge();
            MessageBox.Show("설정이 안전하게 저장되었습니다! 👍", "저장 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem menuOpen = new ToolStripMenuItem("대시보드 열기", null, delegate { this.Show(); this.WindowState = FormWindowState.Normal; this.BringToFront(); });
            ToolStripMenuItem menuToggle = new ToolStripMenuItem("모바일 전달 일시정지/재개", null, delegate { Storage.Config.IsActive = !Storage.Config.IsActive; Storage.SaveConfig(); chkActive.Checked = Storage.Config.IsActive; RefreshBadge(); });
            ToolStripMenuItem menuUpdate = new ToolStripMenuItem("최신 버전 업데이트 확인...", null, delegate { AutoUpdater.CheckForUpdates(true, this); });
            ToolStripMenuItem menuExit = new ToolStripMenuItem("종료", null, delegate { isExiting = true; Application.Exit(); });

            trayMenu.Items.Add(menuOpen);
            trayMenu.Items.Add(menuToggle);
            trayMenu.Items.Add(menuUpdate);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(menuExit);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "CoolNotifier - 쿨메신저 모바일 알림";
            try { trayIcon.Icon = this.Icon; } catch { }
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { this.Show(); this.WindowState = FormWindowState.Normal; this.BringToFront(); };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !isExiting)
            {
                e.Cancel = true;
                this.Hide();
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(1200, "CoolNotifier", "작업표시줄 트레이로 숨겨졌습니다.\n새 쪽지 수신 시 스마트폰으로 자동 전달합니다.", ToolTipIcon.Info);
                }
                return;
            }
            isStopping = true;
            base.OnFormClosing(e);
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key != null) return key.GetValue(AppName) != null;
                }
            }
            catch { }
            return false;
        }

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null)
                    {
                        if (chkAutoStart.Checked) key.SetValue(AppName, Application.ExecutablePath);
                        else key.DeleteValue(AppName, false);
                    }
                }
            }
            catch { }
        }

        #region Watcher Loop
        private void StartWatcher()
        {
            watcherThread = new Thread(WatcherProc);
            watcherThread.IsBackground = true;
            watcherThread.Start();
        }

        private void WatcherProc()
        {
            Logger.Log("SQLite 쪽지 감시 스레드 기동됨");

            while (!isStopping)
            {
                try
                {
                    if (Storage.Config.IsActive)
                    {
                        CheckNewMessages();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("CheckNewMessages 오류: " + ex.Message);
                }

                Thread.Sleep(1500);
            }
        }

        private string FindActiveUdbPath()
        {
            if (!string.IsNullOrEmpty(Storage.Config.DatabasePath) && File.Exists(Storage.Config.DatabasePath))
            {
                return Storage.Config.DatabasePath;
            }

            string memoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoolMessenger", "Memo");
            if (!Directory.Exists(memoDir)) return "";

            string[] udbFiles = Directory.GetFiles(memoDir, "*.udb");
            string best = "";
            DateTime latest = DateTime.MinValue;
            foreach (string f in udbFiles)
            {
                if (!f.EndsWith(".udb", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime t = File.GetLastWriteTime(f);
                if (t > latest)
                {
                    latest = t;
                    best = f;
                }
            }
            return best;
        }

        private void CheckNewMessages()
        {
            string udbPath = FindActiveUdbPath();
            if (string.IsNullOrEmpty(udbPath) || !File.Exists(udbPath)) return;

            byte[] utf8Path = Encoding.UTF8.GetBytes(udbPath + "\0");
            IntPtr db;
            int rc = WinSqlite.sqlite3_open_v2(utf8Path, out db, WinSqlite.SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != 0) return;

            WinSqlite.sqlite3_busy_timeout(db, 3000);

            try
            {
                // First time init: avoid spamming all historical messages
                if (Storage.Config.LastProcessedKey <= 0)
                {
                    string initSql = "SELECT MAX(MessageKey) FROM tbl_recv;";
                    IntPtr initStmt;
                    if (WinSqlite.sqlite3_prepare16_v2(db, initSql, -1, out initStmt, IntPtr.Zero) == 0)
                    {
                        if (WinSqlite.sqlite3_step(initStmt) == WinSqlite.SQLITE_ROW)
                        {
                            Storage.Config.LastProcessedKey = WinSqlite.sqlite3_column_int64(initStmt, 0);
                            Storage.SaveConfig();
                            Logger.Log("최초 기동: 기준 MessageKey = " + Storage.Config.LastProcessedKey);
                        }
                        WinSqlite.sqlite3_finalize(initStmt);
                    }
                    return;
                }

                // Query new messages
                string query = string.Format("SELECT MessageKey, Title, Sender, ReceiveDate, MessageText, FilePath FROM tbl_recv WHERE MessageKey > {0} ORDER BY MessageKey ASC;", Storage.Config.LastProcessedKey);
                IntPtr stmt;
                if (WinSqlite.sqlite3_prepare16_v2(db, query, -1, out stmt, IntPtr.Zero) != 0) return;

                var newItems = new List<NewMessageItem>();
                while (WinSqlite.sqlite3_step(stmt) == WinSqlite.SQLITE_ROW)
                {
                    long key = WinSqlite.sqlite3_column_int64(stmt, 0);
                    string title = Marshal.PtrToStringUni(WinSqlite.sqlite3_column_text16(stmt, 1)) ?? "";
                    string sender = Marshal.PtrToStringUni(WinSqlite.sqlite3_column_text16(stmt, 2)) ?? "";
                    string date = Marshal.PtrToStringUni(WinSqlite.sqlite3_column_text16(stmt, 3)) ?? "";
                    string body = Marshal.PtrToStringUni(WinSqlite.sqlite3_column_text16(stmt, 4)) ?? "";
                    string attach = Marshal.PtrToStringUni(WinSqlite.sqlite3_column_text16(stmt, 5)) ?? "";

                    newItems.Add(new NewMessageItem
                    {
                        Key = key,
                        Title = title,
                        Sender = sender,
                        Date = date,
                        Body = body,
                        Attach = attach
                    });
                }
                WinSqlite.sqlite3_finalize(stmt);

                // Process found messages
                foreach (var item in newItems)
                {
                    bool ok = ProcessAndForward(item);
                    if (ok)
                    {
                        Storage.Config.LastProcessedKey = Math.Max(Storage.Config.LastProcessedKey, item.Key);
                        Storage.SaveConfig();
                    }
                    else
                    {
                        Logger.Log(string.Format("전송 일시 실패(네트워크 오류 등)로 인해 Key={0} 재시도를 위해 대기합니다.", item.Key));
                        break;
                    }
                }
            }
            finally
            {
                WinSqlite.sqlite3_close(db);
            }
        }

        private static string CleanAttachmentString(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            try
            {
                var matches = Regex.Matches(raw, @"([^\\/|:*?""<>]+?\.(?:hwp|hwpx|pdf|xlsx?|docx?|pptx?|zip|png|jpe?g|txt|csv|mp[34]|cell|show))", RegexOptions.IgnoreCase);
                if (matches.Count == 0) return "";
                var list = new List<string>();
                foreach (Match m in matches)
                {
                    string fn = m.Groups[1].Value.Trim();
                    if (!list.Contains(fn)) list.Add(fn);
                }
                return string.Join(", ", list.ToArray());
            }
            catch
            {
                return "";
            }
        }

        private bool ProcessAndForward(NewMessageItem msg)
        {
            Logger.Log(string.Format("새 쪽지 감지! Key={0}, Sender={1}, Title={2}", msg.Key, msg.Sender, msg.Title));

            // 1. Check DND
            if (Storage.Config.DndEnabled)
            {
                DateTime now = DateTime.Now;
                if (Storage.Config.DndWeekend && (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday))
                {
                    Logger.Log("주말 방해금지 시간대로 인해 전송 생략");
                    return true;
                }
                TimeSpan curTime = now.TimeOfDay;
                TimeSpan tStart, tEnd;
                if (TimeSpan.TryParse(Storage.Config.DndStart, out tStart) && TimeSpan.TryParse(Storage.Config.DndEnd, out tEnd))
                {
                    bool isDnd = false;
                    if (tStart <= tEnd) isDnd = (curTime >= tStart && curTime <= tEnd);
                    else isDnd = (curTime >= tStart || curTime <= tEnd); // Over midnight e.g. 18:00 ~ 08:30

                    if (isDnd)
                    {
                        Logger.Log("방해금지 시간대로 인해 전송 생략 (" + Storage.Config.DndStart + " ~ " + Storage.Config.DndEnd + ")");
                        return true;
                    }
                }
            }

            // 2. Check Keyword Filter
            if (!string.IsNullOrEmpty(Storage.Config.KeywordFilter))
            {
                string[] kws = Storage.Config.KeywordFilter.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (kws.Length > 0)
                {
                    bool matched = false;
                    string fullContent = (msg.Title + " " + msg.Body + " " + msg.Sender).ToLower();
                    foreach (string kw in kws)
                    {
                        if (fullContent.Contains(kw.Trim().ToLower()))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        Logger.Log("키워드 필터 불일치로 전송 생략");
                        return true;
                    }
                }
            }

            // 3. Construct Telegram Message
            string teleText = "";
            string cleanTitle = WebUtility.HtmlEncode(msg.Title);
            string cleanSender = WebUtility.HtmlEncode(msg.Sender);
            string cleanDate = WebUtility.HtmlEncode(msg.Date);
            string cleanBody = WebUtility.HtmlEncode(msg.Body);
            string cleanAttach = CleanAttachmentString(msg.Attach);
            string cleanAttachHtml = WebUtility.HtmlEncode(cleanAttach);

            if (Storage.Config.NotificationMode == 0) // Full
            {
                var sb = new StringBuilder();
                sb.AppendLine("🔔 <b>[쿨메신저 새 쪽지]</b>");
                sb.AppendLine(string.Format("👤 <b>보낸이:</b> {0}", cleanSender));
                sb.AppendLine(string.Format("📅 <b>일시:</b> {0}", cleanDate));
                sb.AppendLine(string.Format("📌 <b>제목:</b> {0}", cleanTitle));
                if (!string.IsNullOrEmpty(cleanAttachHtml)) sb.AppendLine(string.Format("📎 <b>첨부:</b> {0}", cleanAttachHtml));
                sb.AppendLine();
                sb.AppendLine("💬 <b>내용:</b>");
                sb.AppendLine(cleanBody);
                teleText = sb.ToString();
            }
            else if (Storage.Config.NotificationMode == 1) // Safe summary
            {
                var sb = new StringBuilder();
                sb.AppendLine("🔔 <b>[쿨메신저 새 쪽지 (안심 요약)]</b>");
                sb.AppendLine(string.Format("👤 <b>보낸이:</b> {0}", cleanSender));
                sb.AppendLine(string.Format("📅 <b>일시:</b> {0}", cleanDate));
                sb.AppendLine(string.Format("📌 <b>제목:</b> {0}", cleanTitle));
                if (!string.IsNullOrEmpty(cleanAttachHtml)) sb.AppendLine(string.Format("📎 <b>첨부:</b> {0}", cleanAttachHtml));
                teleText = sb.ToString();
            }
            else // Simple
            {
                teleText = string.Format("🔔 <b>쿨메신저 쪽지 도착!</b>\n👤 보낸이: {0}\n📅 {1}", cleanSender, cleanDate);
            }

            bool telegramAttempted = Storage.Config.TelegramEnabled && !string.IsNullOrEmpty(Storage.Config.TelegramToken) && !string.IsNullOrEmpty(Storage.Config.TelegramChatId);
            bool discordAttempted = Storage.Config.DiscordEnabled && !string.IsNullOrEmpty(Storage.Config.DiscordWebhook);
            bool anyAttempted = telegramAttempted || discordAttempted;
            bool anySuccess = false;

            // 4. Send to Telegram
            if (telegramAttempted)
            {
                string err;
                bool ok = MessageForwarder.SendTelegram(Storage.Config.TelegramToken, Storage.Config.TelegramChatId, teleText, out err);
                Storage.AddHistory(msg.Sender, msg.Title, "Telegram", ok ? "성공" : "실패");
                if (ok) { Storage.Config.TotalSentCount++; anySuccess = true; }
                else { Storage.Config.TotalFailCount++; Logger.Log("Telegram 전송 실패: " + err); }
            }

            // 5. Send to Discord
            if (discordAttempted)
            {
                string discBody = (Storage.Config.NotificationMode == 0) ? msg.Body : (Storage.Config.NotificationMode == 1 ? "(안심 모드로 본문 생략됨)" : "(단순 알림 모드)");
                string err;
                bool ok = MessageForwarder.SendDiscord(Storage.Config.DiscordWebhook, msg.Title, msg.Sender, msg.Date, discBody, cleanAttach, out err);
                Storage.AddHistory(msg.Sender, msg.Title, "Discord", ok ? "성공" : "실패");
                if (ok) { Storage.Config.TotalSentCount++; anySuccess = true; }
                else { Storage.Config.TotalFailCount++; Logger.Log("Discord 전송 실패: " + err); }
            }

            // Update UI on main thread
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    RefreshBadge();
                    RefreshHistoryUI();
                });
            }

            return !anyAttempted || anySuccess;
        }
        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                isStopping = true;
                if (trayIcon != null) trayIcon.Dispose();
                if (trayMenu != null) trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public class NewMessageItem
    {
        public long Key;
        public string Title;
        public string Sender;
        public string Date;
        public string Body;
        public string Attach;
    }
    #endregion

    #region Auto-Updater System
    public static class AutoUpdater
    {
        public static string CurrentVersion = "1.0.0";
        public static string AppDisplayName = "CoolNotifier";
        public static string DefaultCheckUrl = "https://raw.githubusercontent.com/gssg100/CoolSuite/main/updates/coolnotifier.json";

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
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                    using (var client = new WebClient())
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
                            MessageBox.Show(parent, "업데이트 서버에 연결할 수 없거나 아직 배포 전입니다.\n\n주소: " + GetCheckUrl() + "\n상세: " + ex.Message, AppDisplayName + " 업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            UpdateDialog dlg = new UpdateDialog(remoteVer, downloadUrl, changelog);
            dlg.ShowDialog(parent);
        }
    }

    public class UpdateDialog : Form
    {
        private string remoteVer;
        private string downloadUrl;
        private ProgressBar progressBar;
        private Button btnUpdate;
        private Button btnLater;
        private Label lblStatus;

        public UpdateDialog(string remoteVer, string downloadUrl, string changelog)
        {
            this.remoteVer = remoteVer;
            this.downloadUrl = downloadUrl;

            this.Text = "새로운 버전 업데이트 알림";
            this.Size = new Size(450, 350);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(248, 249, 250);

            Label lblTitle = new Label();
            lblTitle.Text = string.Format("🚀 CoolNotifier 새 버전(v{0})이 출시되었습니다!", remoteVer);
            lblTitle.Font = new Font("Malgun Gothic", 11F, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(30, 41, 59);
            lblTitle.Location = new Point(20, 20);
            lblTitle.AutoSize = true;
            this.Controls.Add(lblTitle);

            Label lblInfo = new Label();
            lblInfo.Text = "지금 업데이트 버튼을 누르면 최신 파일로 자동 교체 후 다시 실행됩니다.";
            lblInfo.Font = new Font("Malgun Gothic", 9F);
            lblInfo.ForeColor = Color.FromArgb(100, 116, 139);
            lblInfo.Location = new Point(22, 48);
            lblInfo.AutoSize = true;
            this.Controls.Add(lblInfo);

            Label lblChangelogTitle = new Label();
            lblChangelogTitle.Text = "[주요 업데이트 내용]";
            lblChangelogTitle.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            lblChangelogTitle.Location = new Point(20, 80);
            lblChangelogTitle.AutoSize = true;
            this.Controls.Add(lblChangelogTitle);

            TextBox txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Text = string.IsNullOrEmpty(changelog) ? "- 안정성 향상 및 최신 쿨메신저 호환성 개선" : changelog;
            txtLog.Location = new Point(20, 105);
            txtLog.Size = new Size(395, 110);
            txtLog.BackColor = Color.White;
            this.Controls.Add(txtLog);

            progressBar = new ProgressBar();
            progressBar.Location = new Point(20, 225);
            progressBar.Size = new Size(395, 20);
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.Visible = false;
            this.Controls.Add(progressBar);

            lblStatus = new Label();
            lblStatus.Text = "";
            lblStatus.Font = new Font("Malgun Gothic", 8.5F);
            lblStatus.ForeColor = Color.FromArgb(100, 116, 139);
            lblStatus.Location = new Point(20, 222);
            lblStatus.AutoSize = true;
            this.Controls.Add(lblStatus);

            btnLater = new Button();
            btnLater.Text = "나중에 하기";
            btnLater.Font = new Font("Malgun Gothic", 9F);
            btnLater.Location = new Point(330, 265);
            btnLater.Size = new Size(85, 32);
            btnLater.Click += delegate { this.Close(); };
            this.Controls.Add(btnLater);

            btnUpdate = new Button();
            btnUpdate.Text = "지금 업데이트 🚀";
            btnUpdate.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            btnUpdate.Location = new Point(190, 265);
            btnUpdate.Size = new Size(130, 32);
            btnUpdate.BackColor = Color.FromArgb(79, 70, 229);
            btnUpdate.ForeColor = Color.White;
            btnUpdate.FlatStyle = FlatStyle.Flat;
            btnUpdate.Click += OnStartUpdate;
            this.Controls.Add(btnUpdate);
        }

        private void OnStartUpdate(object sender, EventArgs e)
        {
            btnUpdate.Enabled = false;
            btnLater.Enabled = false;
            progressBar.Visible = true;
            lblStatus.Text = "최신 버전을 다운로드하고 있습니다...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string currentExe = Application.ExecutablePath;
                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string newExePath = Path.Combine(appDir, "CoolNotifier_update.exe");

                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                    using (var client = new WebClient())
                    {
                        client.DownloadFile(downloadUrl, newExePath);
                    }

                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        lblStatus.Text = "업데이트 파일을 교체하고 다시 실행합니다...";
                        ExecuteReplacementAndRestart(currentExe, newExePath);
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
                string appDir = Path.GetDirectoryName(currentExe);
                string batPath = Path.Combine(appDir, "apply_update.bat");
                string currentExeName = Path.GetFileName(currentExe);
                string newExeName = Path.GetFileName(newExe);

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("timeout /t 1 /nobreak > nul");
                sb.AppendLine(string.Format("move /y \"{0}\" \"{1}\" > nul", newExeName, currentExeName));
                sb.AppendLine(string.Format("start \"\" \"{0}\"", currentExeName));
                sb.AppendLine("del \"%~f0\" > nul");

                File.WriteAllText(batPath, sb.ToString(), Encoding.Default);

                var psi = new ProcessStartInfo();
                psi.FileName = batPath;
                psi.WorkingDirectory = appDir;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);

                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("업데이트 교체 스크립트 실행 실패: " + ex.Message);
            }
        }
    }
    #endregion
}
