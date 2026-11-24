using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Desklink
{
    public static class Certificates
    {
        public static X509Certificate2 ForAddress(IPAddress ip, bool save)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Desklink");
            string path = Path.Combine(dir, ip.ToString() + ".tls");
            if (save && File.Exists(path))
            {
                try
                {
                    var old = Import(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
                    if (old.NotAfter > DateTime.Now.AddDays(1)) return old;
                    old.Dispose();
                }
                catch (CryptographicException) { }
            }
            using (var rsa = new RSACng(2048))
            {
                var req = new CertificateRequest("CN=" + ip, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddIpAddress(ip);
                req.CertificateExtensions.Add(san.Build());
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                var usages = new OidCollection(); usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
                using (var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(180)))
                {
                    byte[] pfx = cert.Export(X509ContentType.Pfx);
                    if (save)
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(path, ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
                    }
                    return Import(pfx);
                }
            }
        }
        static X509Certificate2 Import(byte[] pfx) { return new X509Certificate2(pfx, "", X509KeyStorageFlags.UserKeySet); }
        public static string Fingerprint(X509Certificate2 cert)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(cert.RawData)).Replace("-", " ");
        }
    }

    public class Fault : Exception
    {
        public int Code;
        public Fault(int code, string message) : base(message) { Code = code; }
    }

    public sealed class Server : IDisposable
    {
        readonly IDesktop desktop;
        readonly X509Certificate2 cert;
        readonly TcpListener listener;
        readonly object gate = new object();
        readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        readonly Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        readonly string authority;
        readonly Timer timer;
        readonly string encoderPath = VideoStream.FindEncoder();
        VideoStream video;
        bool stopped, leftDown, permitted = true, control, capturing;
        string token, pin, peer;
        DateTime pinUntil, dragUntil, sessionUntil, idleUntil, blockUntil;
        int failures;
        public string Url { get { return "https://" + authority; } }
        public string Fingerprint { get { return Certificates.Fingerprint(cert); } }
        public string Peer { get { lock (gate) return peer; } }
        public string Pin { get { lock (gate) return DateTime.UtcNow < pinUntil ? pin : null; } }
        public int PinSeconds { get { lock (gate) return Math.Max(0, (int)(pinUntil - DateTime.UtcNow).TotalSeconds); } }

        public Server(IPAddress ip, int port, IDesktop device, X509Certificate2 certificate)
        {
            desktop = device; cert = certificate;
            foreach (string file in new[] { "index.html", "ui.css", "ui.js", "video.js", "mark.svg" })
                using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(file))
                using (var bytes = new MemoryStream()) { input.CopyTo(bytes); assets[file] = bytes.ToArray(); }
            listener = new TcpListener(ip, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(20);
            authority = listener.LocalEndpoint.ToString();
            RenewPin();
            timer = new Timer(Maintain, null, 300, 300);
            new Thread(Accept) { IsBackground = true }.Start();
        }
        static byte[] Random(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }
        public void RenewPin()
        {
            lock (gate)
            {
                if (token != null) throw new InvalidOperationException("请先断开当前平板");
                pin = (BitConverter.ToUInt32(Random(4), 0) % 100000000).ToString("D8");
                pinUntil = DateTime.UtcNow.AddMinutes(5);
                failures = 0; blockUntil = DateTime.MinValue;
            }
        }
        public void AllowControl(bool value)
        {
            lock (gate) { permitted = value; if (!value) { control = false; ReleaseMouse(); } }
        }
        public void Disconnect() { lock (gate) EndSession(); }
        void StopVideo()
        {
            if (video == null) return;
            video.Stop();
            video = null;
        }
        void EndSession() { token = peer = pin = null; control = false; ReleaseMouse(); StopVideo(); }
        void ReleaseMouse()
        {
            if (leftDown) { try { desktop.Button(false, false); } catch { } leftDown = false; }
        }
        void Maintain(object unused)
        {
            lock (gate)
            {
                if (stopped) return;
                var now = DateTime.UtcNow;
                if (leftDown && now >= dragUntil) ReleaseMouse();
                if (video != null && (now - video.LastWrite).TotalSeconds > 12) StopVideo();
                if (token != null && (now >= sessionUntil || now >= idleUntil)) EndSession();
            }
        }
        void Accept()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                lock (gate)
                {
                    if (stopped || clients.Count >= 24) { client.Close(); if (stopped) return; continue; }
                    clients.Add(client);
                }
                ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
            }
        }
        sealed class Request
        {
            public string Method, Path, Body;
            public Dictionary<string,string> Headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            public string Header(string key) { string v; return Headers.TryGetValue(key, out v) ? v : ""; }
            public string Cookie()
            {
                foreach (var s in Header("Cookie").Split(';')) if (s.Trim().StartsWith("__Host-desk=", StringComparison.Ordinal)) return s.Trim().Substring(12);
                return "";
            }
        }
        sealed class Reply
        {
            public int Code = 200;
            public string Type, Cookie;
            public byte[] Data;
            public VideoStream Video;
            public static Reply Json(object value, int code = 200)
            {
                return new Reply { Code = code, Type = "application/json; charset=utf-8", Data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)) };
            }
        }
        static Request Read(Stream stream)
        {
            var data = new List<byte>();
            while (true)
            {
                int c = stream.ReadByte();
                if (c < 0) { if (data.Count == 0) return null; throw new EndOfStreamException(); }
                data.Add((byte)c); int n = data.Count;
                if (n > 16384) throw new Fault(400, "请求头过大");
                if (n >= 4 && data[n-4] == 13 && data[n-3] == 10 && data[n-2] == 13 && c == 10) break;
            }
            string[] lines = Encoding.ASCII.GetString(data.ToArray()).Split(new[] {"\r\n"}, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || parts[2] != "HTTP/1.1") throw new Fault(400, "请求格式错误");
            var r = new Request { Method = parts[0], Path = parts[1] };
            for (int i = 1; lines[i].Length != 0; i++)
            {
                int k = lines[i].IndexOf(':');
                if (k < 1) throw new Fault(400, "请求格式错误");
                string key = lines[i].Substring(0,k);
                if (r.Headers.ContainsKey(key)) throw new Fault(400, "请求头重复");
                r.Headers.Add(key, lines[i].Substring(k+1).Trim());
            }
            int length = 0;
            if (r.Headers.ContainsKey("Transfer-Encoding") || (r.Headers.ContainsKey("Content-Length") &&
                (!int.TryParse(r.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > 12288)))
                throw new Fault(400, "请求长度无效");
            byte[] body = new byte[length];
            for (int i = 0; i < length;) { int read = stream.Read(body, i, length-i); if (read == 0) throw new EndOfStreamException(); i += read; }
            r.Body = Encoding.UTF8.GetString(body);
            return r;
        }
        void Serve(TcpClient client)
        {
            try
            {
                string remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                client.ReceiveTimeout = 12000; client.SendTimeout = 10000; client.NoDelay = true;
                using (var tls = new SslStream(client.GetStream(), false))
                {
                    tls.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
                    using (var reader = new BufferedStream(tls, 8192))
                    for (int count = 0; count < 200; count++)
                    {
                        Reply reply; bool close = count == 199;
                        try
                        {
                            var r = Read(reader); if (r == null) return;
                            close |= r.Header("Connection").Equals("close", StringComparison.OrdinalIgnoreCase);
                            reply = Route(r, remote, client);
                        }
                        catch (Fault f) { reply = Reply.Json(new { error = f.Message }, f.Code); close = true; }
                        catch (Exception) { reply = Reply.Json(new { error = "桌面暂不可用，请检查电脑是否已解锁。" }, 503); close = true; }
                        if (reply.Video != null) { WriteVideo(tls,reply.Video); return; }
                        string reason = reply.Code == 200 ? "OK" : "Request Failed";
                        string head = "HTTP/1.1 " + reply.Code + " " + reason + "\r\nContent-Type: " + reply.Type +
                            "\r\nContent-Length: " + reply.Data.Length + "\r\nConnection: " + (close ? "close" : "keep-alive") +
                            "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer" +
                            "\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'" +
                            (reply.Cookie == null ? "" : "\r\nSet-Cookie: " + reply.Cookie) + "\r\n\r\n";
                        byte[] headers = Encoding.ASCII.GetBytes(head);
                        tls.Write(headers,0,headers.Length); tls.Write(reply.Data,0,reply.Data.Length); tls.Flush();
                        if (close) return;
                    }
                }
            }
            catch (Exception) { /* Closing a browser tab or an untrusted TLS handshake ends this socket. */ }
            finally { client.Close(); lock (gate) clients.Remove(client); }
        }
        void WriteVideo(SslStream tls, VideoStream current)
        {
            try
            {
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
                tls.Write(header,0,header.Length);
                var buffer = new byte[32768];
                Stream source = current.Output;
                for (int n; (n=source.Read(buffer,0,buffer.Length)) > 0;)
                {
                    lock (gate) { if (video != current) break; }
                    tls.Write(buffer,0,n);
                    lock (gate) current.LastWrite = DateTime.UtcNow;
                }
            }
            finally
            {
                lock (gate) { if (video == current) video = null; }
                current.Dispose();
            }
        }
        Reply Route(Request r, string remote, TcpClient client)
        {
            if (r.Header("Host") != authority) throw new Fault(403, "请使用电脑显示的地址");
            if (r.Method == "GET")
            {
                string file = r.Path == "/" ? "index.html" : r.Path.TrimStart('/');
                byte[] asset;
                if (assets.TryGetValue(file, out asset)) return new Reply { Type = file.EndsWith("js") ? "text/javascript; charset=utf-8" : file.EndsWith("css") ? "text/css; charset=utf-8" : file.EndsWith("svg") ? "image/svg+xml" : "text/html; charset=utf-8", Data = asset };
            }
            else if (r.Method != "POST") throw new Fault(405, "请求方式无效");
            if (r.Method == "POST" && (r.Header("Origin") != Url || r.Header("X-Desklink") != "1" ||
                !r.Header("Content-Type").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)))
                throw new Fault(403, "请求来源不正确");
            Dictionary<string,object> args = null;
            if (r.Method == "POST")
            {
                try { args = new JavaScriptSerializer { RecursionLimit = 4 }.Deserialize<Dictionary<string,object>>(r.Body); }
                catch { throw new Fault(400, "参数无效"); }
                if (args == null) throw new Fault(400, "参数无效");
            }
            string capture = null;
            lock (gate)
            {
                if (stopped) throw new Fault(503, "共享已停止");
                if (r.Method == "POST" && r.Path == "/api/pair")
                {
                    if (token != null) throw new Fault(409, "电脑正在与另一会话连接。可在电脑端断开后重新配对。");
                    if (DateTime.UtcNow < blockUntil) throw new Fault(429, "尝试过多，请稍后重试或在电脑上换码");
                    if (pin == null || DateTime.UtcNow >= pinUntil) throw new Fault(410, "配对码已失效，请在电脑上生成新码");
                    if (Value(args,"pin") != pin)
                    {
                        if (++failures >= 5) { blockUntil = DateTime.UtcNow.AddMinutes(1); failures = 0; }
                        throw new Fault(403, "配对码不匹配，请核对电脑上的 8 位数字");
                    }
                    token = Convert.ToBase64String(Random(32)); pin = null; peer = remote;
                    sessionUntil = DateTime.UtcNow.AddHours(8); idleUntil = DateTime.UtcNow.AddMinutes(10);
                    var result = Reply.Json(new { ok = true });
                    result.Cookie = "__Host-desk=" + token + "; Path=/; Secure; HttpOnly; SameSite=Strict";
                    return result;
                }
                if (token == null || token != r.Cookie() || DateTime.UtcNow >= sessionUntil || DateTime.UtcNow >= idleUntil) throw new Fault(401, "请重新配对");
                idleUntil = DateTime.UtcNow.AddMinutes(10);
                if (r.Method == "GET" && r.Path == "/api/state")
                {
                    Rectangle b = desktop.Bounds; Point p = desktop.CursorPosition;
                    if (video != null && b != video.SourceBounds) StopVideo();
                    return Reply.Json(new { name = Environment.MachineName, width = b.Width, height = b.Height, allowed = permitted, control = control, h264 = encoderPath != null, dxgi = SingleDisplay(),
                        x = Math.Max(0,Math.Min(1,(p.X-b.X)/(double)Math.Max(1,b.Width-1))), y = Math.Max(0,Math.Min(1,(p.Y-b.Y)/(double)Math.Max(1,b.Height-1))) });
                }
                if (r.Method == "POST" && r.Path == "/api/video")
                {
                    string quality = Value(args,"quality");
                    double rate = Number(args,"fps",5,60);
                    string source = Value(args,"source");
                    bool dxgi = source == "dxgi";
                    if (source != "" && source != "gdi" && !dxgi) throw new Fault(400,"采集参数无效");
                    if (dxgi && !SingleDisplay()) throw new Fault(409,"多显示器使用主屏兼容采集");
                    if ((quality != "light" && quality != "balanced" && quality != "sharp") ||
                        (rate != 5 && rate != 10 && rate != 15 && rate != 30 && rate != 60)) throw new Fault(400,"视频参数无效");
                    if (encoderPath == null) throw new Fault(503,"未找到 FFmpeg");
                    StopVideo();
                    video = new VideoStream(encoderPath,desktop.Bounds,quality,(int)rate,dxgi,client);
                    return new Reply { Video=video };
                }
                if (r.Method == "GET" && r.Path.StartsWith("/api/frame/"))
                {
                    capture = r.Path.Substring("/api/frame/".Length);
                    if (capture != "light" && capture != "balanced" && capture != "sharp") throw new Fault(400, "画质参数无效");
                    if (capturing) throw new Fault(429, "正在获取画面");
                    capturing = true;
                }
                else if (r.Method == "POST" && r.Path == "/api/mode")
                {
                    bool want = Value(args,"mode") == "control";
                    if (want && !permitted) throw new Fault(403, "电脑已设为仅观看");
                    control = want; ReleaseMouse(); return Reply.Json(new { control = control });
                }
                else if (r.Method == "POST" && r.Path == "/api/leave")
                {
                    EndSession(); var result = Reply.Json(new { ok = true });
                    result.Cookie = "__Host-desk=; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age=0"; return result;
                }
                else if (r.Method == "POST" && r.Path == "/api/input")
                {
                    if (!permitted || !control) throw new Fault(403, "当前为仅观看模式");
                    Input(args); return Reply.Json(new { ok = true });
                }
                else throw new Fault(404, "未找到");
            }
            try { return new Reply { Type = "image/jpeg", Data = desktop.Frame(capture) }; }
            finally { lock (gate) capturing = false; }
        }
        bool SingleDisplay() { return desktop is WindowsDesktop && System.Windows.Forms.Screen.AllScreens.Length == 1; }
        static string Value(Dictionary<string,object> args, string key)
        {
            object v; return args.TryGetValue(key, out v) && v is string ? (string)v : "";
        }
        static double Number(Dictionary<string,object> args, string key, double min, double max)
        {
            object v; double n;
            if (!args.TryGetValue(key,out v) || !double.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n) ||
                double.IsNaN(n) || n < min || n > max) throw new Fault(400, "输入参数无效");
            return n;
        }
        static readonly Dictionary<string,ushort[]> Keys = new Dictionary<string,ushort[]> {
            {"enter",new ushort[]{13}}, {"back",new ushort[]{8}}, {"tab",new ushort[]{9}}, {"esc",new ushort[]{27}},
            {"left",new ushort[]{37}}, {"up",new ushort[]{38}}, {"right",new ushort[]{39}}, {"down",new ushort[]{40}},
            {"copy",new ushort[]{17,67}}, {"paste",new ushort[]{17,86}}, {"all",new ushort[]{17,65}}, {"undo",new ushort[]{17,90}},
            {"switch",new ushort[]{18,9}}, {"desktop",new ushort[]{91,68}}, {"delete",new ushort[]{46}}, {"space",new ushort[]{32}}
        };
        void Input(Dictionary<string,object> a)
        {
            string op = Value(a,"op");
            if (op == "text")
            {
                string s = Value(a,"text"); if (s.Length > 1200 || s.Length == 0) throw new Fault(400, "每次可输入 1–1200 个字符");
                desktop.Text(s); return;
            }
            if (op == "key")
            {
                ushort[] keys; if (!Keys.TryGetValue(Value(a,"key"),out keys)) throw new Fault(400, "无效按键");
                desktop.Keys(keys); return;
            }
            if (op == "wheel") { desktop.Wheel((int)Number(a,"delta",-1200,1200)); return; }
            if (op == "keep") { if (leftDown) dragUntil = DateTime.UtcNow.AddSeconds(3); return; }
            if (op == "release") { ReleaseMouse(); return; }
            if (op != "move" && op != "press" && op != "up" && op != "click" && op != "rightclick") throw new Fault(400, "未知操作");
            desktop.PointAt(Number(a,"x",0,1), Number(a,"y",0,1));
            if (op == "press") { if (!leftDown) { desktop.Button(true,false); leftDown = true; } }
            if (op == "up") ReleaseMouse();
            if (op == "click" || op == "rightclick")
            {
                ReleaseMouse(); bool right = op == "rightclick"; desktop.Button(true,right); desktop.Button(false,right);
            }
            if (leftDown) dragUntil = DateTime.UtcNow.AddSeconds(3);
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true; EndSession(); listener.Stop(); timer.Dispose();
                foreach (var c in clients) c.Close();
            }
            cert.Dispose();
        }
    }
}
