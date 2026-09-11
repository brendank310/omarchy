#!/usr/bin/python3
"""Exercise control-stream and descriptor transport without touching pixel data."""
import array
import importlib.util
import os
from pathlib import Path
import socket
import threading
import unittest

spec = importlib.util.spec_from_file_location('relay', Path(__file__).with_name('listener-relay.py'))
relay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(relay)


class RelayTests(unittest.TestCase):
  def test_stream_and_descriptors(self):
    client, server = socket.socketpair()
    client.settimeout(5)
    worker = threading.Thread(target=relay.session, args=(server,))
    worker.start()
    received = []
    try:
      marker, control, flags, _ = client.recvmsg(1, socket.CMSG_SPACE(4), socket.MSG_CMSG_CLOEXEC)
      self.assertEqual(marker, b'F')
      self.assertFalse(flags & socket.MSG_CTRUNC)
      endpoints = array.array('i')
      endpoints.frombytes(control[0][2])
      with socket.socket(fileno=endpoints[0]) as qemu:
        qemu.settimeout(5)
        client.sendall(b'\x00AUTH EXTERNAL 31303030\r\n')
        self.assertEqual(qemu.recv(1024), b'\x00AUTH EXTERNAL 31303030\r\n')
        qemu.sendall(b'OK test-guid\r\n')
        self.assertEqual(client.recv(1024), b'OK test-guid\r\n')
        # Exercise the maximum descriptor batch plus kernel-added credentials.
        originals = [os.memfd_create('relay-test', os.MFD_CLOEXEC) for _ in range(16)]
        try:
          for i, fd in enumerate(originals):
            os.write(fd, str(i).encode())
          qemu.sendmsg([b'D'], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, array.array('i', originals))])
          marker, control, flags, _ = client.recvmsg(1, socket.CMSG_SPACE(64), socket.MSG_CMSG_CLOEXEC)
          self.assertEqual(marker, b'D')
          self.assertFalse(flags & socket.MSG_CTRUNC)
          for level, kind, data in control:
            if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS:
              values = array.array('i'); values.frombytes(data); received.extend(values)
          self.assertEqual(len(received), 16)
          for i, fd in enumerate(received):
            self.assertEqual(os.fstat(fd).st_ino, os.fstat(originals[i]).st_ino)
            self.assertEqual(os.pread(fd, 10, 0), str(i).encode())
            self.assertFalse(os.get_inheritable(fd))
          # Force multiple receive/send iterations and exercise backpressure.
          payload = bytes(range(256)) * 4096
          sender = threading.Thread(target=qemu.sendall, args=(payload,))
          sender.start()
          result = bytearray()
          while len(result) < len(payload):
            result.extend(client.recv(8192))
          sender.join(5)
          self.assertFalse(sender.is_alive())
          self.assertEqual(result, payload)
        finally:
          for fd in originals + received:
            os.close(fd)
      self.assertEqual(client.recv(1), b'')
    finally:
      client.close()
      worker.join(5)
      self.assertFalse(worker.is_alive(), 'relay threads must stop when either peer closes')


if __name__ == '__main__':
  unittest.main()
