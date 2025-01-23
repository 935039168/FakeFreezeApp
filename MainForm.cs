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

        // �����߳�ִ��״̬����ֹ˯�߻����
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);

        // ע��/ȡ��ע����ջỰ������Ϣ
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        // ��������
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // ���̹��ӻص�ί������
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        // ���ӻص�����
        private static HookProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        // �������
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        // �� unlockPassword ��Ϊ private static���Ա���Loadʱ��ע����ȡ
        private static string unlockPassword = "111111";
        private static bool isUnlocked = false;           // ��ǵ�ǰ�Ƿ��ѽ���

        // Ӧ���������ڿ���������
        private const string appName = "FakeFreezeApp";

        // ������ʵ������
        private static MainForm instance;

        // ���ڳ���ά��BlockInput�빳�ӵĶ�ʱ��
        private static System.Threading.Timer keepBlockingTimer;

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
            // �����ʼʱ����
            this.Hide();
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;

            // ע��Ự������Ϣ֪ͨ
            WTSRegisterSessionNotification(this.Handle, NOTIFY_FOR_THIS_SESSION);

            // ��ȡע����еĽ������루������ڣ�
            LoadPasswordFromRegistry();

            // ������Ҫֱ�ӻ��ӳ���������
            await Task.Delay(1000);
            // StartFakeFreeze();
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            // �˳�ʱ����ֹͣ������ȡ��ע��Ự֪ͨ
            StopFakeFreeze();
            WTSUnRegisterSessionNotification(this.Handle);
        }
        #endregion

        #region ע����ȡ�����߼�

        /// <summary>
        /// ��ע��� HKEY_CURRENT_USER\Software\FakeFreezeApp\Settings �´洢��������
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
        /// ��ע��� HKEY_CURRENT_USER\Software\FakeFreezeApp\Settings ��ȡ��������
        /// ��������ڣ���ʹ��Ĭ������ "111111"
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

            // �����ȡʧ�ܻ򲻴��ڣ���ʹ��Ĭ������
            unlockPassword = "111111";
        }

        #endregion

        #region ����ͼ����˵��¼�
        private void InitializeTrayIcon()
        {
            // ����ͼ��
            notifyIconApp.Icon = SystemIcons.Shield;
            notifyIconApp.Text = "�׿��ػ�";
            notifyIconApp.Visible = true;

            // �����Ҽ��˵�
            ContextMenuStrip contextMenuTray = new ContextMenuStrip();

            ToolStripMenuItem menuItemStart = new ToolStripMenuItem("��ʼ�ػ�");
            menuItemStart.Click += menuItemStart_Click;

            ToolStripMenuItem menuItemConfig = new ToolStripMenuItem("��������");
            menuItemConfig.Click += menuItemConfig_Click;

            ToolStripMenuItem menuItemStartup = new ToolStripMenuItem("��������");
            menuItemStartup.Click += menuItemStartup_Click;

            ToolStripMenuItem menuItemExit = new ToolStripMenuItem("�˳�");
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
            ShowAutoCloseMessageBox("�ػ�ģʽ��������", "��ʾ", 500);
        }

        private void menuItemConfig_Click(object sender, EventArgs e)
        {
            // ������������û������µĽ�������
            string newPassword = Microsoft.VisualBasic.Interaction.InputBox(
                "�������µĽ������룺", "��������", unlockPassword);

            if (!string.IsNullOrEmpty(newPassword))
            {
                unlockPassword = newPassword;
                // ���浽ע���
                SavePasswordToRegistry(newPassword);

                MessageBox.Show("�����Ѹ��£�");
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

                // ���ԭ��û�и�ֵ��������
                if (key.GetValue(appName) == null)
                {
                    key.SetValue(appName, Application.ExecutablePath);
                    MessageBox.Show("�ѳɹ�����Ϊ����������");
                }
                else
                {
                    // ���ԭ���и�ֵ����ɾ������ʾȡ����������
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

        #region ����״̬����
        /// <summary>
        /// ��������ģʽ
        /// </summary>
        private static void StartFakeFreeze()
        {
            // ÿ������ǰ��ȷ����������գ������������
            lock (inputBuffer)
            {
                inputBuffer.Clear();
            }
            // ���ý���״̬
            isUnlocked = false;

            // �����û�����
            BlockInput(true);

            // �����߳�ִ��״̬����ֹϵͳ���ߺ͹ر���ʾ��
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

            // ��װ���̹��ӣ����ڲ����������
            HookKeyboard();

            // �������������
            DisableTaskManager();

            // ���� Ctrl + Alt + Del
            DisableCtrlAltDel();

            // ���� Win ��
            DisableWinKey();

            // ������ʱ���������ָ���������ֹCtrl+Alt+Del+ESC���µ��Զ�������
            StartKeepBlockingTimer();
        }

        /// <summary>
        /// ֹͣ����ģʽ
        /// </summary>
        private static void StopFakeFreeze()
        {
            // ֹͣ��ʱ�����������BlockInput(true)
            StopKeepBlockingTimer();

            // �����������
            BlockInput(false);

            // ж�ؼ��̹���
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }

            // �ָ��߳�ִ��״̬
            SetThreadExecutionState(ES_CONTINUOUS);

            // �ָ����������
            EnableTaskManager();

            // �ָ� Ctrl + Alt + Del
            EnableCtrlAltDel();

            // �ָ� Win ��
            EnableWinKey();
        }
        #endregion

        #region ���ּ����Ķ�ʱ���߼�
        /// <summary>
        /// ����һ����ʱ����ÿ��һС��ʱ�䣬���µ��� BlockInput(true) ����ֹ��ϵͳ����
        /// �������̹��ӣ��綪ʧ�����°�װ
        /// </summary>
        private static void StartKeepBlockingTimer()
        {
            // ����Ѿ��ڼ����У���ֹͣ�ɶ�ʱ�������������
            StopKeepBlockingTimer();

            // ������ 300 �� 500 ���붼����
            keepBlockingTimer = new System.Threading.Timer(KeepBlockingCallback, null, 0, 500);
        }

        /// <summary>
        /// ֹͣ��ʱ�������ٷ�������BlockInput
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
        /// ��ʱ���ص�������������ڡ�������״̬�����ٴε���BlockInput(true)�������̹���
        /// </summary>
        private static void KeepBlockingCallback(object state)
        {
            if (!isUnlocked)
            {
                // ������������
                BlockInput(true);

                // ������Ӷ�ʧ�������°�װ����ʱ���°�ȫ��ϼ����ܵ��¹���ʧЧ��
                if (_hookID == IntPtr.Zero)
                {
                    HookKeyboard();
                }
            }
            else
            {
                // һ��������������������ֹͣ��ʱ��
                StopKeepBlockingTimer();
            }
        }
        #endregion

        #region ���̹����߼�
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
            // nCode >= 0 && wParam == 0x100 (WM_KEYDOWN)
            if (nCode >= 0 && (int)wParam == 0x100)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                char pressedChar = (char)vkCode;

                lock (inputBuffer)
                {
                    inputBuffer.Append(pressedChar);
                    Console.WriteLine($"��ǰ����: {inputBuffer}");

                    // ����������������������δ����
                    if (!isUnlocked &&
                        inputBuffer.ToString().ToLower().Contains(unlockPassword.ToLower()))
                    {
                        isUnlocked = true;  // ����ѽ���
                        StopFakeFreeze();   // ���ý������

                        // ��ջ��壬������һ���������󴥷�
                        inputBuffer.Clear();
                    }

                    // �����������ȳ������볤�ȣ���ֹ��������
                    if (inputBuffer.Length > unlockPassword.Length)
                    {
                        inputBuffer.Clear();
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }
        #endregion

        #region �����������Ctrl+Alt+Del��Win������/����
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

                // ������Щֵ���ø������롢�����������ע����ѡ���޷�����ʹ��
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
                            // ����SendKeys�Զ����ͻس����ر�MessageBox
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
            const int WM_WTSSESSION_CHANGE = 0x02B1;  // �Ự״̬������Ϣ
            const int WTS_SESSION_UNLOCK = 0x8;       // �Ự����
            const int WTS_SESSION_LOCK = 0x7;         // �Ự����

            base.WndProc(ref m);

            if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int eventId = m.WParam.ToInt32();

                if (eventId == WTS_SESSION_LOCK)
                {
                    // �û���������ʱ��ֹͣ�����������������汻����
                    StopFakeFreeze();
                }
                else if (eventId == WTS_SESSION_UNLOCK)
                {
                    // �������ٴ���������
                    StartFakeFreeze();
                }
            }
        }
        #endregion
    }
}
