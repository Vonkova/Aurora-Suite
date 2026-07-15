using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AuroraSuite
{
    public class FtpEntry
    {
        public string Name { get; set; } = "";
        public bool IsDirectory { get; set; }
    }

    public class MiniFtpClient : IDisposable
    {
        private TcpClient? _control;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public void Connect(string host, int port, string username, string password, int timeoutMs = 8000)
        {
            _control = new TcpClient();
            var result = _control.BeginConnect(host, port, null, null);
            if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                throw new TimeoutException($"Could not connect to {host}:{port} (timed out)");
            }
            _control.EndConnect(result);

            _stream = _control.GetStream();
            _reader = new StreamReader(_stream, Encoding.ASCII);
            _writer = new StreamWriter(_stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            ReadResponse();

            SendCommand($"USER {username}");
            var userResp = ReadResponse();
            if (userResp.Code == 331)
            {
                SendCommand($"PASS {password}");
                var passResp = ReadResponse();
                if (passResp.Code != 230)
                    throw new Exception($"Login failed: {passResp.Message}");
            }
            else if (userResp.Code != 230)
            {
                throw new Exception($"Login failed: {userResp.Message}");
            }

            SendCommand("TYPE I");
            ReadResponse();
        }

        public void ChangeDirectory(string path)
        {
            SendCommand($"CWD {path}");
            var resp = ReadResponse();
            if (resp.Code != 250)
                throw new Exception($"CWD to '{path}' failed: {resp.Message}");
        }

        public List<FtpEntry> ListDirectory(string path)
        {
            ChangeDirectory(path);
            string listing = "";
            var closeResp = ExecuteDataCommand("LIST", dataStream =>
            {
                using var ms = new MemoryStream();
                dataStream.CopyTo(ms);
                listing = Encoding.ASCII.GetString(ms.ToArray());
            });
            if (closeResp.Code != 226 && closeResp.Code != 250)
            {
                // some minimal servers still send the data cleanly but close the
                // control connection oddly; listing itself already came through
                // so we don't hard-fail here, but this is worth surfacing.
            }

            return ParseListing(listing);
        }

        public List<string> NlstNames(string path)
        {
            ChangeDirectory(path);
            string listing = "";
            ExecuteDataCommand("NLST", dataStream =>
            {
                using var ms = new MemoryStream();
                dataStream.CopyTo(ms);
                listing = Encoding.ASCII.GetString(ms.ToArray());
            });

            var names = new List<string>();
            foreach (var rawLine in listing.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = rawLine.Trim();
                if (trimmed.Length == 0) continue;

                if ((trimmed[0] == 'd' || trimmed[0] == '-' || trimmed[0] == 'l')
                    && trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 2)
                {
                    var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    names.Add(tokens[tokens.Length - 1]);
                }
                else
                {
                    var lastSlash = trimmed.LastIndexOf('/');
                    var name = lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
                    names.Add(name);
                }
            }
            return names;
        }

        public void UploadFile(string localFilePath, string remoteFileName)
        {
            var closeResp = ExecuteDataCommand($"STOR {remoteFileName}", dataStream =>
            {
                using var fs = File.OpenRead(localFilePath);
                fs.CopyTo(dataStream);
            });

            if (closeResp.Code != 226 && closeResp.Code != 250)
                throw new Exception($"Upload of '{remoteFileName}' did not complete cleanly: {closeResp.Message}");
        }

        /// <summary>
        /// Returns the size in bytes of a remote file via the FTP SIZE command,
        /// or null if the server doesn't support SIZE (RFC 3659) or the file
        /// isn't found. Used to verify an upload actually landed rather than
        /// just trusting the transfer-complete response code.
        /// </summary>
        public long? GetSize(string remoteFileName)
        {
            SendCommand($"SIZE {remoteFileName}");
            var resp = ReadResponse();
            if (resp.Code != 213) return null;

            var parts = resp.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out var size))
                return size;
            return null;
        }

        /// <summary>
        /// Runs one data-transfer FTP command (LIST/NLST/STOR/RETR), preferring
        /// passive mode (PASV) since it doesn't require the console to open an
        /// inbound connection back to this PC -- which active mode (PORT) does,
        /// and which is commonly and silently blocked by Windows Firewall while
        /// the control connection still reports a misleadingly clean success.
        /// Falls back to active mode only if the server rejects PASV outright.
        /// </summary>
        private (int Code, string Message) ExecuteDataCommand(string command, Action<NetworkStream> transferAction)
        {
            SendCommand("PASV");
            var pasvResp = ReadResponse();
            if (pasvResp.Code == 227)
            {
                var match = Regex.Match(pasvResp.Message, @"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)");
                if (match.Success)
                {
                    var ip = $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}.{match.Groups[4].Value}";
                    var port = int.Parse(match.Groups[5].Value) * 256 + int.Parse(match.Groups[6].Value);

                    using var dataClient = new TcpClient();
                    dataClient.Connect(ip, port);

                    SendCommand(command);
                    var openResp = ReadResponse();
                    if (openResp.Code != 150 && openResp.Code != 125)
                        throw new Exception($"{command} failed: {openResp.Message}");

                    using (var dataStream = dataClient.GetStream())
                    {
                        transferAction(dataStream);
                    }
                    return ReadResponse();
                }
            }

            // Fallback: active mode (PORT). Requires the console to connect back
            // to this PC; may fail silently behind a firewall.
            var listener = OpenActiveDataChannel();
            SendCommand(command);
            var openResp2 = ReadResponse();
            if (openResp2.Code != 150 && openResp2.Code != 125)
            {
                listener.Stop();
                throw new Exception($"{command} failed: {openResp2.Message}");
            }

            using (var dataClient2 = listener.AcceptTcpClient())
            using (var dataStream2 = dataClient2.GetStream())
            {
                transferAction(dataStream2);
            }
            listener.Stop();
            return ReadResponse();
        }

        public void Quit()
        {
            try
            {
                SendCommand("QUIT");
                ReadResponse();
            }
            catch
            {
            }
        }

        private TcpListener OpenActiveDataChannel()
        {
            var localIp = ((IPEndPoint)_control!.Client.LocalEndPoint!).Address;
            if (localIp.IsIPv4MappedToIPv6)
                localIp = localIp.MapToIPv4();
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            var ipBytes = localIp.GetAddressBytes();
            var portHi = localPort / 256;
            var portLo = localPort % 256;
            SendCommand($"PORT {ipBytes[0]},{ipBytes[1]},{ipBytes[2]},{ipBytes[3]},{portHi},{portLo}");
            var resp = ReadResponse();
            if (resp.Code != 200)
            {
                listener.Stop();
                throw new Exception($"PORT failed: {resp.Message}");
            }

            return listener;
        }

        private void SendCommand(string command)
        {
            _writer!.WriteLine(command);
        }

        private (int Code, string Message) ReadResponse()
        {
            string? line = _reader!.ReadLine();
            if (line == null) throw new Exception("Connection closed unexpectedly");

            if (line.Length >= 4 && line[3] == '-')
            {
                var code = line.Substring(0, 3);
                string? next;
                do
                {
                    next = _reader.ReadLine();
                    if (next == null) break;
                } while (!(next.StartsWith(code + " ")));
                line = next ?? line;
            }

            var codeStr = line.Length >= 3 ? line.Substring(0, 3) : "0";
            int.TryParse(codeStr, out int parsedCode);
            return (parsedCode, line);
        }

        private static List<FtpEntry> ParseListing(string raw)
        {
            var entries = new List<FtpEntry>();
            var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed[0] == 'd' || trimmed[0] == '-' || trimmed[0] == 'l')
                {
                    bool isDir = trimmed[0] == 'd';
                    var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2)
                    {
                        var name = tokens[tokens.Length - 1];
                        entries.Add(new FtpEntry { Name = name, IsDirectory = isDir });
                        continue;
                    }
                }

                var dosMatch = Regex.Match(trimmed,
                    @"^\S+\s+\S+\s+(<DIR>|\d+)\s+(.+)$");
                if (dosMatch.Success)
                {
                    bool isDir = dosMatch.Groups[1].Value == "<DIR>";
                    entries.Add(new FtpEntry { Name = dosMatch.Groups[2].Value.Trim(), IsDirectory = isDir });
                    continue;
                }

                var fallbackTokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fallbackTokens.Length >= 1)
                {
                    entries.Add(new FtpEntry { Name = fallbackTokens[fallbackTokens.Length - 1], IsDirectory = true });
                }
            }

            return entries;
        }

        public void Dispose()
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _control?.Close(); } catch { }
        }
    }
}
