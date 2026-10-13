// Shared with the small .NET Framework launcher; keep C# 5 / Framework-compatible.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;

namespace Timetable.Updates
{
    public sealed class UpdateFile
    {
        public UpdateFile() { Path = ""; Sha256 = ""; }
        public string Path { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
    }
    public sealed class UpdateManifest
    {
        public UpdateManifest() { Version = ""; Files = new List<UpdateFile>(); }
        public int Format { get; set; }
        public string Version { get; set; }
        public List<UpdateFile> Files { get; set; }
    }
    public sealed class UpdateRequest
    {
        public UpdateRequest() { Version = ""; Stage = ""; TestUrl = ""; }
        public string Version { get; set; }
        public string Stage { get; set; }
        public int ParentPid { get; set; }
        public long ParentStartTicks { get; set; }
        public string TestUrl { get; set; }
        public bool SmokeTest { get; set; }
    }
    public static class UpdatePackage
    {
        public static T ReadXml<T>(string path)
        {
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                var value = new XmlSerializer(typeof(T)).Deserialize(reader);
                if (value == null) throw new InvalidDataException("更新配置为空。");
                return (T)value;
            }
        }
        public static void WriteXml<T>(string path, T value)
        {
            using (var stream = File.Create(path)) new XmlSerializer(typeof(T)).Serialize(stream, value);
        }
        public static string Hash(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static bool SafePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Contains("\\") || path.Contains(":")) return false;
            var pieces = path.Split('/');
            if (pieces.Any(piece => piece == "" || piece == "." || piece == ".." || piece.EndsWith(".") || piece.EndsWith(" "))) return false;
            return path == "DesktopTimetable.exe" || path.StartsWith("app/", StringComparison.Ordinal);
        }
        public static void CheckLinks(string directory)
        {
            var current = new DirectoryInfo(directory);
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新路径不能使用目录链接。");
                current = current.Parent;
            }
        }
        public static void Validate(string directory, string expectedVersion)
        {
            CheckLinks(directory);
            var manifest = ReadXml<UpdateManifest>(System.IO.Path.Combine(directory, "update-manifest.xml"));
            if (manifest.Format != 1 || manifest.Version != expectedVersion || manifest.Files == null || manifest.Files.Count == 0)
                throw new InvalidDataException("更新包版本或格式不匹配。");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (!SafePath(file.Path) || !paths.Add(file.Path)) throw new InvalidDataException("更新包包含不允许的路径或重复文件。");
                var path = System.IO.Path.Combine(directory, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    || new FileInfo(path).Length != file.Size || !String.Equals(Hash(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新文件缺失或校验失败：" + file.Path);
            }
            foreach (var path in FilesWithoutLinks(directory))
            {
                var relative = path.Substring(directory.Length + 1).Replace('\\', '/');
                if (relative != "update-manifest.xml" && !paths.Contains(relative)) throw new InvalidDataException("更新包包含清单以外的文件。");
            }
            foreach (var required in new[] { "DesktopTimetable.exe", "app/DesktopTimetable.exe", "app/DesktopTimetable.dll", "app/resources.pri" })
                if (!paths.Contains(required)) throw new InvalidDataException("更新包不完整：" + required);
            var version = FileVersionInfo.GetVersionInfo(System.IO.Path.Combine(directory, "app/DesktopTimetable.dll"));
            var actual = version.FileMajorPart + "." + version.FileMinorPart + "." + version.FileBuildPart;
            if (actual != expectedVersion) throw new InvalidDataException("程序内部版本与发布版本不匹配。");
        }
        private static IEnumerable<string> FilesWithoutLinks(string directory)
        {
            foreach (var path in Directory.GetFiles(directory)) yield return path;
            foreach (var child in Directory.GetDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新包不能包含目录链接。");
                foreach (var path in FilesWithoutLinks(child)) yield return path;
            }
        }
    }
}
