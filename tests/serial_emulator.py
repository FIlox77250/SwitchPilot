"""POSIX pseudo-terminal only; never opens a physical console device.

Used by ssh_emulator.py to exercise the real System.IO.Ports channel on Linux.
Windows runners leave these tests skipped; real COM operation remains a hardware check.
"""
import os
import threading
import time


def start():
    if os.name != "posix":
        return {}, lambda: None
    import pty
    import tty
    master, slave = pty.openpty()
    tty.setraw(slave)
    path = os.ttyname(slave)

    def console():
        buffer = b""
        try:
            while True:
                data = os.read(master, 4096)
                if not data:
                    break
                buffer += data
                while b"\r" in buffer:
                    raw, buffer = buffer.split(b"\r", 1)
                    command = raw.decode("ascii", errors="replace").strip()
                    output = ""
                    if command == "show interfaces switchport":
                        os.write(master, (command + "\r\n").encode())
                        # A 26-port dump at 9600 baud exceeds the old 25s command
                        # deadline even though the console continuously delivers data.
                        for port in range(1, 27):
                            block = (f"Name: Fa0/{port}\r\nAdministrative Mode: static access\r\n"
                                     "Operational Mode: static access\r\n")
                            block += "Negotiation of Trunking: Off\r\n" * 30
                            os.write(master, block.encode())
                            time.sleep(1)
                        os.write(master, b"SERIAL-SW#")
                        continue
                    if command.startswith("show interfaces"):
                        output = "%LINK-3-UPDOWN: Interface Fa0/2 changed state\r\nFastEthernet0/1 is up, line protocol is up\r\nFull-duplex, 100Mb/s\r\n0 input errors, 0 CRC, 0 collisions\r\n"
                    elif command == "show denied":
                        output = "Command rejected: test refusal\r\n"
                    response = (command + "\r\n" + output + "SERIAL-SW#").encode()
                    split = len(response) // 2
                    os.write(master, response[:split])
                    os.write(master, response[split:])
        except OSError:
            pass

    threading.Thread(target=console, daemon=True).start()

    def close():
        os.close(master)
        os.close(slave)

    return {"SWITCHPILOT_SERIAL_TEST_PORT": path}, close
