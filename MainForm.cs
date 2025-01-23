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

        // 安装/卸载全局钩子
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        // 获取当前模块句柄，用于设置钩子
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // 设置线程执行状态，防止系统待机和关屏
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);

        // 注册/取消注册会话更改消息
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        // 常量定义
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // 键盘钩子回调委托
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static HookProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        // 解锁相关
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        private static string unlockPassword = "111111";
        private static bool isUnlocked = false;  // 标记当前是否已解锁

        // 应用相关
        private const string appName = "FakeFreezeApp";
        private static MainForm instance;

        // 定时器，用于持续维持阻塞（防止 Ctrl+Alt+Del+ESC 时解冻）
        private static System.Threading.Timer keepBlockingTimer;

        // 新增：是否在程序启动后立即锁定
        private static bool autoLockOnStartup = false;

        #endregion

        #region 构造函数与窗体事件
        public MainForm()
        {
            InitializeComponent();
            instance = this;
            InitializeTrayIcon();
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            // 隐藏主窗体
            this.Hide();
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;

            // 注册会话更改消息通知
            WTSRegisterSessionNotification(this.Handle, NOTIFY_FOR_THIS_SESSION);

            // 读取注册表中的解锁密码（如果存在）
            LoadPasswordFromRegistry();

            // 读取是否启动后立即锁定的设置
            LoadAutoLockOnStartupFromRegistry();

            // 按需延迟或直接启动假死
            await Task.Delay(1000);

            // 如果用户勾选了自动锁定，就执行 StartFakeFreeze
            if (autoLockOnStartup)
            {
                StartFakeFreeze();
            }
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            // 退出时，停止假死并注销会话监听
            StopFakeFreeze();
            WTSUnRegisterSessionNotification(this.Handle);
        }
        #endregion

        #region 注册表存取 - 密码 & 启动锁定选项

        /// <summary>
        /// 从注册表读取解锁密码，如果不存在则使用默认"111111"
        /// </summary>
        private void LoadPasswordFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\" + appName + @"\Settings"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("UnlockPassword");
                        if (value != null)
                        {
                            unlockPassword = value.ToString();
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取注册表密码失败：" + ex.Message);
            }
            unlockPassword = "111111";
        }

        /// <summary>
        /// 保存解锁密码到注册表
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
                MessageBox.Show("保存密码到注册表失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 读取 是否“启动后立即锁定”的配置
        /// </summary>
        private void LoadAutoLockOnStartupFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\" + appName + @"\Settings"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("AutoLockOnStartup");
                        if (value != null)
                        {
                            // 如果该值存在并且是1，就标记为 true
                            autoLockOnStartup = (value.ToString() == "1");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取是否自动锁定配置失败：" + ex.Message);
            }

            // 默认关闭
            autoLockOnStartup = false;
        }

        /// <summary>
        /// 保存 是否“启动后立即锁定”的配置 到注册表
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
                MessageBox.Show("保存自动锁定配置到注册表失败：" + ex.Message);
            }
        }

        #endregion

        #region 托盘图标与菜单事件
        private void InitializeTrayIcon()
        {
            notifyIconApp.Icon = SystemIcons.Shield;
            notifyIconApp.Text = "蹲坑守护";
            notifyIconApp.Visible = true;

            ContextMenuStrip contextMenuTray = new ContextMenuStrip();

            // 1. 开始守护
            ToolStripMenuItem menuItemStart = new ToolStripMenuItem("开始守护");
            menuItemStart.Click += menuItemStart_Click;

            // 2. 配置密码
            ToolStripMenuItem menuItemConfig = new ToolStripMenuItem("配置密码");
            menuItemConfig.Click += menuItemConfig_Click;

            // 3. 启动后立即锁定 - 新增
            ToolStripMenuItem menuItemAutoLock = new ToolStripMenuItem("启动后立即锁定");
            menuItemAutoLock.Checked = autoLockOnStartup;  // 读取当前状态
            menuItemAutoLock.Click += menuItemAutoLock_Click;

            // 4. 开机启动
            ToolStripMenuItem menuItemStartup = new ToolStripMenuItem("开机启动");
            menuItemStartup.Click += menuItemStartup_Click;

            // 5. 退出
            ToolStripMenuItem menuItemExit = new ToolStripMenuItem("退出");
            menuItemExit.Click += menuItemExit_Click;

            contextMenuTray.Items.Add(menuItemStart);
            contextMenuTray.Items.Add(menuItemConfig);
            contextMenuTray.Items.Add(menuItemAutoLock);
            contextMenuTray.Items.Add(menuItemStartup);
            contextMenuTray.Items.Add(menuItemExit);

            notifyIconApp.ContextMenuStrip = contextMenuTray;

            CheckStartupStatus(menuItemStartup);
        }

        private void menuItemStart_Click(object sender, EventArgs e)
        {
            StartFakeFreeze();
            ShowAutoCloseMessageBox("守护模式已启动！", "提示", 500);
        }

        private void menuItemConfig_Click(object sender, EventArgs e)
        {
            string newPassword = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入新的解锁密码：", "配置密码", unlockPassword);

            if (!string.IsNullOrEmpty(newPassword))
            {
                unlockPassword = newPassword;
                SavePasswordToRegistry(newPassword);
                MessageBox.Show("密码已更新！");
            }
        }

        // 新增：启动后立即锁定 选项
        private void menuItemAutoLock_Click(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                // 切换当前勾选状态
                bool newState = !menuItem.Checked;
                menuItem.Checked = newState;

                // 保存到注册表
                SaveAutoLockOnStartupToRegistry(newState);

                autoLockOnStartup = newState;

                string msg = newState ? "程序启动后将自动锁定" : "程序启动后不再自动锁定";
                ShowAutoCloseMessageBox(msg, "提示", 800);
            }
        }

        private void menuItemStartup_Click(object sender, EventArgs e)
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);

                if (key == null)
                {
                    MessageBox.Show("无法打开注册表项，请检查权限。");
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
                MessageBox.Show($"修改开机启动失败: {ex.Message}");
            }
        }

        private void menuItemExit_Click(object sender, EventArgs e)
        {
            StopFakeFreeze();
            Application.Exit();
        }

        private void CheckStartupStatus(ToolStripMenuItem startupMenuItem)
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(
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

        #region 假死控制与定时器

        private static void StartFakeFreeze()
        {
            lock (inputBuffer)
            {
                inputBuffer.Clear();
            }
            isUnlocked = false;

            BlockInput(true);

            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

            HookKeyboard();

            DisableTaskManager();
            DisableCtrlAltDel();
            DisableWinKey();

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

                if (_hookID == IntPtr.Zero)
                {
                    HookKeyboard();
                }
            }
            else
            {
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

                    // 检测是否已解锁
                    if (!isUnlocked
                        && inputBuffer.ToString().ToLower().Contains(unlockPassword.ToLower()))
                    {
                        // 1) 标记已解锁
                        isUnlocked = true;

                        // 2) 停止假死
                        StopFakeFreeze();

                        // 3) 清空输入缓冲
                        inputBuffer.Clear();

                        // 4) **关键：拦截此按键消息，不再传递给下一个钩子 / 系统**
                        return (IntPtr)1;
                    }

                    // 为防止缓冲区无限增长，定期清空
                    if (inputBuffer.Length > unlockPassword.Length)
                    {
                        inputBuffer.Clear();
                    }
                }
            }

            // 如果没有匹配解锁，则按原先逻辑传递消息
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }
        #endregion

        #region 禁用/启用任务管理器、Win键、Ctrl+Alt+Del

        private static void DisableTaskManager()
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(
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
                RegistryKey key = Registry.CurrentUser.OpenSubKey(
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
                RegistryKey key = Registry.LocalMachine.OpenSubKey(
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
                RegistryKey key = Registry.LocalMachine.OpenSubKey(
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
                RegistryKey key = Registry.CurrentUser.CreateSubKey(
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
                RegistryKey key = Registry.CurrentUser.CreateSubKey(
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

        #region 自动关闭提示框
        private void ShowAutoCloseMessageBox(string message, string title, int duration)
        {
            Task.Run(() =>
            {
                DialogResult result = DialogResult.None;
                this.Invoke(new Action(() =>
                {
                    var thread = new Thread(() =>
                    {
                        result = MessageBox.Show(message, title, MessageBoxButtons.OK);
                    });

                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();

                    Task.Delay(duration).ContinueWith((t) =>
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

        #region 会话切换消息处理
        protected override void WndProc(ref Message m)
        {
            const int WM_WTSSESSION_CHANGE = 0x02B1;
            const int WTS_SESSION_UNLOCK = 0x8;
            const int WTS_SESSION_LOCK = 0x7;

            base.WndProc(ref m);

            if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int eventId = m.WParam.ToInt32();

                if (eventId == WTS_SESSION_LOCK)
                {
                    // 用户锁屏时，停止假死，防止锁屏界面卡死
                    StopFakeFreeze();
                }
                else if (eventId == WTS_SESSION_UNLOCK)
                {
                    // 解锁后，再次启动假死
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
