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
        #region Windows API/��������

        // ������ꡢ��������
        [DllImport("user32.dll")]
        public static extern bool BlockInput(bool block);

        // ��װ/ж��ȫ�ֹ���
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        // ��ȡ��ǰģ�������������ù���
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // �����߳�ִ��״̬����ֹϵͳ�����͹���
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);

        // ע��/ȡ��ע��Ự������Ϣ
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        // ��������
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // ���̹��ӻص�ί��
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static HookProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        // �������
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        private static string unlockPassword = "111111";
        private static bool isUnlocked = false;  // ��ǵ�ǰ�Ƿ��ѽ���

        // Ӧ�����
        private const string appName = "FakeFreezeApp";
        private static MainForm instance;

        // ��ʱ�������ڳ���ά����������ֹ Ctrl+Alt+Del+ESC ʱ�ⶳ��
        private static System.Threading.Timer keepBlockingTimer;

        // �������Ƿ��ڳ�����������������
        private static bool autoLockOnStartup = false;

        #endregion

        #region ���캯���봰���¼�
        public MainForm()
        {
            InitializeComponent();
            instance = this;
            InitializeTrayIcon();
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            // ����������
            this.Hide();
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;

            // ע��Ự������Ϣ֪ͨ
            WTSRegisterSessionNotification(this.Handle, NOTIFY_FOR_THIS_SESSION);

            // ��ȡע����еĽ������루������ڣ�
            LoadPasswordFromRegistry();

            // ��ȡ�Ƿ���������������������
            LoadAutoLockOnStartupFromRegistry();

            // �����ӳٻ�ֱ����������
            await Task.Delay(1000);

            // ����û���ѡ���Զ���������ִ�� StartFakeFreeze
            if (autoLockOnStartup)
            {
                StartFakeFreeze();
            }
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            // �˳�ʱ��ֹͣ������ע���Ự����
            StopFakeFreeze();
            WTSUnRegisterSessionNotification(this.Handle);
        }
        #endregion

        #region ע����ȡ - ���� & ��������ѡ��

        /// <summary>
        /// ��ע����ȡ�������룬�����������ʹ��Ĭ��"111111"
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
                MessageBox.Show("��ȡע�������ʧ�ܣ�" + ex.Message);
            }
            unlockPassword = "111111";
        }

        /// <summary>
        /// ����������뵽ע���
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
                MessageBox.Show("�������뵽ע���ʧ�ܣ�" + ex.Message);
            }
        }

        /// <summary>
        /// ��ȡ �Ƿ�����������������������
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
                            // �����ֵ���ڲ�����1���ͱ��Ϊ true
                            autoLockOnStartup = (value.ToString() == "1");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("��ȡ�Ƿ��Զ���������ʧ�ܣ�" + ex.Message);
            }

            // Ĭ�Ϲر�
            autoLockOnStartup = false;
        }

        /// <summary>
        /// ���� �Ƿ����������������������� ��ע���
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
                MessageBox.Show("�����Զ��������õ�ע���ʧ�ܣ�" + ex.Message);
            }
        }

        #endregion

        #region ����ͼ����˵��¼�
        private void InitializeTrayIcon()
        {
            notifyIconApp.Icon = SystemIcons.Shield;
            notifyIconApp.Text = "�׿��ػ�";
            notifyIconApp.Visible = true;

            ContextMenuStrip contextMenuTray = new ContextMenuStrip();

            // 1. ��ʼ�ػ�
            ToolStripMenuItem menuItemStart = new ToolStripMenuItem("��ʼ�ػ�");
            menuItemStart.Click += menuItemStart_Click;

            // 2. ��������
            ToolStripMenuItem menuItemConfig = new ToolStripMenuItem("��������");
            menuItemConfig.Click += menuItemConfig_Click;

            // 3. �������������� - ����
            ToolStripMenuItem menuItemAutoLock = new ToolStripMenuItem("��������������");
            menuItemAutoLock.Checked = autoLockOnStartup;  // ��ȡ��ǰ״̬
            menuItemAutoLock.Click += menuItemAutoLock_Click;

            // 4. ��������
            ToolStripMenuItem menuItemStartup = new ToolStripMenuItem("��������");
            menuItemStartup.Click += menuItemStartup_Click;

            // 5. �˳�
            ToolStripMenuItem menuItemExit = new ToolStripMenuItem("�˳�");
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
            ShowAutoCloseMessageBox("�ػ�ģʽ��������", "��ʾ", 500);
        }

        private void menuItemConfig_Click(object sender, EventArgs e)
        {
            string newPassword = Microsoft.VisualBasic.Interaction.InputBox(
                "�������µĽ������룺", "��������", unlockPassword);

            if (!string.IsNullOrEmpty(newPassword))
            {
                unlockPassword = newPassword;
                SavePasswordToRegistry(newPassword);
                MessageBox.Show("�����Ѹ��£�");
            }
        }

        // �������������������� ѡ��
        private void menuItemAutoLock_Click(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                // �л���ǰ��ѡ״̬
                bool newState = !menuItem.Checked;
                menuItem.Checked = newState;

                // ���浽ע���
                SaveAutoLockOnStartupToRegistry(newState);

                autoLockOnStartup = newState;

                string msg = newState ? "�����������Զ�����" : "�������������Զ�����";
                ShowAutoCloseMessageBox(msg, "��ʾ", 800);
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
                    MessageBox.Show("�޷���ע��������Ȩ�ޡ�");
                    return;
                }

                if (key.GetValue(appName) == null)
                {
                    key.SetValue(appName, Application.ExecutablePath);
                    MessageBox.Show("�ѳɹ�����Ϊ����������");
                }
                else
                {
                    key.DeleteValue(appName);
                    MessageBox.Show("��ȡ������������");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"�޸Ŀ�������ʧ��: {ex.Message}");
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

        #region ���������붨ʱ��

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

        #region ���̹���
        private static void HookKeyboard()
        {
            _hookID = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0);
            if (_hookID == IntPtr.Zero)
            {
                MessageBox.Show("���̹��Ӱ�װʧ�ܣ����Թ���ԱȨ�����У�");
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
                    Console.WriteLine($"��ǰ����: {inputBuffer}");

                    // ����Ƿ��ѽ���
                    if (!isUnlocked
                        && inputBuffer.ToString().ToLower().Contains(unlockPassword.ToLower()))
                    {
                        // 1) ����ѽ���
                        isUnlocked = true;

                        // 2) ֹͣ����
                        StopFakeFreeze();

                        // 3) ������뻺��
                        inputBuffer.Clear();

                        // 4) **�ؼ������ش˰�����Ϣ�����ٴ��ݸ���һ������ / ϵͳ**
                        return (IntPtr)1;
                    }

                    // Ϊ��ֹ�����������������������
                    if (inputBuffer.Length > unlockPassword.Length)
                    {
                        inputBuffer.Clear();
                    }
                }
            }

            // ���û��ƥ���������ԭ���߼�������Ϣ
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }
        #endregion

        #region ����/���������������Win����Ctrl+Alt+Del

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
                MessageBox.Show($"�������������ʧ��: {ex.Message}");
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
                MessageBox.Show($"�������������ʧ��: {ex.Message}");
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
                MessageBox.Show($"���� Ctrl + Alt + Del ʧ��: {ex.Message}");
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
                MessageBox.Show($"���� Ctrl + Alt + Del ʧ��: {ex.Message}");
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
                MessageBox.Show($"���� Win ��ʧ��: {ex.Message}");
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
                MessageBox.Show($"���� Win ��ʧ��: {ex.Message}");
            }
        }
        #endregion

        #region �Զ��ر���ʾ��
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

        #region �Ự�л���Ϣ����
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
                    // �û�����ʱ��ֹͣ��������ֹ�������濨��
                    StopFakeFreeze();
                }
                else if (eventId == WTS_SESSION_UNLOCK)
                {
                    // �������ٴ���������
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
