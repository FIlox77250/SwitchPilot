"""Loopback-only Cisco CLI emulator for integration tests. Requires paramiko.

Runs dotnet test as a child; fake credentials and host key exist only for this run.
Never use this emulator as a network service.
"""
import os
from pathlib import Path
import socket
import subprocess
import sys
import threading

import paramiko

FIXTURES = Path(__file__).parent / "SwitchPilot.Tests" / "Fixtures"
HOST_KEY = paramiko.RSAKey.generate(2048)


class Server(paramiko.ServerInterface):
    def __init__(self):
        self.ready = threading.Event()

    def check_auth_password(self, username, password):
        return paramiko.AUTH_SUCCESSFUL if (username, password) == ("test-technician", "test-only-password") else paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_pty_request(self, *args):
        return True

    def check_channel_shell_request(self, channel):
        self.ready.set()
        return True


def connection(client):
    transport = paramiko.Transport(client)
    try:
        transport.add_server_key(HOST_KEY)
        server = Server()
        transport.start_server(server=server)
        channel = transport.accept(10)
        if not channel or not server.ready.wait(10):
            return
        mode = ">"
        secret_pending = False
        channel.sendall(b"Authorized lab emulator only\r\nLAB-SW>")
        buffer = ""
        while transport.is_active():
            data = channel.recv(4096)
            if not data:
                break
            buffer += data.decode("utf-8")
            while "\n" in buffer:
                line, buffer = buffer.split("\n", 1)
                line = line.rstrip("\r")
                if secret_pending:
                    secret_pending = False
                    mode = "#" if line == "test-enable" else ">"
                    channel.sendall(("\r\nLAB-SW" + mode).encode())
                    continue
                channel.sendall((line + "\r\n").encode())
                if line == "enable":
                    secret_pending = True
                    channel.sendall(b"Password:")
                    continue
                if line == "show hang":
                    continue
                if line == "show confirm":
                    channel.sendall(b"Proceed? [confirm]")
                    continue
                output = ""
                if line == "show version":
                    output = "Cisco IOS Software, Version 15.2(4)E10, RELEASE SOFTWARE\r\nModel number : WS-C2960+24TC-L"
                elif line == "show interfaces status":
                    output = (FIXTURES / "interfaces-status.txt").read_text()
                elif line == "show interfaces description":
                    output = "Interface Status Protocol Description\nFa0/14 up up Bureau 214 - prise murale A-014\nGi0/1 up up Uplink distribution"
                elif line == "show interfaces switchport" or line.endswith(" switchport"):
                    output = "Name: Fa0/14\nAdministrative Mode: static access\nOperational Mode: static access\nName: Gi0/1\nAdministrative Mode: trunk\nOperational Mode: trunk"
                elif line.startswith(("show mac address-table", "show mac-address-table")):
                    output = (FIXTURES / "mac-table.txt").read_text()
                elif line == "show vlan brief":
                    output = (FIXTURES / "vlans.txt").read_text()
                elif line.startswith("show interfaces Fa"):
                    output = "FastEthernet0/14 is up, line protocol is up\n  Full-duplex, 100Mb/s\n  7 input errors, 3 CRC, 0 frame\n  0 collisions"
                elif line == "configure terminal":
                    mode = "(config)#"
                elif line.startswith("interface "):
                    mode = "(config-if)#"
                elif line == "end":
                    mode = "#"
                elif line == "write memory":
                    output = "Building configuration...\r\n[OK]"
                elif line == "show running-config":
                    output = "Building configuration...\r\nhostname LAB-SW\r\nusername test secret test-config-secret\r\nend"
                elif line.startswith("show cable-diagnostics"):
                    output = "% Invalid input detected at '^' marker."
                elif not (line.startswith("terminal ") or line in ("shutdown", "no shutdown") or line.startswith("description ")):
                    output = "% Invalid input detected at '^' marker."
                response = output.replace("\r\n", "\n").replace("\n", "\r\n") + "\r\nLAB-SW" + mode
                # Fragment real network writes so the SSH client's prompt framing is exercised.
                split = max(1, len(response) // 2)
                channel.sendall(response[:split].encode())
                channel.sendall(response[split:].encode())
    except (EOFError, OSError, paramiko.SSHException):
        pass
    finally:
        transport.close()


def main():
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()

    def accept():
        while True:
            try:
                client, _ = listener.accept()
            except OSError:
                return
            threading.Thread(target=connection, args=(client,), daemon=True).start()

    threading.Thread(target=accept, daemon=True).start()
    env = dict(os.environ, SWITCHPILOT_SSH_TEST_PORT=str(listener.getsockname()[1]))
    try:
        result = subprocess.run([sys.argv[1] if len(sys.argv) > 1 else "dotnet", "test", "tests/SwitchPilot.Tests", "-c", "Release", "--logger", "trx;LogFileName=integration.trx"], env=env)
        return result.returncode
    finally:
        listener.close()


if __name__ == "__main__":
    raise SystemExit(main())
