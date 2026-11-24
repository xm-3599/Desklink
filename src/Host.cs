using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

namespace Desklink
{
    static class Program
    {
        [STAThread] static void Main()
        {
            WindowsDesktop.SetDpiMode();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HostWindow());
        }
    }
    sealed class HostWindow : Form
    {
        readonly Color ink = Color.FromArgb(23,47,48), accent = Color.FromArgb(15,105,94);
        readonly ComboBox adapters = new ComboBox();
        readonly Label address = new Label(), code = new Label(), hint = new Label(), state = new Label();
        readonly CheckBox allow = new CheckBox();
        readonly Button start = new Button(), renew = new Button(), copy = new Button(), fingerprint = new Button(), refresh = new Button(), route = new Button();
        readonly Timer clock = new Timer();
        Server server;
        sealed class Adapter
        {
            public IPAddress Ip; public string Title, Kind; public int Priority, Index;
            public override string ToString() { return Kind + " · " + Title + "    " + Ip; }
        }
        public HostWindow()
        {
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96,96);
            Text = "同桌 · 电脑端"; Font = new Font("Microsoft YaHei UI",10); ForeColor = ink;
            BackColor = Color.FromArgb(245,248,247); FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(590,555);
            var title = LabelAt("同桌",28,18,130,53,26,true); title.ForeColor = accent;
            LabelAt("局域网远程桌面  /  电脑端",32,74,520,28,11,false);
            Add(state,367,35,190,30); state.TextAlign = ContentAlignment.MiddleRight;
            LabelAt("共享使用的网卡",32,124,235,24,10,true);
            Add(adapters,32,154,429,35); adapters.DropDownStyle = ComboBoxStyle.DropDownList;
            SetButton(refresh,"刷新",470,152,86,34);
            refresh.Click += delegate { LoadAdapters(); };
            LabelAt("在平板浏览器中打开",32,211,290,26,10,true);
            Add(address,32,244,434,42); address.Font = new Font("Segoe UI",15,FontStyle.Bold);
            SetButton(copy,"复制",470,243,86,35);
            copy.Click += delegate { try { Clipboard.SetText(server.Url); } catch { MessageBox.Show("剪贴板正忙，请手动抄录地址。"); } };
            LabelAt("一次性配对码",32,306,250,28,10,true);
            Add(code,28,335,326,62); code.Font = new Font("Consolas",29,FontStyle.Bold); code.ForeColor = accent;
            SetButton(renew,"生成新码",422,344,134,39);
            renew.Click += delegate { if (server.Peer != null) server.Disconnect(); server.RenewPin(); UpdateState(); };
            Add(hint,32,404,540,30);
            Add(allow,32,447,225,29); allow.Text = "允许平板控制键鼠"; allow.Checked = true;
            allow.CheckedChanged += delegate { if (server != null) server.AllowControl(allow.Checked); };
            SetButton(route,"检查平板路由",270,441,136,38);
            route.Click += delegate { var a=adapters.SelectedItem as Adapter; if (a != null) NetworkPath.ShowDialog(this,a.Index,a.Title); };
            SetButton(fingerprint,"证书指纹",416,441,140,38);
            fingerprint.Click += delegate { MessageBox.Show(this, "请与平板浏览器证书详情中的 SHA-256 指纹逐字核对。\n\n" + server.Fingerprint, "此电脑的证书指纹", MessageBoxButtons.OK); };
            SetButton(start,"开始共享",32,492,524,43);
            start.ForeColor = Color.White; start.BackColor = accent; start.FlatStyle = FlatStyle.Flat;
            start.Click += delegate { if (server == null) Begin(); else Stop(); };
            FormClosed += delegate { clock.Stop(); Stop(); };
            clock.Interval = 500; clock.Tick += delegate { UpdateState(); }; clock.Start();
            LoadAdapters(); UpdateState();
        }
        void Add(Control control, int x, int y, int w, int h)
        { control.SetBounds(x,y,w,h); Controls.Add(control); }
        Label LabelAt(string text, int x, int y, int w, int h, int size, bool bold)
        {
            var c = new Label { Text = text, Font = new Font("Microsoft YaHei UI",size,bold ? FontStyle.Bold : FontStyle.Regular) };
            Add(c,x,y,w,h); return c;
        }
        void SetButton(Button b, string title, int x, int y, int w, int h)
        { Add(b,x,y,w,h); b.Text = title; b.UseVisualStyleBackColor = true; b.Cursor = Cursors.Hand; }
        void LoadAdapters()
        {
            var all = new List<Adapter>();
            foreach (var net in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var uni in net.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(uni.Address)) continue;
                    var v4 = net.GetIPProperties().GetIPv4Properties();
                    if (v4 == null) continue;
                    bool physical = NetworkPath.Hardware(net,v4.Index);
                    bool wifi = net.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    all.Add(new Adapter { Ip=uni.Address, Title=net.Name, Index=v4.Index,
                        Kind=physical ? (wifi ? "无线" : "物理") : "虚拟", Priority=physical ? (wifi ? 0 : 1) : 2 });
                }
            }
            adapters.Items.Clear();
            foreach (var a in all.OrderBy(a => a.Priority)) adapters.Items.Add(a);
            if (adapters.Items.Count > 0) adapters.SelectedIndex = 0;
        }
        void Begin()
        {
            var a = adapters.SelectedItem as Adapter; if (a == null) return;
            try
            {
                UseWaitCursor = true;
                var certificate = Certificates.ForAddress(a.Ip,true);
                try { server = new Server(a.Ip,49731,new WindowsDesktop(),certificate); }
                catch { certificate.Dispose(); throw; }
                server.AllowControl(allow.Checked);
            }
            catch (Exception e) { MessageBox.Show(this,"共享启动失败。请确认网卡仍已连接、端口 49731 空闲。\n\n" + e.Message,"同桌"); }
            finally { UseWaitCursor = false; UpdateState(); }
        }
        void Stop() { if (server != null) { server.Dispose(); server = null; } if (!IsDisposed) UpdateState(); }
        void UpdateState()
        {
            bool on = server != null;
            string peer = on ? server.Peer : null, pin = on ? server.Pin : null;
            state.Text = on ? (peer == null ? "●  正在共享" : "●  平板已连接") : "○  共享已关闭";
            state.ForeColor = on ? accent : Color.DimGray;
            start.Text = on ? "停止共享" : "开始共享"; start.Enabled = on || adapters.SelectedItem != null;
            adapters.Enabled = refresh.Enabled = !on; copy.Enabled = fingerprint.Enabled = renew.Enabled = on;
            route.Enabled = adapters.SelectedItem != null;
            address.Text = on ? server.Url : "开始共享后显示地址";
            address.ForeColor = on ? ink : Color.Gray;
            code.Text = pin != null ? pin.Substring(0,4) + " " + pin.Substring(4) : "···· ····";
            renew.Text = peer == null ? "生成新码" : "断开并换码";
            hint.Text = peer != null ? "已配对平板  ·  " + peer :
                !on ? "电脑和平板需连接到可以相互访问的校园网。" :
                pin == null ? "配对码已失效，点「生成新码」即可继续。" : "有效时间  " + TimeSpan.FromSeconds(server.PinSeconds).ToString(@"m\:ss") + "  ·  配对成功后立即作废";
        }
    }
}
