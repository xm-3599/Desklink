using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Sockets;

namespace Desklink
{
    // The encoder has no network listener. Its only output is a pipe to the paired TLS connection.
    internal sealed class VideoStream : IDisposable
    {
        readonly Process encoder;
        readonly TcpClient client;
        public readonly Rectangle SourceBounds;
        public DateTime LastWrite;

        public static string FindEncoder()
        {
            string folders = AppDomain.CurrentDomain.BaseDirectory + ";" + Environment.GetEnvironmentVariable("PATH");
            foreach (string entry in folders.Split(';'))
            {
                string dir = entry.Trim().Trim('"');
                if (dir.Length < 3 || dir[1] != ':' || (dir[2] != '\\' && dir[2] != '/')) continue;
                try
                {
                    string file = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(file)) return file;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public VideoStream(string file, Rectangle b, string quality, int fps, bool dxgi, TcpClient connection)
        {
            SourceBounds = b;
            int limit = quality == "sharp" ? 1920 : quality == "light" ? 960 : 1440;
            double scale = Math.Min(1, (double)limit / Math.Max(b.Width,b.Height));
            int w = Math.Max(2, (int)Math.Round(b.Width * scale / 2) * 2);
            int h = Math.Max(2, (int)Math.Round(b.Height * scale / 2) * 2);
            int kbps = quality == "sharp" ? 8500 : quality == "light" ? 2500 : 5000;
            if (fps == 60) kbps = kbps * 3 / 2;
            // All arguments come from the primary monitor and the server's fixed presets.
            string input = dxgi
                ? "-f lavfi -i ddagrab=output_idx=0:draw_mouse=1:framerate=" + fps + " -vf hwdownload,format=bgra,"
                : string.Format(CultureInfo.InvariantCulture,"-f gdigrab -framerate {0} -draw_mouse 1 " +
                  "-offset_x {1} -offset_y {2} -video_size {3}x{4} -i desktop -vf ",fps,b.Left,b.Top,b.Width,b.Height);
            string args = "-nostdin -hide_banner -loglevel error " + input + string.Format(CultureInfo.InvariantCulture,
                "scale={0}:{1}:flags=fast_bilinear -an -pix_fmt yuv420p -c:v libx264 " +
                "-preset ultrafast -tune zerolatency -profile:v baseline -threads 4 -g {2} -bf 0 " +
                "-b:v {3}k -maxrate {3}k -bufsize {4}k -vsync 0 -flush_packets 1 " +
                "-flvflags no_duration_filesize -f flv pipe:1",
                w,h,fps,kbps,kbps/2);
            client = connection;
            LastWrite = DateTime.UtcNow;
            var info = new ProcessStartInfo(file,args) {
                UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true
            };
            encoder = Process.Start(info);
            try { encoder.BeginErrorReadLine(); }
            catch { Dispose(); throw; }
        }
        public Stream Output { get { return encoder.StandardOutput.BaseStream; } }
        public void Stop()
        {
            client.Close();
            try { if (!encoder.HasExited) encoder.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        public void Dispose() { Stop(); encoder.Dispose(); }
    }
}
