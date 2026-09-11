#!/usr/bin/python3
"""Share one guest metadata connection between independent application viewers.

This process transports JSON metadata only. QEMU DMA-BUF descriptors and input
remain on each viewer's direct D-Bus connection.
"""
import argparse
import asyncio
import contextlib
from collections import deque
import fcntl
import json
import os
from pathlib import Path
import socket
import stat
import struct
import subprocess
import sys
import time

MAX_FRAME = 1024 * 1024


async def read_frame(reader):
  size, = struct.unpack('<I', await reader.readexactly(4))
  if not 0 < size <= MAX_FRAME:
    raise ValueError('Invalid metadata frame size')
  value = json.loads(await reader.readexactly(size))
  if not isinstance(value, dict):
    raise ValueError('Metadata frame must be an object')
  return value


def encode(value):
  data = json.dumps(value, separators=(',', ':')).encode()
  if len(data) > MAX_FRAME:
    raise ValueError('Metadata frame too large')
  return struct.pack('<I', len(data)) + data


class Client:
  def __init__(self, writer):
    self.writer = writer
    self.queue = deque()
    self.wake = asyncio.Event()

  def publish(self, event):
    if event.get('type') == 'windows.snapshot':
      self.queue = deque(item for item in self.queue if item.get('type') != 'windows.snapshot')
    if len(self.queue) >= 64:
      self.writer.close()
    else:
      self.queue.append(event)
      self.wake.set()

  async def send(self):
    while True:
      await self.wake.wait()
      self.writer.write(encode(self.queue.popleft()))
      if not self.queue:
        self.wake.clear()
      await asyncio.wait_for(self.writer.drain(), 3)


class Broker:
  def __init__(self, guest):
    self.guest = guest
    self.clients = set()
    self.cache = {}
    self.writer = None
    self.lock = asyncio.Lock()
    self.serial = 0
    self.pending = {}
    self.layouts = {}
    self.owners = {}

  async def upstream(self):
    while True:
      try:
        reader, self.writer = await asyncio.open_unix_connection(self.guest)
        while True:
          event = await read_frame(reader)
          kind = event.get('type')
          if kind == 'hello':
            self.cache.clear()
          if kind in ('hello', 'session.ready', 'session.unavailable'):
            self.cache = {key: value for key, value in self.cache.items() if key == 'hello'}
            self.layouts.clear()
            self.owners.clear()
          if kind:
            if kind in ('hello', 'session.ready', 'session.unavailable', 'agent.ready', 'windows.snapshot'):
              self.cache[kind] = event
            if kind == 'windows.snapshot':
              live = {(w['id'], w['processId']) for w in event['windows'] if not w['minimized']}
              self.layouts = {key: value for key, value in self.layouts.items() if key in live}
              self.owners = {key: value for key, value in self.owners.items() if key in live}
            for client in tuple(self.clients):
              client.publish(event)
          elif event.get('id') in self.pending:
            client, original, _ = self.pending.pop(event['id'])
            if client in self.clients:
              client.publish({**event, 'id': original})
      except (OSError, ValueError, asyncio.IncompleteReadError) as error:
        print('Guest metadata disconnected:', error, flush=True)
      finally:
        if self.writer:
          self.writer.close()
        self.writer = None
        self.cache.clear()
        self.layouts.clear()
        self.owners.clear()
        for client, original, _ in self.pending.values():
          client.publish({'v': 1, 'id': original, 'error': 'guest_disconnected'})
        self.pending.clear()
        for client in tuple(self.clients):
          client.publish({'v': 1, 'type': 'session.unavailable'})
      await asyncio.sleep(1)

  async def request(self, client, event):
    original = event.get('id')
    if event.get('v') != 1 or not isinstance(original, str) or not 0 < len(original) <= 128:
      raise ValueError('Invalid request envelope')
    if not self.writer:
      client.publish({'v': 1, 'id': original, 'error': 'guest_disconnected'})
      return
    if len(self.pending) >= 512 or sum(c is client for c, _, _ in self.pending.values()) >= 32:
      raise ValueError('Too many outstanding requests')
    if event.get('method') == 'window.resize':
      session = self.cache.get('session.ready', {}).get('session')
      if not session or event.get('session') != session:
        client.publish({'v': 1, 'id': original, 'error': 'stale_session'})
        return
      key = (event.get('windowId'), event.get('processId'))
      if not isinstance(key[0], str) or not isinstance(key[1], int):
        raise ValueError('Invalid window identity')
      if key in self.owners and self.owners[key] is not client:
        client.publish({'v': 1, 'id': original, 'error': 'window_already_viewed'})
        return
      if key not in self.layouts and len(self.layouts) >= 16:
        client.publish({'v': 1, 'id': original, 'error': 'too_many_windows'})
        return
      width, height = event.get('width'), event.get('height')
      if not isinstance(width, int) or not isinstance(height, int) or not 200 <= width <= 2048 or not 120 <= height <= 1536:
        raise ValueError('Invalid window size')
      self.owners[key] = client
      self.layouts[key] = {'windowId': key[0], 'processId': key[1], 'width': width, 'height': height}
      event = {**event, 'method': 'windows.layout', 'windows': list(self.layouts.values())}
      for field in ('windowId', 'processId', 'width', 'height'):
        event.pop(field, None)
    self.serial += 1
    identifier = f'broker-{self.serial}'
    self.pending[identifier] = (client, original, time.monotonic())
    try:
      async with self.lock:
        if not self.writer:
          raise ConnectionError('Guest disconnected')
        self.writer.write(encode({**event, 'id': identifier}))
        await asyncio.wait_for(self.writer.drain(), 3)
    except (OSError, asyncio.TimeoutError):
      self.pending.pop(identifier, None)
      client.publish({'v': 1, 'id': original, 'error': 'guest_disconnected'})

  async def expire(self):
    while True:
      await asyncio.sleep(1)
      for identifier, (client, original, started) in list(self.pending.items()):
        if time.monotonic() - started > 15:
          self.pending.pop(identifier, None)
          client.publish({'v': 1, 'id': original, 'error': 'guest_timeout'})

  async def connect(self, reader, writer):
    client = Client(writer)
    if len(self.clients) >= 32:
      writer.close()
      return
    self.clients.add(client)
    for kind in ('hello', 'session.ready', 'session.unavailable', 'agent.ready', 'windows.snapshot'):
      if kind in self.cache:
        client.publish(self.cache[kind])
    sender = asyncio.create_task(client.send())
    async def receive():
      while True:
        await self.request(client, await read_frame(reader))
    receiver = asyncio.create_task(receive())
    try:
      await asyncio.wait((sender, receiver), return_when=asyncio.FIRST_COMPLETED)
    finally:
      self.clients.discard(client)
      self.owners = {key: owner for key, owner in self.owners.items() if owner is not client}
      # Keep detached applications in their reserved regions until they close
      # or minimize, so they cannot cover another application's view.
      for task in (sender, receiver):
        task.cancel()
        with contextlib.suppress(asyncio.CancelledError, OSError, ValueError, asyncio.IncompleteReadError, asyncio.TimeoutError):
          await task
      writer.close()
      with contextlib.suppress(OSError):
        await writer.wait_closed()


async def serve(directory):
  os.umask(0o077)
  with open(directory / 'broker.lock', 'a') as lock:
    try:
      fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
      return
    path = directory / 'broker.sock'
    path.unlink(missing_ok=True)
    broker = Broker(str(directory / 'enhanced.sock'))
    server = await asyncio.start_unix_server(broker.connect, path=path)
    try:
      async with server:
        await asyncio.gather(server.serve_forever(), broker.upstream(), broker.expire())
    finally:
      path.unlink(missing_ok=True)


def ensure(directory):
  def connected():
    with socket.socket(socket.AF_UNIX) as connection:
      connection.settimeout(.2)
      try:
        connection.connect(str(directory / 'broker.sock'))
        return True
      except OSError:
        return False
  if connected():
    return
  with open(directory / 'broker.log', 'ab') as output:
    subprocess.Popen([sys.executable, str(Path(__file__).resolve()), str(directory)],
      stdin=subprocess.DEVNULL, stdout=output, stderr=output, start_new_session=True)
  for _ in range(100):
    if connected():
      return
    time.sleep(.05)
  raise RuntimeError('Metadata broker did not start; inspect broker.log')


def main():
  parser = argparse.ArgumentParser(description=__doc__)
  parser.add_argument('directory', type=Path)
  parser.add_argument('--ensure', action='store_true')
  args = parser.parse_args()
  directory = args.directory.absolute()
  info = directory.lstat()
  if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
    raise SystemExit('Broker directory must belong to this user with mode 0700')
  if args.ensure:
    ensure(directory)
  else:
    asyncio.run(serve(directory))


if __name__ == '__main__':
  main()
