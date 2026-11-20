using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

/// <summary>
/// Minimal single-session WebSocket server (RFC 6455, text frames only) on a TCP port.
/// Plain HTTP GETs on the same port serve the files in the client folder, so the browser
/// can open http://host:port/ with nothing else to install. No rooms, no auth.
/// </summary>
public class WsSignalingServer : IDisposable
{
    public enum Kind { Connected, Message, Closed }

    public readonly ConcurrentQueue<(Kind kind, string text)> Events = new ConcurrentQueue<(Kind, string)>();

    TcpListener listener;
    Thread acceptThread;
    volatile bool running;
    string clientDir;
    Stream current;
    readonly object sendLock = new object();

    public void Start(int port, string clientDirectory)
    {
        clientDir = clientDirectory;
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ws-accept" };
        acceptThread.Start();
    }

    void AcceptLoop()
    {
        while (running)
        {
            try
            {
                var c = listener.AcceptTcpClient();
                new Thread(() => Handle(c)) { IsBackground = true, Name = "ws-conn" }.Start();
            }
            catch { if (!running) break; }
        }
    }

    void Handle(TcpClient c)
    {
        c.NoDelay = true;
        Stream s = null;
        try
        {
            s = c.GetStream();
            string head = ReadHeaders(s);
            if (head == null) return;
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int k = lines[i].IndexOf(':');
                if (k > 0) headers[lines[i].Substring(0, k).Trim()] = lines[i].Substring(k + 1).Trim();
            }

            if (headers.TryGetValue("Upgrade", out var up) && up.ToLowerInvariant() == "websocket"
                && headers.TryGetValue("Sec-WebSocket-Key", out var key))
            {
                string accept = Convert.ToBase64String(SHA1.Create().ComputeHash(
                    Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                WriteAscii(s, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "
                    + accept + "\r\n\r\n");
                RunSocket(s);
            }
            else
            {
                ServeFile(s, parts.Length > 1 ? parts[1] : "/");
            }
        }
        catch { }
        finally { try { if (s != null && s != current) c.Close(); } catch { } }
    }

    void RunSocket(Stream s)
    {
        lock (sendLock)
        {
            if (current != null) { try { current.Close(); } catch { } }
            current = s;
        }
        Events.Enqueue((Kind.Connected, null));

        var msg = new MemoryStream();
        try
        {
            while (running)
            {
                int b0 = s.ReadByte(), b1 = s.ReadByte();
                if (b0 < 0 || b1 < 0) break;
                bool fin = (b0 & 0x80) != 0;
                int op = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;
                if (len == 126) { var l = ReadExact(s, 2); len = (l[0] << 8) | l[1]; }
                else if (len == 127) { var l = ReadExact(s, 8); len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | l[i]; }
                byte[] mask = masked ? ReadExact(s, 4) : null;
                byte[] data = len > 0 ? ReadExact(s, (int)len) : new byte[0];
                if (masked) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];

                if (op == 8) break;                       // close
                if (op == 9) { SendFrame(0x8A, data); continue; }   // ping -> pong
                if (op == 10) continue;                   // pong
                msg.Write(data, 0, data.Length);          // text (1) or continuation (0)
                if (fin)
                {
                    Events.Enqueue((Kind.Message, Encoding.UTF8.GetString(msg.ToArray())));
                    msg.SetLength(0);
                }
            }
        }
        catch { }
        bool wasCurrent;
        lock (sendLock) { wasCurrent = current == s; if (wasCurrent) current = null; }
        try { s.Close(); } catch { }
        if (wasCurrent) Events.Enqueue((Kind.Closed, null));
    }

    static string ReadHeaders(Stream s)
    {
        var sb = new StringBuilder();
        while (sb.Length < 16384)
        {
            int b = s.ReadByte();
            if (b < 0) return null;
            sb.Append((char)b);
            if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 2] == '\r'
                && sb[sb.Length - 3] == '\n' && sb[sb.Length - 4] == '\r')
                return sb.ToString(0, sb.Length - 4);
        }
        return null;
    }

    static void WriteAscii(Stream s, string t)
    {
        var b = Encoding.ASCII.GetBytes(t);
        s.Write(b, 0, b.Length);
        s.Flush();
    }

    static byte[] ReadExact(Stream s, int n)
    {
        var buf = new byte[n];
        int o = 0;
        while (o < n)
        {
            int r = s.Read(buf, o, n - o);
            if (r <= 0) throw new EndOfStreamException();
            o += r;
        }
        return buf;
    }

    void ServeFile(Stream s, string path)
    {
        int q = path.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) path = path.Substring(0, q);
        string name = path == "/" ? "index.html" : Path.GetFileName(path);
        string full = Path.Combine(clientDir ?? "", name);
        if (clientDir != null && File.Exists(full))
        {
            var body = File.ReadAllBytes(full);
            string ct = name.EndsWith(".js") ? "text/javascript" : name.EndsWith(".css") ? "text/css" : "text/html; charset=utf-8";
            WriteAscii(s, $"HTTP/1.1 200 OK\r\nContent-Type: {ct}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            s.Write(body, 0, body.Length);
        }
        else
        {
            WriteAscii(s, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        }
        s.Flush();
    }

    public void Send(string text) { SendFrame(0x81, Encoding.UTF8.GetBytes(text)); }

    void SendFrame(int firstByte, byte[] payload)
    {
        lock (sendLock)
        {
            if (current == null) return;
            try
            {
                var ms = new MemoryStream();
                ms.WriteByte((byte)firstByte);
                if (payload.Length < 126) ms.WriteByte((byte)payload.Length);
                else if (payload.Length < 65536) { ms.WriteByte(126); ms.WriteByte((byte)(payload.Length >> 8)); ms.WriteByte((byte)payload.Length); }
                else
                {
                    ms.WriteByte(127);
                    for (int i = 7; i >= 0; i--) ms.WriteByte((byte)((long)payload.Length >> (8 * i)));
                }
                ms.Write(payload, 0, payload.Length);
                var arr = ms.ToArray();
                current.Write(arr, 0, arr.Length);
                current.Flush();
            }
            catch { }
        }
    }

    public void Dispose()
    {
        running = false;
        try { listener?.Stop(); } catch { }
        lock (sendLock) { try { current?.Close(); } catch { } current = null; }
    }
}
