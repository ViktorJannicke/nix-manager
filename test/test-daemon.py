"""The Runic port's daemon (D025, W300-031) with real processes: a daemon, or a dashboard, and command lines.

Mocked build tools as in test-manager.py; never activates the machine. Every process gets its own runtime
directory, so the single-instance socket never reaches a real Nix Manager. The daemon runs in Runic's serve-only
mode: its dashboard Window is served instead of shown, and a line on its standard input closes it.
"""
import base64
import http.client
import json
import os
from pathlib import Path
import pty
import re
import shlex
import shutil
import signal
import socket
import struct
import subprocess
import tempfile
import threading
import time
import unittest
from urllib.parse import urljoin, urlsplit

COMMAND = shlex.split(os.environ["NIX_MANAGER_TEST_COMMAND"])
TIMEOUT = 60


class Sandbox:
    """A checkout, mocked tools, a state directory and a runtime directory shared by the processes of one test."""

    def __init__(self):
        self.directory = tempfile.TemporaryDirectory()
        root = self.root = Path(self.directory.name)
        (root / "flake.nix").touch()
        (root / "hosts.json").write_text(json.dumps({"test": {
            "homeConfiguration": "user-test", "nixosConfiguration": "test-machine",
            "nixosHostName": "test-machine", "system": "x86_64-linux"}}))
        bindir = root / "bin"
        bindir.mkdir()
        bash = shutil.which("bash")
        for name in ("home-manager", "nix-env", "nix-store"):
            executable = bindir / name
            executable.write_text(f'#!{bash}\n'
                'printf "%s\\n" "${0##*/} $*" >> "$CALLS"\n'
                '[[ "${0##*/} ${1:-}" != "$FAIL_COMMAND" ]]\n')
            executable.chmod(0o755)
        activate = root / "activate"
        activate.write_text(f'#!{bash}\n'
            'printf "%s\\n" "activate $*" >> "$CALLS"\n'
            'echo "Activating home configuration"\n'
            '[[ "activate ${1:-}" != "$FAIL_COMMAND" ]]\n')
        activate.chmod(0o755)
        # `nix build` prints to both streams, and with GATE waits until the test opens it, so an operation can be
        # caught while it runs.
        (bindir / "nix").write_text(f'#!{bash}\n'
            'printf "%s\\n" "nix $*" >> "$CALLS"\n'
            'link=""; prev=""\n'
            'for arg in "$@"; do [[ $prev == --out-link ]] && link=$arg; prev=$arg; done\n'
            'echo "building ${link##*/}"\n'
            'echo "evaluation warning: a mocked warning" >&2\n'
            'if [[ -n ${GATE:-} && $1 == build ]]; then\n'
            '  touch "$GATE.started"\n'
            '  for _ in $(seq 600); do [[ -e "$GATE.open" ]] && break; sleep 0.1; done\n'
            'fi\n'
            '[[ "nix $1 ${link##*/}" == "$FAIL_COMMAND" ]] && exit 1\n'
            'if [[ -n $link ]]; then\n'
            '  out="$OUTPUTS/${link##*/}"; mkdir -p "$out"\n'
            f'  cp {activate} "$out/activate"; ln -sfn "$out" "$link"\n'
            'fi\n')
        (bindir / "nix").chmod(0o755)
        (root / "outputs").mkdir()
        state = root / "state"
        profile = state / "nix/profiles/home-manager"
        profile.parent.mkdir(parents=True)
        profile.touch()
        runtime = root / "runtime"
        runtime.mkdir(mode=0o700)
        self.gate = root / "gate"
        base = {key: value for key, value in os.environ.items()
                if key not in ("DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "SSH_CONNECTION", "SSH_TTY",
                               "NIX_MANAGER_DAEMON", "XDG_ACTIVATION_TOKEN", "DESKTOP_STARTUP_ID")}
        self.env = base | {
            "PATH": str(bindir) + ":" + os.environ["PATH"],
            "NIX_MANAGER_HOSTS": str(root / "hosts.json"),
            "NIX_MANAGER_FLAKE": str(root), "NIX_MANAGER_HOST": "test",
            "FAIL_COMMAND": "", "CALLS": str(root / "calls"),
            "XDG_STATE_HOME": str(state), "OUTPUTS": str(root / "outputs"),
            "XDG_RUNTIME_DIR": str(runtime),
            # No window or browser: the dashboard is served, and a line on standard input closes it.
            "RUNIC_APPLICATION_SERVE_ONLY": "1",
            "NO_COLOR": "1",
        }

    def close(self):
        self.open_gate()
        self.directory.cleanup()

    def with_gate(self):
        for suffix in (".started", ".open"):
            Path(str(self.gate) + suffix).unlink(missing_ok=True)
        return {"GATE": str(self.gate)}

    def wait_for_gate(self):
        deadline = time.monotonic() + TIMEOUT
        while not Path(str(self.gate) + ".started").exists():
            if time.monotonic() > deadline:
                raise AssertionError("the build never started")
            time.sleep(0.05)

    def open_gate(self):
        Path(str(self.gate) + ".open").touch()

    def run(self, args, env=None, timeout=TIMEOUT):
        return subprocess.run([*COMMAND, *args], env=self.env | (env or {}), text=True, capture_output=True,
                              timeout=timeout, stdin=subprocess.DEVNULL)

    def start(self, args, env=None):
        return subprocess.Popen([*COMMAND, *args], env=self.env | (env or {}), text=True, stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def without_checkout(self):
        """No checkout asked for, and none where Nix Manager looks: the setup assistant's case."""
        home = self.root / "home"
        home.mkdir(exist_ok=True)
        return {"NIX_MANAGER_FLAKE": "", "NIX_MANAGER_HOST": "", "HOME": str(home)}

    def history(self):
        result = self.run(["history", "--format", "json"])
        assert result.returncode == 0, result.stderr
        return payload(result.stdout)["history"]


class Instance:
    """A daemon (`tray`) or a dashboard (`ui`) process, with its output collected as it arrives."""

    def __init__(self, sandbox, args, env=None, ready=None):
        # Outside any Git checkout, which Nix Manager would otherwise consider managing.
        self.process = subprocess.Popen([*COMMAND, *args], env=sandbox.env | (env or {}), text=True, cwd=sandbox.root,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.lines = {"stdout": [], "stderr": []}
        self.changed = threading.Condition()
        self.readers = [threading.Thread(target=self.read, args=(name, stream), daemon=True)
                        for name, stream in (("stdout", self.process.stdout), ("stderr", self.process.stderr))]
        for reader in self.readers:
            reader.start()
        if ready:
            self.wait_for(*ready)

    @property
    def pid(self):
        return self.process.pid

    def read(self, name, stream):
        for line in stream:
            with self.changed:
                self.lines[name].append(line.rstrip("\n"))
                self.changed.notify_all()

    def count(self, stream, text):
        with self.changed:
            return sum(text in line for line in self.lines[stream])

    def wait_for(self, stream, text, count=1, timeout=TIMEOUT):
        with self.changed:
            if not self.changed.wait_for(lambda: self.count(stream, text) >= count or self.process.poll() is not None,
                                         timeout):
                raise AssertionError(f"{text!r} did not appear on {stream}: {self.lines}")
            if self.count(stream, text) < count:
                raise AssertionError(f"the process ended before {text!r} appeared on {stream}: {self.lines}")

    def page(self):
        """The page the served Window starts with, from the address it printed, without loading it."""
        with self.changed:
            line = next(line for line in self.lines["stdout"] if line.startswith("RUNIC_APPLICATION_URL="))
        address = line.split("=", 1)[1]
        host, _, path = address.removeprefix("http://").partition("/")
        connection = http.client.HTTPConnection(host, timeout=TIMEOUT)
        try:
            connection.request("GET", "/" + path)
            response = connection.getresponse()
            response.read()
            return (response.getheader("Location") or "").rsplit("/", 1)[-1]
        finally:
            connection.close()

    def close_window(self):
        self.process.stdin.write("\n")
        self.process.stdin.flush()

    def stop(self, sig=signal.SIGTERM, timeout=TIMEOUT):
        if self.process.poll() is None:
            self.process.send_signal(sig)
        code = self.process.wait(timeout)
        self.close()
        return code

    def close(self):
        for reader in self.readers:
            reader.join(5)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            try:
                stream.close()
            except BrokenPipeError:
                pass


class Tab:
    """A browser tab on a served page: it loads the page and its WebUI bridge script, then holds the authenticated
    WebSocket connection the bridge opens, as WebKitGTK or Chromium would, until it is closed or reloaded."""

    def __init__(self, address, cookies=None):
        self.address = address
        # A browser keeps the page's cookies across a reload; the page then knows the tab again.
        self.cookies = cookies if cookies is not None else {}
        page = self.get(address)
        bridge = self.get(urljoin(page, "webui.js"), read=True)
        token = int(re.search(r"const TOKEN = (\d+);", bridge).group(1))
        port = int(re.search(r"const PORT = (\d+);", bridge).group(1))
        base = re.search(r'const BASE_PATH = "([^"]*)";', bridge).group(1)
        credential = json.loads(re.search(r"const SESSION_CREDENTIAL = (\"[^\"]*\");", bridge).group(1))
        origin = urlsplit(address)
        # Until the server has let go of a reloaded page's socket, it refuses the next one; the bridge tries again.
        for _ in range(20):
            if self.upgrade(origin, port, base, credential):
                break
            time.sleep(0.05)
        else:
            raise AssertionError("the served page refused the tab's connection")
        self.lock = threading.Lock()
        # The bridge's token check (CMD_CHECK_TK); the server answers 1 once the connection is authenticated.
        self.authenticated = threading.Event()
        self.send(bytes([0xDD]) + struct.pack("<IH", token, 0) + bytes([0xF5, 0]))
        self.reader = threading.Thread(target=self.receive, daemon=True)
        self.reader.start()
        if not self.authenticated.wait(TIMEOUT):
            raise AssertionError("the served page did not accept the tab's token")

    def upgrade(self, origin, port, base, credential):
        """Opens the bridge's WebSocket; false when the server refused it."""
        self.socket = socket.create_connection((origin.hostname, port), timeout=TIMEOUT)
        key = base64.b64encode(os.urandom(16)).decode()
        headers = [
            f"GET {base}/_webui_ws_connect HTTP/1.1", f"Host: {origin.hostname}:{port}", "Upgrade: websocket",
            "Connection: Upgrade", f"Sec-WebSocket-Key: {key}", "Sec-WebSocket-Version: 13",
            f"Origin: {origin.scheme}://{origin.netloc}",
        ]
        if credential:
            headers.append(f"Sec-WebSocket-Protocol: runic-desktop.{credential}")
        if self.cookies:
            headers.append("Cookie: " + "; ".join(f"{name}={value}" for name, value in self.cookies.items()))
        self.socket.sendall(("\r\n".join(headers) + "\r\n\r\n").encode())
        response = b""
        while b"\r\n\r\n" not in response:
            response += self.socket.recv(4096)
        if response.startswith(b"HTTP/1.1 101"):
            return True
        assert response.startswith(b"HTTP/1.1 403"), response
        self.socket.close()
        return False

    def get(self, address, read=False):
        """Follows redirects as a browser does; returns the final address, or with read the body."""
        for _ in range(5):
            parts = urlsplit(address)
            connection = http.client.HTTPConnection(parts.netloc, timeout=TIMEOUT)
            try:
                headers = {"Cookie": "; ".join(f"{name}={value}" for name, value in self.cookies.items())} if self.cookies else {}
                connection.request("GET", parts.path + (f"?{parts.query}" if parts.query else ""), headers=headers)
                response = connection.getresponse()
                body = response.read().decode()
                for cookie in response.headers.get_all("Set-Cookie") or []:
                    name, _, value = cookie.split(";", 1)[0].partition("=")
                    self.cookies[name] = value
                if response.status in (301, 302, 303, 307, 308):
                    address = urljoin(address, response.getheader("Location"))
                    continue
                assert response.status == 200, (address, response.status)
                return body if read else address
            finally:
                connection.close()
        raise AssertionError(f"too many redirects from {address}")

    def send(self, payload, opcode=0x2):
        mask = os.urandom(4)
        length = len(payload)
        header = bytes([0x80 | opcode])
        header += bytes([0x80 | length]) if length < 126 else bytes([0x80 | 126]) + struct.pack(">H", length) \
            if length < 65536 else bytes([0x80 | 127]) + struct.pack(">Q", length)
        with self.lock:
            self.socket.sendall(header + mask + bytes(byte ^ mask[i % 4] for i, byte in enumerate(payload)))

    def receive(self):
        """Reads what the page is sent, answers pings, and notes the token check's answer; the rest is ignored."""
        stream = self.socket.makefile("rb")
        try:
            while True:
                first, second = stream.read(2)
                length = second & 0x7F
                if length == 126:
                    length = struct.unpack(">H", stream.read(2))[0]
                elif length == 127:
                    length = struct.unpack(">Q", stream.read(8))[0]
                payload = stream.read(length)
                opcode = first & 0x0F
                if opcode == 0x9:
                    self.send(payload, 0xA)
                elif opcode == 0x8:
                    return
                elif len(payload) > 8 and payload[0] == 0xDD and payload[7] == 0xF5 and payload[8] == 1:
                    self.authenticated.set()
        except (OSError, ValueError):
            return

    def reload(self):
        """Reloads the page: its connection closes, and the reloaded page connects again with the same cookies."""
        self.close()
        return Tab(self.address, self.cookies)

    def close(self):
        """Closes the tab: the bridge's socket closes, as when the browser closes the tab."""
        try:
            self.send(struct.pack(">H", 1001), 0x8)
            # The server answers the close, as it does a browser's.
            self.reader.join(5)
            self.socket.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.socket.close()


def tray(sandbox, env=None):
    return Instance(sandbox, ["tray"], env, ready=("stdout", "Nix Manager is running in the tray."))


OPERATION_ID = re.compile(r"\d{13}-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
TIMESTAMP = re.compile(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z")
PID = re.compile(r'"pid": ?\d+')
REQUEST_ID = re.compile(r'"requestId":"[0-9a-f]+"')


MOCKED_WARNING = "evaluation warning: a mocked warning\n"


def normal(text):
    """The output without what differs between two runs: request and operation IDs, times and the process that ran it."""
    return REQUEST_ID.sub('"requestId":""', PID.sub('"pid": 0', TIMESTAMP.sub("TIME", OPERATION_ID.sub("ID", text))))


def payload(stdout):
    """The result in a runic.commandline/1 response (--format json)."""
    response = json.loads(stdout)
    assert response["protocol"] == "runic.commandline/1", response
    return response["payload"]


class DaemonTests(unittest.TestCase):
    def setUp(self):
        self.sandbox = Sandbox()
        self.instances = []

    def tearDown(self):
        self.sandbox.open_gate()
        for instance in self.instances:
            if instance.process.poll() is None:
                instance.process.kill()
            instance.process.wait(TIMEOUT)
            instance.close()
        self.sandbox.close()

    def tray(self, env=None):
        instance = tray(self.sandbox, env)
        self.instances.append(instance)
        return instance

    def dashboard(self, env=None):
        instance = Instance(self.sandbox, ["ui"], env, ready=("stdout", "RUNIC_APPLICATION_URL="))
        self.instances.append(instance)
        return instance

    def assertSameRun(self, standalone, forwarded):
        self.assertEqual(forwarded.returncode, standalone.returncode, (standalone.stderr, forwarded.stderr))
        self.assertEqual(normal(forwarded.stdout), normal(standalone.stdout))
        # The mocked nix writes one line to each of its streams; which of the two the runner reads first is up to the
        # scheduler, in a standalone run as much as in the daemon, so that line is compared by count, not by place.
        self.assertEqual(forwarded.stderr.count(MOCKED_WARNING), standalone.stderr.count(MOCKED_WARNING))
        self.assertEqual(normal(forwarded.stderr.replace(MOCKED_WARNING, "")), normal(standalone.stderr.replace(MOCKED_WARNING, "")))

    def test_forwarded_output_and_exit_status_equal_a_standalone_run(self):
        # The daemon runs operations with its own environment, so a failing step is set there.
        groups = {
            "": [["home", "switch"], ["home", "switch", "--output", "json"], ["--plain", "home", "cleanup", "switch"],
                 ["home", "build", "--", "--show-trace"]],
            "nix build home": [["home", "switch", "--format", "json"], ["home", "switch"]],
            "activate --driver-version": [["home", "cleanup", "switch"]],
        }
        for fail, commands in groups.items():
            env = {"FAIL_COMMAND": fail}
            standalone = [self.sandbox.run(args, env) for args in commands]
            daemon = self.tray(env)
            for args, alone in zip(commands, standalone):
                with self.subTest(args=args, fail=fail):
                    self.assertEqual(alone.returncode, 1 if fail else 0, alone.stderr)
                    forwarded = self.sandbox.run(args, env)
                    self.assertSameRun(alone, forwarded)
                    self.assertEqual(self.sandbox.history()[0]["pid"], daemon.pid, "the daemon ran it")
            self.assertEqual(daemon.stop(), 0)

    def test_read_only_commands_and_previews_run_here_with_the_same_output(self):
        commands = [["status", "--format", "json"], ["history"], ["--dry-run", "update", "cleanup", "switch"],
                    ["--dry-run", "home", "switch", "--format", "json"], ["completion", "bash"], ["--help"], ["switch", "boot"]]
        standalone = [self.sandbox.run(args) for args in commands]
        self.tray()
        for args, alone in zip(commands, standalone):
            with self.subTest(args=args):
                self.assertSameRun(alone, self.sandbox.run(args))

    def test_ctrl_c_cancels_the_operation_in_the_daemon(self):
        def interrupted():
            cli = self.sandbox.start(["home", "switch"], self.sandbox.with_gate())
            self.sandbox.wait_for_gate()
            cli.send_signal(signal.SIGINT)
            stdout, stderr = cli.communicate(timeout=TIMEOUT)
            return cli.returncode, stdout, stderr

        alone = interrupted()
        daemon = self.tray(self.sandbox.with_gate())
        forwarded = interrupted()
        self.assertEqual(forwarded[0], 130, forwarded)
        self.assertEqual((forwarded[0], normal(forwarded[1]), normal(forwarded[2])), (alone[0], normal(alone[1]), normal(alone[2])))
        latest = self.sandbox.history()[0]
        self.assertEqual((latest["pid"], latest["status"]), (daemon.pid, "cancelled"))
        # The daemon stays and runs the next operation.
        self.assertEqual(daemon.process.poll(), None)
        self.sandbox.open_gate()
        self.assertEqual(self.sandbox.run(["home", "build"]).returncode, 0)

    def test_a_second_operation_is_refused_like_the_operation_lock_refuses_it(self):
        def second():
            first = self.sandbox.start(["home", "switch"], self.sandbox.with_gate())
            self.sandbox.wait_for_gate()
            refused = self.sandbox.run(["home", "build"])
            self.sandbox.open_gate()
            first.communicate(timeout=TIMEOUT)
            self.assertEqual(first.returncode, 0)
            return refused

        alone = second()
        self.tray(self.sandbox.with_gate())
        forwarded = second()
        self.assertEqual(forwarded.returncode, 1)
        self.assertIn("Another nix-manager operation is running. Wait for it to finish.", forwarded.stdout)
        self.assertIn("The operation failed.", forwarded.stderr)
        self.assertSameRun(alone, forwarded)

    def test_a_second_ui_launch_brings_the_open_dashboard_forward(self):
        daemon = self.tray()
        first = self.sandbox.run(["ui"])
        self.assertEqual(first.returncode, 0, first.stderr)
        daemon.wait_for("stderr", "Opened the dashboard.")
        second = self.sandbox.run(["ui"])
        self.assertEqual(second.returncode, 0, second.stderr)
        daemon.wait_for("stderr", "Brought the dashboard forward.")
        self.assertEqual(daemon.count("stdout", "RUNIC_APPLICATION_URL="), 1)
        self.assertEqual(daemon.stop(), 0)

    def test_closing_the_dashboard_leaves_the_operation_running(self):
        daemon = self.tray(self.sandbox.with_gate())
        self.assertEqual(self.sandbox.run(["ui"]).returncode, 0)
        daemon.wait_for("stdout", "RUNIC_APPLICATION_URL=")
        cli = self.sandbox.start(["home", "switch", "--format", "json"])
        self.sandbox.wait_for_gate()
        daemon.close_window()
        daemon.wait_for("stderr", "The dashboard closed; the operation keeps running.")
        self.sandbox.open_gate()
        stdout, stderr = cli.communicate(timeout=TIMEOUT)
        self.assertEqual(cli.returncode, 0, stderr)
        self.assertEqual(payload(stdout)["operation"]["status"], "succeeded")
        self.assertEqual(daemon.process.poll(), None)
        self.assertEqual(daemon.stop(), 0)

    def test_stopping_the_daemon_cancels_the_operation_and_tells_the_command_line(self):
        daemon = self.tray(self.sandbox.with_gate())
        cli = self.sandbox.start(["home", "switch"])
        self.sandbox.wait_for_gate()
        self.assertEqual(daemon.stop(signal.SIGTERM), 0)
        stdout, stderr = cli.communicate(timeout=TIMEOUT)
        self.assertEqual(cli.returncode, 130, (stdout, stderr))
        self.assertEqual(self.sandbox.history()[0]["status"], "cancelled")

    def test_a_daemon_that_dies_mid_operation_fails_the_command_line_clearly(self):
        daemon = self.tray(self.sandbox.with_gate())
        cli = self.sandbox.start(["home", "switch"])
        self.sandbox.wait_for_gate()
        daemon.stop(signal.SIGKILL)
        stdout, stderr = cli.communicate(timeout=TIMEOUT)
        self.assertEqual(cli.returncode, 1, (stdout, stderr))
        self.assertIn("Nix Manager's daemon ended before the operation finished.", stdout)
        self.assertIn("The operation failed.", stderr)
        # Without the daemon the command line runs operations itself again.
        self.sandbox.open_gate()
        self.assertEqual(self.sandbox.run(["home", "build"]).returncode, 0)

    def test_without_a_daemon_the_command_line_runs_the_operation_itself(self):
        cli = self.sandbox.start(["home", "switch", "--format", "json"])
        stdout, stderr = cli.communicate(timeout=TIMEOUT)
        self.assertEqual(cli.returncode, 0, stderr)
        self.assertEqual(payload(stdout)["operation"]["pid"], cli.pid)
        # Turned off, and for another state directory, it runs here while a daemon runs.
        daemon = self.tray()
        for env in ({"NIX_MANAGER_DAEMON": "0"}, {"NIX_MANAGER_STATE_DIR": str(self.sandbox.root / "other-state")}):
            with self.subTest(env=env):
                cli = self.sandbox.start(["home", "build", "--format", "json"], env)
                stdout, stderr = cli.communicate(timeout=TIMEOUT)
                self.assertEqual(cli.returncode, 0, stderr)
                self.assertEqual(payload(stdout)["operation"]["pid"], cli.pid)
                self.assertNotEqual(payload(stdout)["operation"]["pid"], daemon.pid)
        # And so does every process without a runtime directory, as in a plain SSH or cron environment.
        cli = self.sandbox.start(["home", "build", "--format", "json"], {"XDG_RUNTIME_DIR": ""})
        stdout, stderr = cli.communicate(timeout=TIMEOUT)
        self.assertEqual((cli.returncode, payload(stdout)["operation"]["pid"]), (0, cli.pid), stderr)

    def test_a_dashboard_without_a_daemon_serves_command_lines_until_it_closes(self):
        dashboard = self.dashboard(self.sandbox.with_gate())
        cli = self.sandbox.start(["home", "switch", "--format", "json"])
        self.sandbox.wait_for_gate()
        # A second ui launch reaches the same process.
        self.assertEqual(self.sandbox.run(["ui"]).returncode, 0)
        dashboard.wait_for("stderr", "Brought the dashboard forward.")
        dashboard.close_window()
        dashboard.wait_for("stderr", "The dashboard closed; the operation keeps running.")
        self.assertEqual(dashboard.process.poll(), None)
        self.sandbox.open_gate()
        stdout, stderr = cli.communicate(timeout=TIMEOUT)
        self.assertEqual(cli.returncode, 0, stderr)
        self.assertEqual(payload(stdout)["operation"]["pid"], dashboard.pid)
        # It ends once the Window is closed and nothing runs.
        self.assertEqual(dashboard.process.wait(TIMEOUT), 0)

    def test_the_tray_waits_for_a_dashboard_and_a_second_tray_fails(self):
        dashboard = self.dashboard()
        waiting = Instance(self.sandbox, ["tray"])
        self.instances.append(waiting)
        waiting.wait_for("stderr", "Waiting for the dashboard to close before starting the tray.")
        dashboard.close_window()
        self.assertEqual(dashboard.process.wait(TIMEOUT), 0)
        waiting.wait_for("stdout", "Nix Manager is running in the tray.")
        second = self.sandbox.run(["tray"])
        self.assertEqual(second.returncode, 1)
        self.assertIn("Nix Manager is already running in the tray.", second.stderr)
        self.assertEqual(waiting.stop(), 0)


class SetupTests(unittest.TestCase):
    """`ui` before there is a checkout, and with one that does not load."""

    def setUp(self):
        self.sandbox = Sandbox()
        self.instances = []

    def tearDown(self):
        for instance in self.instances:
            if instance.process.poll() is None:
                instance.process.kill()
            instance.process.wait(TIMEOUT)
            instance.close()
        self.sandbox.close()

    def served(self, args, env):
        instance = Instance(self.sandbox, args, env, ready=("stdout", "RUNIC_APPLICATION_URL="))
        self.instances.append(instance)
        return instance

    def test_ui_without_a_checkout_serves_the_setup_assistant_until_it_is_closed(self):
        for args in (["ui"], ["ui", "--no-open"]):
            with self.subTest(args=args):
                setup = self.served(args, self.sandbox.without_checkout())
                self.assertEqual(setup.page(), "setup.html")
                self.assertEqual(setup.process.poll(), None)
                setup.close_window()
                # Closed before it created anything, the assistant ends successfully.
                self.assertEqual(setup.process.wait(TIMEOUT), 0, setup.lines)
                setup.close()
                self.assertFalse((self.sandbox.root / "home" / "nixos-config").exists())
                self.assertFalse((self.sandbox.root / "state" / "nix-manager" / "checkout").exists())

    def test_a_configuration_that_does_not_load_is_shown_in_the_assistant_s_window_from_the_menu(self):
        broken = {"NIX_MANAGER_FLAKE": str(self.sandbox.root / "missing")}
        missing = f"Error: The flake directory {broken['NIX_MANAGER_FLAKE']} does not exist."
        # Started from the menu, standard output is not a terminal: the Window says why until it is closed.
        problem = self.served(["ui"], broken)
        self.assertEqual(problem.page(), "setup.html")
        problem.wait_for("stderr", missing)
        problem.close_window()
        self.assertEqual(problem.process.wait(TIMEOUT), 1)
        problem.close()
        # In a terminal, and with --no-open, the error is enough.
        primary, secondary = pty.openpty()
        try:
            in_terminal = subprocess.run([*COMMAND, "ui"], env=self.sandbox.env | broken, stdin=subprocess.DEVNULL, stdout=secondary,
                                         stderr=subprocess.PIPE, text=True, timeout=TIMEOUT, cwd=self.sandbox.root)
        finally:
            os.close(secondary)
            os.close(primary)
        self.assertEqual(in_terminal.returncode, 1)
        self.assertIn(missing, in_terminal.stderr)
        no_open = self.sandbox.run(["ui", "--no-open"], broken)
        self.assertEqual(no_open.returncode, 1)
        self.assertNotIn("RUNIC_APPLICATION_URL=", no_open.stdout)

    def test_ui_with_browser_serves_the_page_and_opens_it_in_the_default_browser(self):
        opened = self.sandbox.root / "opened"
        browser = self.sandbox.root / "bin" / "xdg-open"
        browser.write_text(f'#!{shutil.which("bash")}\nprintf "%s\\n" "$*" >> {opened}\n')
        browser.chmod(0o755)
        try:
            for env, page in (({}, "index.html"), (self.sandbox.without_checkout(), "setup.html")):
                with self.subTest(page=page):
                    opened.unlink(missing_ok=True)
                    served = self.served(["ui", "--browser"], env)
                    self.assertEqual(served.page(), page)
                    with served.changed:
                        address = next(line for line in served.lines["stdout"] if line.startswith("RUNIC_APPLICATION_URL="))
                    deadline = time.monotonic() + TIMEOUT
                    while not opened.exists() and time.monotonic() < deadline:
                        time.sleep(0.05)
                    self.assertEqual(opened.read_text().strip(), address.split("=", 1)[1])
                    served.close_window()
                    self.assertEqual(served.process.wait(TIMEOUT), 0, served.lines)
                    served.close()
        finally:
            browser.unlink()


class BrowserTests(unittest.TestCase):
    """`ui --browser` ends once its last tab has been closed for a second, so a reload keeps it. A daemon that runs
    meanwhile keeps running."""

    def setUp(self):
        self.sandbox = Sandbox()
        self.instances = []
        self.tabs = []
        # The default browser opens nothing; the tests are the browser.
        browser = self.sandbox.root / "bin" / "xdg-open"
        browser.write_text(f'#!{shutil.which("bash")}\nexit 0\n')
        browser.chmod(0o755)

    def tearDown(self):
        for tab in self.tabs:
            tab.close()
        for instance in self.instances:
            if instance.process.poll() is None:
                instance.process.kill()
            instance.process.wait(TIMEOUT)
            instance.close()
        self.sandbox.close()

    def browse(self, env=None):
        instance = Instance(self.sandbox, ["ui", "--browser"], env, ready=("stdout", "RUNIC_APPLICATION_URL="))
        self.instances.append(instance)
        return instance

    def tab(self, instance):
        with instance.changed:
            line = next(line for line in instance.lines["stdout"] if line.startswith("RUNIC_APPLICATION_URL="))
        tab = Tab(line.split("=", 1)[1])
        self.tabs.append(tab)
        return tab

    def assertServes(self, instance, seconds=3):
        time.sleep(seconds)
        self.assertIsNone(instance.process.poll(), instance.lines)

    def assertEnds(self, instance):
        # The grace period is a second, and the page is looked at four times a second.
        self.assertEqual(instance.process.wait(15), 0, instance.lines)

    def test_the_dashboard_ends_when_its_last_tab_closes_and_a_reload_keeps_it(self):
        served = self.browse()
        first = self.tab(served)
        self.assertServes(served)
        second = first.reload()
        self.tabs.append(second)
        self.assertServes(served)
        second.close()
        self.assertEnds(served)

    def test_with_the_daemon_running_only_the_browser_session_ends(self):
        daemon = tray(self.sandbox)
        self.instances.append(daemon)
        served = self.browse()
        tab = self.tab(served)
        self.assertServes(served, 2)
        tab.close()
        self.assertEnds(served)
        time.sleep(2)
        self.assertIsNone(daemon.process.poll(), daemon.lines)
        result = self.sandbox.run(["home", "build"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone(daemon.process.poll(), daemon.lines)

    def test_the_setup_assistant_ends_when_its_last_tab_closes(self):
        served = self.browse(self.sandbox.without_checkout())
        tab = self.tab(served)
        self.assertServes(served)
        tab.close()
        self.assertEnds(served)
        self.assertFalse((self.sandbox.root / "home" / "nixos-config").exists())


if __name__ == "__main__":
    unittest.main()
