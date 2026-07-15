using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AuroraSuite
{
    /// <summary>
    /// A real XBDM client that talks RDCP straight down a TCP socket on port 730.
    ///
    /// This replaces the old XDevkit COM-based check. That approach needed the Xbox 360
    /// SDK installed and the console registered in Neighborhood as a real devkit, which is
    /// exactly why it never worked on RGH/JTAG boxes. Speaking the protocol directly needs
    /// none of that - only that xbdm is running on the console (DashLaunch's xbdm plugin).
    ///
    /// The headline feature for this app: RDCP has no authentication whatsoever. There is
    /// no USER, no PASS, no login step - you connect and start issuing commands. That is
    /// what makes the password optional when syncing over XBDM instead of FTP.
    ///
    /// Protocol summary (xboxdevwiki "Xbox Debug Monitor", cross-checked against the
    /// Experiment5X/XBDM reference client):
    ///   - On connect the console sends "201- connected".
    ///   - Commands are plain text terminated with CRLF.
    ///   - Every reply is a line "NNN- message"; 2xx is success, 4xx is failure.
    ///   - 202 means a multiline body follows, terminated by a line containing just ".".
    ///   - 204 means "send me the binary data now", after which the console sends one
    ///     final status line reporting the result.
    /// </summary>
    public sealed class XbdmClient : IDisposable
    {
        public const int DefaultPort = 730;

        // Status codes we actually care about.
        public const int CodeOk = 200;
        public const int CodeConnected = 201;
        public const int CodeMultiline = 202;
        public const int CodeBinary = 203;
        public const int CodeReadyToAcceptData = 204;
        public const int CodeFileNotFound = 402;

        private TcpClient? _tcp;
        private NetworkStream? _stream;

        // Our own read buffer. We never hand the socket to a StreamReader, because a
        // StreamReader would greedily buffer past the end of a text line and swallow bytes
        // that belong to a following binary payload.
        private readonly byte[] _buffer = new byte[8192];
        private int _bufferPos;
        private int _bufferLen;

        public sealed class Response
        {
            public int Code { get; init; }
            public string Message { get; init; } = "";
            public bool IsSuccess => Code >= 200 && Code < 300;
            public override string ToString() => $"{Code}- {Message}";
        }

        public sealed class DirEntry
        {
            public string Name { get; init; } = "";
            public long Size { get; init; }
            public bool IsDirectory { get; init; }
        }

        public void Connect(string host, int port = DefaultPort, int timeoutMs = 8000)
        {
            _tcp = new TcpClient { NoDelay = true };
            var async = _tcp.BeginConnect(host, port, null, null);
            if (!async.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                _tcp.Close();
                throw new TimeoutException(
                    $"Could not reach XBDM at {host}:{port} (timed out). Check that xbdm is enabled " +
                    "in DashLaunch and that the console is on.");
            }
            _tcp.EndConnect(async);

            _stream = _tcp.GetStream();
            _tcp.ReceiveTimeout = timeoutMs;
            _tcp.SendTimeout = Math.Max(timeoutMs, 30000);

            // No login of any kind - the console greets us and we're ready to go.
            var greeting = ReadResponse();
            if (greeting.Code != CodeConnected && greeting.Code != CodeOk)
                throw new IOException($"Unexpected XBDM greeting: {greeting}");
        }

        // ---------- low level ----------

        private int ReadRaw()
        {
            if (_bufferPos >= _bufferLen)
            {
                _bufferLen = _stream!.Read(_buffer, 0, _buffer.Length);
                _bufferPos = 0;
                if (_bufferLen <= 0) return -1;
            }
            return _buffer[_bufferPos++];
        }

        private int PeekRaw()
        {
            if (_bufferPos >= _bufferLen)
            {
                _bufferLen = _stream!.Read(_buffer, 0, _buffer.Length);
                _bufferPos = 0;
                if (_bufferLen <= 0) return -1;
            }
            return _buffer[_bufferPos];
        }

        /// <summary>
        /// Reads one CRLF-terminated line. XBDM also allows CR NUL as a terminator, so both
        /// are handled.
        /// </summary>
        private string ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = ReadRaw();
                if (b < 0)
                {
                    if (sb.Length == 0)
                        throw new IOException("XBDM closed the connection.");
                    break;
                }
                if (b == '\n') break;
                if (b == '\r')
                {
                    int next = PeekRaw();
                    if (next == '\n' || next == 0) ReadRaw();
                    break;
                }
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static readonly Regex ResponseRegex = new(@"^(\d{3})\s*-\s?(.*)$", RegexOptions.Compiled);

        private Response ReadResponse()
        {
            var line = ReadLine();
            var m = ResponseRegex.Match(line);
            if (!m.Success)
                throw new IOException($"Malformed XBDM response: '{line}'");
            return new Response
            {
                Code = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Message = m.Groups[2].Value.Trim()
            };
        }

        private void WriteLine(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\r\n");
            _stream!.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        public Response SendCommand(string command)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected to XBDM.");
            WriteLine(command);
            return ReadResponse();
        }

        /// <summary>Reads a 202 multiline body: every line up to a line containing only ".".</summary>
        private List<string> ReadMultiline()
        {
            var lines = new List<string>();
            while (true)
            {
                var line = ReadLine();
                if (line == ".") break;
                if (line.Length == 0) continue;
                lines.Add(line);
            }
            return lines;
        }

        // ---------- commands ----------

        private static readonly Regex NameRegex = new("name=\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex SizeHiRegex = new(@"sizehi=0x([0-9A-Fa-f]+)", RegexOptions.Compiled);
        private static readonly Regex SizeLoRegex = new(@"sizelo=0x([0-9A-Fa-f]+)", RegexOptions.Compiled);
        private static readonly Regex DriveNameRegex = new("drivename=\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex DirectoryFlagRegex = new(@"(^|\s)directory(\s|$)", RegexOptions.Compiled);

        /// <summary>
        /// Lists a directory. Paths are XBDM-native, e.g. Hdd:\Aurora_0.7b\Data\GameData.
        /// A missing directory comes back as 402 and is reported as an empty list via
        /// <paramref name="found"/> rather than throwing.
        /// </summary>
        public List<DirEntry> DirList(string path, out bool found)
        {
            var entries = new List<DirEntry>();
            var response = SendCommand($"dirlist name=\"{path}\"");

            if (response.Code == CodeFileNotFound)
            {
                found = false;
                return entries;
            }
            if (response.Code != CodeMultiline)
                throw new IOException($"dirlist \"{path}\" failed: {response}");

            found = true;
            foreach (var line in ReadMultiline())
            {
                var nameMatch = NameRegex.Match(line);
                if (!nameMatch.Success) continue;

                long hi = ParseHex(SizeHiRegex.Match(line));
                long lo = ParseHex(SizeLoRegex.Match(line));

                // "directory" is a bare token appended to the line for folders. Strip the
                // quoted name out first, otherwise a file called something like
                // "my directory backup.asset" would be misread as a folder.
                var withoutName = NameRegex.Replace(line, " ");

                entries.Add(new DirEntry
                {
                    Name = nameMatch.Groups[1].Value,
                    Size = (hi << 32) | (uint)lo,
                    IsDirectory = DirectoryFlagRegex.IsMatch(withoutName)
                });
            }
            return entries;
        }

        public List<DirEntry> DirList(string path) => DirList(path, out _);

        private static long ParseHex(Match m) =>
            m.Success ? long.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;

        /// <summary>Drive names available on the console, e.g. Hdd, Flash, Usb0.</summary>
        public List<string> DriveList()
        {
            var drives = new List<string>();
            var response = SendCommand("drivelist");
            if (response.Code != CodeMultiline)
                throw new IOException($"drivelist failed: {response}");

            foreach (var line in ReadMultiline())
            {
                var m = DriveNameRegex.Match(line);
                if (m.Success) drives.Add(m.Groups[1].Value);
            }
            return drives;
        }

        /// <summary>
        /// Uploads a whole file, overwriting whatever is there.
        ///
        /// sendfile is deliberately a single whole-file operation: the length is declared up
        /// front and the console then reads exactly that many bytes. It must never be called
        /// per-chunk in a loop - each call restarts the file from offset 0. The chunking
        /// below is only how we feed the socket; it is one continuous stream to the console.
        ///
        /// Note the length is sent as hex (length=0x...), which is what xbdm expects.
        /// </summary>
        public void SendFile(string localPath, string remotePath)
        {
            var info = new FileInfo(localPath);
            if (!info.Exists) throw new FileNotFoundException("Local file not found.", localPath);

            var response = SendCommand($"sendfile name=\"{remotePath}\" length=0x{info.Length:X}");
            if (response.Code != CodeReadyToAcceptData)
                throw new IOException($"Console refused the upload of '{remotePath}': {response}");

            using (var fs = File.OpenRead(localPath))
            {
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
                    _stream!.Write(chunk, 0, read);
                _stream!.Flush();
            }

            // The console always answers once the data has landed. This has to be consumed
            // even when it's a success: leaving it in the socket would desynchronise every
            // command that follows.
            var final = ReadResponse();
            if (!final.IsSuccess)
                throw new IOException($"Upload of '{remotePath}' failed: {final}");
        }

        public void Bye()
        {
            try { if (_stream != null) SendCommand("bye"); }
            catch { /* going away regardless */ }
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
        }
    }
}
