#!/usr/bin/python3
"""Bridge QEMU listener authentication across a container PID namespace.

Only Unix socket bytes and SCM_RIGHTS descriptors are forwarded. DMA-BUF pixel
storage is never mapped, read, compressed or copied. The private socket directory
is the access boundary; run this process as the same UID as QEMU and the viewer.
"""
import argparse
import array
import os
from pathlib import Path
import socket
import stat
import threading


def forward(source, destination):
  try:
    while True:
      # SO_PASSCRED adds a ucred beside SCM_RIGHTS. Reserve space for both and
      # prevent forwarded descriptors from leaking into a future exec.
      capacity = socket.CMSG_SPACE(16 * array.array('i').itemsize) + socket.CMSG_SPACE(12)
      data, control, flags, _ = source.recvmsg(65536, capacity, socket.MSG_CMSG_CLOEXEC)
      descriptors = array.array('i')
      for level, kind, payload in control:
        if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS:
          descriptors.frombytes(payload[:len(payload) - len(payload) % descriptors.itemsize])
      try:
        if flags & socket.MSG_CTRUNC:
          raise RuntimeError('Truncated descriptor message')
        if not data:
          return
        rights = [(socket.SOL_SOCKET, socket.SCM_RIGHTS, descriptors)] if descriptors else []
        sent = destination.sendmsg([data], rights)
        if sent < len(data):
          destination.sendall(data[sent:])
      finally:
        for fd in descriptors:
          os.close(fd)
  except (OSError, RuntimeError) as error:
    print('Listener relay:', error, flush=True)
  finally:
    for channel in (source, destination):
      try:
        channel.shutdown(socket.SHUT_RDWR)
      except OSError:
        pass


def session(client):
  with client:
    local, remote = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
    with local, remote:
      local.setsockopt(socket.SOL_SOCKET, socket.SO_PASSCRED, 1)
      remote.setsockopt(socket.SOL_SOCKET, socket.SO_PASSCRED, 1)
      # QEMU receives this endpoint through RegisterListener. Its peer is this
      # process, whose PID is visible inside the container namespace.
      client.sendmsg([b'F'], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, array.array('i', [remote.fileno()]))])
      remote.close()
      incoming = threading.Thread(target=forward, args=(client, local), daemon=True)
      incoming.start()
      forward(local, client)
      incoming.join()


def serve_client(client, slots):
  try:
    session(client)
  except OSError as error:
    print('Listener relay handshake:', error, flush=True)
  finally:
    slots.release()


def main():
  parser = argparse.ArgumentParser(description=__doc__)
  parser.add_argument('socket_path', type=Path)
  args = parser.parse_args()
  path = args.socket_path.absolute()
  parent = path.parent.lstat()
  if not stat.S_ISDIR(parent.st_mode) or parent.st_uid != os.getuid() or parent.st_mode & 0o077:
    raise SystemExit('Relay directory must belong to this user and have mode 0700')
  if path.exists():
    with socket.socket(socket.AF_UNIX) as existing:
      try:
        existing.connect(str(path))
      except ConnectionRefusedError:
        path.unlink()
      else:
        raise SystemExit('Listener relay is already running')
  os.umask(0o077)
  slots = threading.BoundedSemaphore(8)
  with socket.socket(socket.AF_UNIX) as server:
    server.bind(str(path))
    server.listen(4)
    print('Listener relay ready:', path, flush=True)
    try:
      while True:
        client, _ = server.accept()
        if slots.acquire(blocking=False):
          threading.Thread(target=serve_client, args=(client, slots), daemon=True).start()
        else:
          client.close()
    finally:
      path.unlink(missing_ok=True)


if __name__ == '__main__':
  main()
