using System;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Desklink
{
    static class NetworkPath
    {
        // Layout through InterfaceAndOperStatusFlags in the Windows SDK's MIB_IF_ROW2.
        // The trailing allocation holds the remaining statistics written by GetIfEntry2.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 2048)]
        struct InterfaceRow
        {
            public ulong Luid;
            public uint Index;
            public Guid Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
            public uint AddressLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Address;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentAddress;
            public uint Mtu, Type, TunnelType, MediaType, Medium, AccessType, Direction;
            public byte Flags;
        }
        [DllImport("iphlpapi.dll")] static extern uint GetIfEntry2(ref InterfaceRow row);
        [DllImport("iphlpapi.dll")] static extern uint GetBestInterface(uint destination, out uint index);

        public static bool Hardware(NetworkInterface net, int index)
        {
            var row = new InterfaceRow { Index=(uint)index, Address=new byte[32], PermanentAddress=new byte[32] };
            if (GetIfEntry2(ref row) == 0) return (row.Flags & 1) != 0;
            return net.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        }
        public static string Describe(string input, int selected, string label)
        {
            IPAddress ip; string s = input.Trim();
            if (s.Split('.').Length != 4 || !IPAddress.TryParse(s, out ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                ip.ToString() != s || IPAddress.IsLoopback(ip) || ip.GetAddressBytes()[0] == 0 || ip.GetAddressBytes()[0] >= 224)
                return "请输入平板网络设置里的 IPv4 地址，例如 10.20.30.45。";
            uint index;
            if (GetBestInterface(BitConverter.ToUInt32(ip.GetAddressBytes(),0), out index) != 0)
                return "Windows 当前没有到这个地址的可用路由。请确认平板的地址及网络连接。";
            string route = "接口 " + index;
            foreach (var device in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!device.Supports(NetworkInterfaceComponent.IPv4)) continue;
                var v4 = device.GetIPProperties().GetIPv4Properties();
                if (v4 != null && v4.Index == index) { route = device.Name; break; }
            }
            return "共享网卡　" + label + "\r\n系统选路　" + route + "\r\n\r\n" +
                (selected == index ? "两者一致。请用平板打开共享地址，确认校园网允许设备互访。" :
                "两者不同。如果平板走校园网，可检查虚拟网卡软件的「允许局域网」或分流设置。如果两端都在同一虚拟网络，请选对应虚拟网卡并使用平板的虚拟地址。");
        }
        public static void ShowDialog(Form owner, int index, string label)
        {
            using (var dialog = new Form())
            {
                dialog.Text="检查平板路由"; dialog.Font=owner.Font; dialog.StartPosition=FormStartPosition.CenterParent;
                dialog.FormBorderStyle=FormBorderStyle.FixedDialog; dialog.MaximizeBox=dialog.MinimizeBox=false; dialog.ClientSize=new Size(490,278);
                var help = new Label { Text="输入平板的 IPv4 地址，查看 Windows 默认会选择哪张网卡。", Left=20, Top=18, Width=450, Height=44 };
                var box = new TextBox { Left=20, Top=66, Width=300 };
                var check = new Button { Text="检查路由", Left=334, Top=62, Width=132, Height=34 };
                var result = new Label { Left=20, Top=111, Width=450, Height=155, ForeColor=Color.FromArgb(35,68,58) };
                check.Click += delegate {
                    try { result.Text=Describe(box.Text,index,label); }
                    catch (NetworkInformationException) { result.Text="网络状态已改变，请回到主窗口刷新网卡。"; }
                };
                dialog.Controls.AddRange(new Control[] { help, box, check, result }); dialog.AcceptButton=check; dialog.ShowDialog(owner);
            }
        }
    }
}
