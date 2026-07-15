#!/usr/bin/env python3
"""
Mock XBDM console + a faithful port of XbdmClient's protocol flow.

Purpose: the C# can't be compiled here (no Windows/.NET SDK), but the protocol state
machine can still be exercised end-to-end. This mirrors XbdmClient.cs exactly:
greeting -> drivelist -> dirlist -> sendfile(204 -> binary -> trailing status) -> dirlist
verify -> bye.

The subtle thing under test: sendfile replies 204, takes the raw bytes, and THEN sends a
final status line. If the client forgets to consume that final line, it stays in the socket
and every later command reads a stale response. The test below issues more commands after
an upload precisely to catch that.
"""
import socket, threading, re

HOST = "127.0.0.1"

# ---------------- mock console ----------------

class MockXbdm(threading.Thread):
    """Behaves like xbdm on a console: no auth, RDCP on a socket."""

    def __init__(self):
        super().__init__(daemon=True)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind((HOST, 0))
        self.sock.listen(1)
        self.port = self.sock.getsockname()[1]
        # remote path -> size
        self.files = {
            r"Hdd:\Aurora_0.7b\Data\GameData\454D0016_Bad Boys Miami\GC454D0016.asset": 657408,
        }
        self.received = {}

    def run(self):
        conn, _ = self.sock.accept()
        f = conn.makefile("rwb")
        f.write(b"201- connected\r\n"); f.flush()
        while True:
            line = f.readline()
            if not line:
                break
            cmd = line.decode("ascii", "replace").strip()
            if cmd == "bye":
                f.write(b"200- bye\r\n"); f.flush(); break
            elif cmd == "drivelist":
                f.write(b"202- multiline response follows\r\n")
                for d in ("Hdd", "Flash", "Usb0"):
                    f.write(f'drivename="{d}"\r\n'.encode())
                f.write(b".\r\n"); f.flush()
            elif cmd.startswith("dirlist"):
                path = re.search(r'name="([^"]*)"', cmd).group(1)
                self._dirlist(f, path)
            elif cmd.startswith("sendfile"):
                self._sendfile(f, cmd)
            else:
                f.write(b"407- unknown command\r\n"); f.flush()
        try: conn.close()
        except Exception: pass

    BASE = r"Hdd:\Aurora_0.7b\Data\GameData"

    @property
    def directories(self):
        return {
            self.BASE,
            self.BASE + r"\454D0016_Bad Boys Miami",
            self.BASE + r"\55530043_Some Game",
        }

    def _dirlist(self, f, path):
        # A real console answers 402 for a directory that doesn't exist, whatever its
        # prefix - it does not return an empty listing.
        if path not in self.directories:
            f.write(b"402- file not found\r\n"); f.flush()
            return

        f.write(b"202- multiline response follows\r\n")
        # child directories
        for d in sorted(self.directories):
            if d != path and d.rsplit("\\", 1)[0] == path:
                nm = d.rsplit("\\", 1)[1]
                f.write(f'name="{nm}" sizehi=0x0 sizelo=0x0 createhi=0x1D2 createlo=0x5E6 '
                        f'changehi=0x1D2 changelo=0x5E6 directory\r\n'.encode())
        # child files
        children = {p: s for p, s in list(self.files.items()) + list(self.received.items())
                    if p.rsplit("\\", 1)[0] == path}
        for p, size in sorted(children.items()):
            nm = p.rsplit("\\", 1)[1]
            f.write(f'name="{nm}" sizehi=0x{size >> 32:X} sizelo=0x{size & 0xFFFFFFFF:X} '
                    f'createhi=0x1D2 createlo=0x5E6 changehi=0x1D2 changelo=0x5E6\r\n'.encode())
        f.write(b".\r\n"); f.flush()

    def _sendfile(self, f, cmd):
        path = re.search(r'name="([^"]*)"', cmd).group(1)
        length = int(re.search(r"length=0x([0-9A-Fa-f]+)", cmd).group(1), 16)  # hex!
        f.write(b"204- send binary data\r\n"); f.flush()
        data = f.read(length)          # exactly the declared number of bytes
        self.received[path] = len(data)
        f.write(b"200- OK\r\n"); f.flush()   # trailing status the client MUST consume


# ---------------- port of XbdmClient.cs ----------------

RESPONSE = re.compile(r"^(\d{3})\s*-\s?(.*)$")
NAME = re.compile(r'name="([^"]*)"')
SIZEHI = re.compile(r"sizehi=0x([0-9A-Fa-f]+)")
SIZELO = re.compile(r"sizelo=0x([0-9A-Fa-f]+)")
DRIVE = re.compile(r'drivename="([^"]*)"')
DIRFLAG = re.compile(r"(^|\s)directory(\s|$)")


class Client:
    def __init__(self, host, port):
        self.s = socket.create_connection((host, port), timeout=5)
        self.f = self.s.makefile("rwb")
        code, msg = self.read_response()
        assert code == 201, f"bad greeting {code}"

    def read_line(self):
        return self.f.readline().decode("ascii", "replace").rstrip("\r\n")

    def read_response(self):
        m = RESPONSE.match(self.read_line())
        return int(m.group(1)), m.group(2).strip()

    def send_command(self, cmd):
        self.f.write((cmd + "\r\n").encode()); self.f.flush()
        return self.read_response()

    def read_multiline(self):
        out = []
        while True:
            l = self.read_line()
            if l == ".": break
            if l: out.append(l)
        return out

    def drivelist(self):
        code, _ = self.send_command("drivelist")
        assert code == 202, code
        return [DRIVE.search(l).group(1) for l in self.read_multiline() if DRIVE.search(l)]

    def dirlist(self, path):
        code, msg = self.send_command(f'dirlist name="{path}"')
        if code == 402: return None
        assert code == 202, f"{code} {msg}"
        entries = []
        for l in self.read_multiline():
            nm = NAME.search(l)
            if not nm: continue
            hi = int(SIZEHI.search(l).group(1), 16) if SIZEHI.search(l) else 0
            lo = int(SIZELO.search(l).group(1), 16) if SIZELO.search(l) else 0
            entries.append(dict(name=nm.group(1), size=(hi << 32) | (lo & 0xFFFFFFFF),
                                isdir=bool(DIRFLAG.search(NAME.sub(" ", l)))))
        return entries

    def sendfile(self, data, remote_path):
        code, msg = self.send_command(f'sendfile name="{remote_path}" length=0x{len(data):X}')
        assert code == 204, f"expected 204, got {code} {msg}"
        self.f.write(data); self.f.flush()
        code, msg = self.read_response()      # trailing status - must be consumed
        assert 200 <= code < 300, f"upload failed {code} {msg}"

    def bye(self):
        self.send_command("bye")


# ---------------- the test ----------------

def main():
    srv = MockXbdm(); srv.start()
    c = Client(HOST, srv.port)
    print("1. connected, greeting 201 accepted (no login was sent)")

    drives = c.drivelist()
    print(f"2. drivelist -> {drives}")
    assert drives == ["Hdd", "Flash", "Usb0"]

    base = r"Hdd:\Aurora_0.7b\Data\GameData"
    entries = c.dirlist(base)
    folders = [e["name"] for e in entries if e["isdir"]]
    print(f"3. dirlist base -> {len(entries)} entries, {len(folders)} folders: {folders}")
    assert len(folders) == 2

    title = base + r"\454D0016_Bad Boys Miami"
    before = c.dirlist(title)
    print(f"4. dirlist title -> {[(e['name'], e['size']) for e in before]}")
    assert before[0]["size"] == 657408 and not before[0]["isdir"]

    payload = bytes(range(256)) * 5128           # 1,312,768 bytes = a real 2-shot SS asset
    c.sendfile(payload, title + r"\SS454D0016.asset")
    print(f"5. sendfile {len(payload)} bytes -> 204, streamed, trailing 200 consumed")

    # The real point: commands after an upload must still be in sync.
    after = c.dirlist(title)
    names = {e["name"]: e["size"] for e in after}
    print(f"6. dirlist AFTER upload (proves no desync) -> {names}")
    assert names["SS454D0016.asset"] == len(payload), "size verify failed"

    missing = c.dirlist(base + r"\DoesNotExist")
    print(f"7. dirlist missing folder -> {missing} (402 handled, no throw)")
    assert missing is None

    c.bye()
    print("8. bye -> clean disconnect")
    print(f"\nConsole received: {srv.received}")
    print("\nALL PROTOCOL FLOW TESTS PASSED")


if __name__ == "__main__":
    main()
