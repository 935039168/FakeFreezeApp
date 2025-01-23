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

        // ��װ/ж��ȫ�ּ��̹���
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        // ��ȡ��ǰģ����
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // �����߳�ִ��״̬����ֹ��������
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);

        // ע��/ȡ��ע��Ự������Ϣ(��������/����)
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        // ��������
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // ���̹��ӻص�
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static HookProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        // �������
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        private static string unlockPassword = "111111";
        private static bool isUnlocked = false;

        // Ӧ�ñ�ʶ & ����������
        private const string appName = "FakeFreezeApp";
        private static MainForm instance;

        // ��ʱ��������ά�ּ���״̬
        private static System.Threading.Timer keepBlockingTimer;

        #endregion

        #region ���캯�� & �¼�

        public MainForm()
        {
            InitializeComponent();
            instance = this;

            // ��ʼ������ͼ�ꡢ�˵�
            InitializeTrayIcon();

            // 1) �ڹ��캯�����ȡ�Ƿ�"����������"
            bool autoLockOnStartup = LoadAutoLockOnStartupFromRegistry();
            if (autoLockOnStartup)
            {
                // 2) ��Ϊtrue����������������
                StartFakeFreeze();
            }
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            // ����������
            this.Hide();
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;

            // ע��Ự������Ϣ
            WTSRegisterSessionNotification(this.Handle, NOTIFY_FOR_THIS_SESSION);

            // ��ȡ�������루������ڣ�
            LoadPasswordFromRegistry();

            // ���Ը�����Ҫ�����ӳ�
            await Task.Delay(500);
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            // �˳�ʱֹͣ����
            StopFakeFreeze();
            // ע���Ự�����¼�
            WTSUnRegisterSessionNotification(this.Handle);
        }

        #endregion

        #region ע���������������� & AutoLockOnStartup

        /// <summary>
        /// ��ȡ��������
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
                MessageBox.Show("��ȡ����ʧ�ܣ�" + ex.Message);
            }
        }

        /// <summary>
        /// �����������
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
                MessageBox.Show("��������ʧ�ܣ�" + ex.Message);
            }
        }

        /// <summary>
        /// ��ȡ"�Ƿ���������"����
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
                MessageBox.Show("��ȡ�Զ�����ʧ�ܣ�" + ex.Message);
            }
            // Ĭ��Ϊ false
            return false;
        }

        /// <summary>
        /// ����"�Ƿ���������"����
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
                MessageBox.Show("�����Զ�����ʧ�ܣ�" + ex.Message);
            }
        }

        #endregion

        #region ����ͼ����˵�
        private void InitializeTrayIcon()
        {
            notifyIconApp.Icon = new Icon("shield.ico"); // �����Զ���ͼ��
            notifyIconApp.Text = "�׿��ػ�";
            notifyIconApp.Visible = true;

            notifyIconApp.MouseClick += NotifyIconApp_MouseClick;

            ContextMenuStrip menu = new ContextMenuStrip();

            // "�����ػ�"���ֶ���������
            var menuItemStart = new ToolStripMenuItem("�����ػ�");
            menuItemStart.Click += menuItemStart_Click;
            menu.Items.Add(menuItemStart);

            // "��������"
            var menuItemConfig = new ToolStripMenuItem("��������");
            menuItemConfig.Click += menuItemConfig_Click;
            menu.Items.Add(menuItemConfig);

            // "����������"
            var menuItemAutoLock = new ToolStripMenuItem("����������");
            bool autoLock = LoadAutoLockOnStartupFromRegistry();
            menuItemAutoLock.Checked = autoLock;
            menuItemAutoLock.Click += (s, e) =>
            {
                bool newVal = !menuItemAutoLock.Checked;
                menuItemAutoLock.Checked = newVal;
                SaveAutoLockOnStartupToRegistry(newVal);

                MessageBox.Show(newVal ?
                    "�´��������Զ�����" :
                    "�´����������Զ�����");
            };
            menu.Items.Add(menuItemAutoLock);

            // "��������"
            var menuItemStartup = new ToolStripMenuItem("��������");
            menuItemStartup.Click += menuItemStartup_Click;
            menu.Items.Add(menuItemStartup);

            // "�˳�"
            var menuItemExit = new ToolStripMenuItem("�˳�");
            menuItemExit.Click += menuItemExit_Click;
            menu.Items.Add(menuItemExit);

            notifyIconApp.ContextMenuStrip = menu;

            // ��鲢��ʾ"��������"�Ƿ��ѹ�ѡ
            CheckStartupStatus(menuItemStartup);
        }

        // ���"�����ػ�"
        private void menuItemStart_Click(object sender, EventArgs e)
        {
            StartFakeFreeze();
            ShowAutoCloseMessageBox("�������ػ�ģʽ��", "��ʾ", 500);
        }

        // ͨ������������"�����ػ�"
        private async void NotifyIconApp_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // �ȴ�2��
                await Task.Delay(2000);
                // ���������������ͼ�꣬������
                StartFakeFreeze();
                ShowAutoCloseMessageBox("�������ػ�ģʽ��", "��ʾ", 500);
                //ShowAutoCloseMessageBox("��ͨ�����������������ػ�ģʽ��", "��ʾ", 500);
            }
        }

        // ���"��������"
        private void menuItemConfig_Click(object sender, EventArgs e)
        {
            string newPassword = Microsoft.VisualBasic.Interaction.InputBox(
                "�������µĽ������룺",
                "��������",
                unlockPassword);

            if (!string.IsNullOrEmpty(newPassword))
            {
                unlockPassword = newPassword;
                SavePasswordToRegistry(newPassword);
                MessageBox.Show("�����Ѹ��£�");
            }
        }

        // ���"��������"
        private void menuItemStartup_Click(object sender, EventArgs e)
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);

                if (key == null)
                {
                    MessageBox.Show("�޷���ע�������Ȩ�ޡ�");
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
                MessageBox.Show("������������ʧ��: " + ex.Message);
            }
        }

        // ���"�˳�"
        private void menuItemExit_Click(object sender, EventArgs e)
        {
            StopFakeFreeze();
            Application.Exit();
        }

        // ����ע����ж�"��������"�Ƿ�ѡ
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

        #region ��������(��ʼ/ֹͣ)�붨ʱ��

        private static void StartFakeFreeze()
        {
            lock (inputBuffer)
            {
                inputBuffer.Clear();
            }
            isUnlocked = false;

            // ��������
            BlockInput(true);

            // ��ֹϵͳ����
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

            // ��װ���̹���
            HookKeyboard();

            // ���������������Ctrl+Alt+Del��Win��
            DisableTaskManager();
            DisableCtrlAltDel();
            DisableWinKey();

            // ������ʱ��������ά�ּ���
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

                // ������Ӷ�ʧ�����°�װ
                if (_hookID == IntPtr.Zero)
                {
                    HookKeyboard();
                }
            }
            else
            {
                // ������ֹͣ��ʱ
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

                    // ����������
                    if (!isUnlocked &&
                        inputBuffer.ToString().ToLower().Contains(unlockPassword.ToLower()))
                    {
                        isUnlocked = true;
                        StopFakeFreeze();
                        inputBuffer.Clear();

                        // ���ش˰�������������ַ�"©"��ǰ̨
                        return (IntPtr)1;
                    }

                    // ��ֹ��������
                    if (inputBuffer.Length > unlockPassword.Length)
                    {
                        inputBuffer.Clear();
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        #endregion

        #region ����/���� �����������Ctrl+Alt+Del��Win��

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
                MessageBox.Show($"�������������ʧ��: {ex.Message}");
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
                MessageBox.Show($"�������������ʧ��: {ex.Message}");
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
                MessageBox.Show($"���� Ctrl + Alt + Del ʧ��: {ex.Message}");
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
                MessageBox.Show($"���� Ctrl + Alt + Del ʧ��: {ex.Message}");
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
                MessageBox.Show($"���� Win ��ʧ��: {ex.Message}");
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
                MessageBox.Show($"���� Win ��ʧ��: {ex.Message}");
            }
        }

        #endregion

        #region �Զ��ر���Ϣ��

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

        #region �Ự�л�������/������
        protected override void WndProc(ref Message m)
        {
            const int WM_WTSSESSION_CHANGE = 0x02B1;
            const int WTS_SESSION_LOCK = 0x7; // �û�����
            const int WTS_SESSION_UNLOCK = 0x8; // �û�����

            base.WndProc(ref m);

            if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int eventId = m.WParam.ToInt32();

                if (eventId == WTS_SESSION_LOCK)
                {
                    // �û�����ʱֹͣ����
                    StopFakeFreeze();
                }
                else if (eventId == WTS_SESSION_UNLOCK)
                {
                    // ��������δ���������ٴ���������
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
