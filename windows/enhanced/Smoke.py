#!/usr/bin/python3
"""Guest integration probe, not a renderer or production menu client."""

import argparse
import json
import socket
import struct
import time


def read_exact(connection, size):
  result = bytearray()
  while len(result) < size:
    part = connection.recv(size - len(result))
    if not part:
      raise EOFError("Guest disconnected")
    result.extend(part)
  return result


def read_frame(connection):
  size, = struct.unpack("<I", read_exact(connection, 4))
  if not 0 < size <= 1024 * 1024:
    raise ValueError("Invalid frame size")
  return json.loads(read_exact(connection, size))


def send_frame(connection, value):
  data = json.dumps(value).encode("utf-8")
  connection.sendall(struct.pack("<I", len(data)) + data)


def probe(path, launch):
  deadline = time.monotonic() + 30
  epoch = None
  got_catalog = got_snapshot = False
  got_launch = launch is None
  with socket.socket(socket.AF_UNIX) as connection:
    connection.settimeout(10)
    connection.connect(path)
    while time.monotonic() < deadline:
      frame = read_frame(connection)
      if frame.get("v") != 1:
        raise ValueError("Unexpected protocol version")
      if frame.get("type") == "hello":
        print("ok - guest handshake; rendering:", frame.get("rendering"))
      elif frame.get("type") == "session.unavailable":
        raise RuntimeError("Sign in to the Windows console before running this probe")
      elif frame.get("type") == "session.ready":
        epoch = frame["session"]
        got_catalog = got_snapshot = False
        send_frame(connection, {"v": 1, "id": "catalog", "session": epoch, "method": "apps.list"})
      elif frame.get("id") == "catalog":
        if "error" in frame:
          raise RuntimeError(frame["error"])
        apps = frame["result"]
        print("ok - application catalog:", ", ".join(app["id"] for app in apps) or "empty")
        got_catalog = True
        if launch:
          if not any(app["id"] == launch for app in apps):
            raise RuntimeError("Requested test application is not installed")
          send_frame(connection, {"v": 1, "id": "launch", "session": epoch,
                                  "method": "app.launch", "appId": launch})
      elif frame.get("id") == "launch":
        if "error" in frame:
          raise RuntimeError(frame["error"])
        print("ok - process creation accepted:", frame["result"])
        got_launch = True
      elif frame.get("type") == "windows.snapshot":
        if not got_snapshot:
          # Avoid printing potentially private document titles in test output.
          print("ok - window snapshot:", len(frame["windows"]), "windows")
        got_snapshot = True
      if got_catalog and got_snapshot and got_launch:
        return
  raise TimeoutError("Incomplete guest handshake/catalog/snapshot exchange")


if __name__ == "__main__":
  parser = argparse.ArgumentParser(description=__doc__)
  parser.add_argument("socket", help="Protected QEMU VirtIO channel Unix socket")
  parser.add_argument("--launch", choices=["word", "excel", "outlook"], help="Also launch one installed app")
  arguments = parser.parse_args()
  probe(arguments.socket, arguments.launch)
