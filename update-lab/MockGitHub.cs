using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Security.Cryptography;
using System.Collections.Generic;

internal static class MockGitHub
{
    private static string root, scenario = "newer";
    private static int port;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly string[] Scenarios = { "newer", "latest", "older", "no-release", "api-error", "missing-asset", "invalid-version", "checksum-fallback", "corrupt", "truncated", "unsafe-package", "wrong-version", "failed-startup", "slow", "timeout", "current-v1.2", "future-v1.2" };
    private static void Main(string[] args)
    {
        root = Path.GetFullPath(args[0]);
        port = args.Length > 1 ? Int32.Parse(args[1]) : 18761;
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        File.WriteAllText(Path.Combine(root, "server-state-" + port + ".json"), Json.Serialize(new { pid = System.Diagnostics.Process.GetCurrentProcess().Id, port = port }));
        Console.WriteLine("Mock GitHub: http://127.0.0.1:" + port + "/");
        while (true)
        {
            var connection = listener.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(delegate { Handle(connection); });
        }
    }
    private static void Handle(TcpClient connection)
    {
        using (connection)
        try
        {
            connection.ReceiveTimeout = 5000;
            using (var stream = connection.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true))
            {
                var first = reader.ReadLine(); if (first == null) return;
                var parts = first.Split(' '); if (parts.Length < 2) return;
                var path = parts[1]; string header; int total = 0;
                while (!String.IsNullOrEmpty(header = reader.ReadLine())) { total += header.Length; if (total > 65536) return; }
                if (path.StartsWith("/scenario?name="))
                {
                    var next = Uri.UnescapeDataString(path.Substring(15));
                    if (Array.IndexOf(Scenarios, next) < 0) { SendText(stream, 400, "Unknown scenario"); return; }
                    scenario = next; SendText(stream, 200, scenario); return;
                }
                var selected = scenario;
                if (path == "/" || path.StartsWith("/?"))
                {
                    var html = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><title>课表更新测试</title><style>body{font:16px Segoe UI,sans-serif;margin:40px;max-width:800px;background:#f6f7fb}button{padding:12px;margin:6px;border:0;border-radius:8px;background:white;cursor:pointer}h1{font-size:26px}#state{padding:18px;background:white;border-radius:12px}</style><h1>课表更新模拟服务</h1><p>选择场景后，在测试课表里右键 → 检查更新</p><p id='state'>当前场景：" + selected + "</p>");
                    var labels = new[] { "有新版 1.1.1", "已是最新版 1.1.0", "远程版本较低", "没有正式 Release", "GitHub 请求失败", "缺少更新附件", "版本号无效", "独立校验附件", "下载包损坏", "下载中断", "不安全的压缩路径", "内部版本不匹配", "新版启动失败", "慢速下载／取消", "请求超时", "正式版界面：1.2.0", "正式版界面：1.2.1（仅检查）" };
                    for (int index = 0; index < Scenarios.Length; index++) html.Append("<button onclick=\"fetch('/scenario?name=" + Scenarios[index] + "').then(r=>r.text()).then(t=>document.getElementById('state').textContent='当前场景：'+t)\">" + labels[index] + "</button>");
                    html.Append("<p>此服务只监听本机，不连接真实 GitHub。正式课表目录不会被测试覆盖。</p></html>");
                    Send(stream, 200, Encoding.UTF8.GetBytes(html.ToString()), "text/html; charset=utf-8"); return;
                }
                if (path == "/repos/FylzX/timetable-for-seewo/releases/latest")
                {
                    if (selected == "timeout") { Thread.Sleep(25000); }
                    if (selected == "no-release") { SendText(stream, 404, "No release"); return; }
                    if (selected == "api-error") { SendText(stream, 429, "Rate limited"); return; }
                    var version = selected == "current-v1.2" ? "1.2.0" : selected == "future-v1.2" ? "1.2.1" : selected == "latest" ? "1.1.0" : selected == "older" ? "1.0.9" : selected == "invalid-version" ? "banana" : selected == "wrong-version" ? "1.1.2" : "1.1.1";
                    var file = Asset(selected); var assets = new List<object>();
                    if (selected != "missing-asset")
                    {
                        assets.Add(new { name = "DesktopTimetable-update-win-x64.zip", size = new FileInfo(file).Length,
                            browser_download_url = "http://127.0.0.1:" + port + "/download.zip", digest = selected == "checksum-fallback" ? null : "sha256:" + Hash(file) });
                        if (selected == "checksum-fallback") assets.Add(new { name = "DesktopTimetable-update-win-x64.zip.sha256", size = 66,
                            browser_download_url = "http://127.0.0.1:" + port + "/checksum", digest = (string)null });
                    }
                    SendText(stream, 200, Json.Serialize(new { tag_name = "v" + version, draft = false, prerelease = false,
                        body = "本机模拟更新：" + selected + "\n仅用于验证更新流程与课表数据保留", assets = assets })); return;
                }
                if (path == "/checksum") { SendText(stream, 200, Hash(Asset(selected)) + "\n"); return; }
                if (path == "/download.zip")
                {
                    var file = Asset(selected); var length = new FileInfo(file).Length;
                    SendHeaders(stream, 200, length, "application/zip");
                    using (var input = File.OpenRead(file))
                    {
                        var buffer = new byte[65536]; int count; bool firstChunk = true;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (selected == "corrupt" && firstChunk) buffer[0] ^= 1;
                            stream.Write(buffer, 0, count); firstChunk = false;
                            if (selected == "truncated") return;
                            if (selected == "slow") Thread.Sleep(50);
                        }
                    }
                    return;
                }
                SendText(stream, 404, "Not found");
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
    }
    private static string Asset(string selected) { return Path.Combine(root, "packages", selected == "unsafe-package" ? "unsafe.zip" : selected == "failed-startup" ? "broken.zip" : "new.zip"); }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var input = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
    private static void SendText(Stream output, int status, string text) { Send(output, status, Encoding.UTF8.GetBytes(text), "application/json; charset=utf-8"); }
    private static void Send(Stream output, int status, byte[] bytes, string type) { SendHeaders(output, status, bytes.Length, type); output.Write(bytes, 0, bytes.Length); }
    private static void SendHeaders(Stream output, int status, long size, string type) { var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "Error") + "\r\nContent-Length: " + size + "\r\nContent-Type: " + type + "\r\nConnection: close\r\n\r\n"); output.Write(bytes, 0, bytes.Length); }
}
