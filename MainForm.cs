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

        // 设置线程执行状态，防止睡眠或关屏
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);

        // 注册/取消注册接收会话更改消息
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        // 常量定义
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // 键盘钩子回调委托类型
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        // 钩子回调与句柄
        private static HookProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        // 解锁相关
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        // 将 unlockPassword 改为 private static，以便在Load时从注册表读取
        private static string unlockPassword = "111111";
        private static bool isUnlocked = false;           // 标记当前是否已解锁

        // 应用名，用于开机启动等
        private const string appName = "FakeFreezeApp";

        // 主窗口实例引用
        private static MainForm instance;

        // 用于持续维持BlockInput与钩子的定时器
        private static System.Threading.Timer keepBlockingTimer;

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
            // 窗体初始时隐藏
            this.Hide();
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;

            // 注册会话更改消息通知
            WTSRegisterSessionNotification(this.Handle, NOTIFY_FOR_THIS_SESSION);

            // 读取注册表中的解锁密码（如果存在）
            LoadPasswordFromRegistry();

            // 可视需要直接或延迟启动假死
            await Task.Delay(1000);
            // StartFakeFreeze();
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            // 退出时必须停止假死并取消注册会话通知
            StopFakeFreeze();
            WTSUnRegisterSessionNotification(this.Handle);
        }
        #endregion

        #region 注册表存取密码逻辑

        /// <summary>
        /// 在注册表 HKEY_CURRENT_USER\Software\FakeFreezeApp\Settings 下存储解锁密码
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
        /// 从注册表 HKEY_CURRENT_USER\Software\FakeFreezeApp\Settings 读取解锁密码
        /// 如果不存在，则使用默认密码 "111111"
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

            // 如果读取失败或不存在，则使用默认密码
            unlockPassword = "111111";
        }

        #endregion

        #region 托盘图标与菜单事件
        private void InitializeTrayIcon()
        {
            // 设置图标
            notifyIconApp.Icon = SystemIcons.Shield;
            notifyIconApp.Text = "蹲坑守护";
            notifyIconApp.Visible = true;

            // 创建右键菜单
            ContextMenuStrip contextMenuTray = new ContextMenuStrip();

            ToolStripMenuItem menuItemStart = new ToolStripMenuItem("开始守护");
            menuItemStart.Click += menuItemStart_Click;

            ToolStripMenuItem menuItemConfig = new ToolStripMenuItem("配置密码");
            menuItemConfig.Click += menuItemConfig_Click;

            ToolStripMenuItem menuItemStartup = new ToolStripMenuItem("开机启动");
            menuItemStartup.Click += menuItemStartup_Click;

            ToolStripMenuItem menuItemExit = new ToolStripMenuItem("退出");
            menuItemExit.Click += menuItemExit_Click;

            contextMenuTray.Items.Add(menuItemStart);
            contextMenuTray.Items.Add(menuItemConfig);
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
            // 弹出输入框，让用户配置新的解锁密码
            string newPassword = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入新的解锁密码：", "配置密码", unlockPassword);

            if (!string.IsNullOrEmpty(newPassword))
            {
                unlockPassword = newPassword;
                // 保存到注册表
                SavePasswordToRegistry(newPassword);

                MessageBox.Show("密码已更新！");
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

                // 如果原先没有该值，则设置
                if (key.GetValue(appName) == null)
                {
                    key.SetValue(appName, Application.ExecutablePath);
                    MessageBox.Show("已成功设置为开机启动！");
                }
                else
                {
                    // 如果原先有该值，则删除，表示取消开机启动
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

        #region 假死状态控制
        /// <summary>
        /// 启动假死模式
        /// </summary>
        private static void StartFakeFreeze()
        {
            // 每次启动前，确保缓冲区清空，避免残留输入
            lock (inputBuffer)
            {
                inputBuffer.Clear();
            }
            // 重置解锁状态
            isUnlocked = false;

            // 阻塞用户输入
            BlockInput(true);

            // 设置线程执行状态：防止系统休眠和关闭显示器
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

            // 安装键盘钩子，用于捕获解锁密码
            HookKeyboard();

            // 禁用任务管理器
            DisableTaskManager();

            // 禁用 Ctrl + Alt + Del
            DisableCtrlAltDel();

            // 禁用 Win 键
            DisableWinKey();

            // 开启定时器，持续恢复阻塞（防止Ctrl+Alt+Del+ESC导致的自动解锁）
            StartKeepBlockingTimer();
        }

        /// <summary>
        /// 停止假死模式
        /// </summary>
        private static void StopFakeFreeze()
        {
            // 停止定时器，避免继续BlockInput(true)
            StopKeepBlockingTimer();

            // 解除输入阻塞
            BlockInput(false);

            // 卸载键盘钩子
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }

            // 恢复线程执行状态
            SetThreadExecutionState(ES_CONTINUOUS);

            // 恢复任务管理器
            EnableTaskManager();

            // 恢复 Ctrl + Alt + Del
            EnableCtrlAltDel();

            // 恢复 Win 键
            EnableWinKey();
        }
        #endregion

        #region 保持假死的定时器逻辑
        /// <summary>
        /// 启动一个定时器，每隔一小段时间，重新调用 BlockInput(true) 来防止被系统解锁
        /// 并检查键盘钩子，如丢失则重新安装
        /// </summary>
        private static void StartKeepBlockingTimer()
        {
            // 如果已经在假死中，先停止旧定时器（保险起见）
            StopKeepBlockingTimer();

            // 这里用 300 或 500 毫秒都可以
            keepBlockingTimer = new System.Threading.Timer(KeepBlockingCallback, null, 0, 500);
        }

        /// <summary>
        /// 停止定时器，不再反复调用BlockInput
        /// </summary>
        private static void StopKeepBlockingTimer()
        {
            if (keepBlockingTimer != null)
            {
                keepBlockingTimer.Dispose();
                keepBlockingTimer = null;
            }
        }

        /// <summary>
        /// 定时器回调方法：如果还在“假死”状态，就再次调用BlockInput(true)并检查键盘钩子
        /// </summary>
        private static void KeepBlockingCallback(object state)
        {
            if (!isUnlocked)
            {
                // 继续阻塞输入
                BlockInput(true);

                // 如果钩子丢失，就重新安装（有时按下安全组合键可能导致钩子失效）
                if (_hookID == IntPtr.Zero)
                {
                    HookKeyboard();
                }
            }
            else
            {
                // 一旦解锁，不必再阻塞，停止定时器
                StopKeepBlockingTimer();
            }
        }
        #endregion

        #region 键盘钩子逻辑
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
            // nCode >= 0 && wParam == 0x100 (WM_KEYDOWN)
            if (nCode >= 0 && (int)wParam == 0x100)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                char pressedChar = (char)vkCode;

                lock (inputBuffer)
                {
                    inputBuffer.Append(pressedChar);
                    Console.WriteLine($"当前输入: {inputBuffer}");

                    // 如果输入包含解锁密码且尚未解锁
                    if (!isUnlocked &&
                        inputBuffer.ToString().ToLower().Contains(unlockPassword.ToLower()))
                    {
                        isUnlocked = true;  // 标记已解锁
                        StopFakeFreeze();   // 调用解除假死

                        // 清空缓冲，避免下一次输入又误触发
                        inputBuffer.Clear();
                    }

                    // 若缓冲区长度超过密码长度，防止无限增长
                    if (inputBuffer.Length > unlockPassword.Length)
                    {
                        inputBuffer.Clear();
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }
        #endregion

        #region 任务管理器、Ctrl+Alt+Del、Win键禁用/启用
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

                // 下面这些值会让更改密码、锁定计算机、注销等选项无法正常使用
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
                            // 利用SendKeys自动发送回车，关闭MessageBox
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
            const int WM_WTSSESSION_CHANGE = 0x02B1;  // 会话状态更改消息
            const int WTS_SESSION_UNLOCK = 0x8;       // 会话解锁
            const int WTS_SESSION_LOCK = 0x7;         // 会话锁定

            base.WndProc(ref m);

            if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int eventId = m.WParam.ToInt32();

                if (eventId == WTS_SESSION_LOCK)
                {
                    // 用户真正锁屏时，停止假死，避免锁屏界面被卡死
                    StopFakeFreeze();
                }
                else if (eventId == WTS_SESSION_UNLOCK)
                {
                    // 解锁后，再次启动假死
                    StartFakeFreeze();
                }
            }
        }
        #endregion
    }
}
