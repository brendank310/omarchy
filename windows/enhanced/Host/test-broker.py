#!/usr/bin/python3
import asyncio
import contextlib
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('broker', Path(__file__).with_name('broker.py'))
broker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(broker)


class BrokerTests(unittest.IsolatedAsyncioTestCase):
  async def asyncSetUp(self):
    self.directory = tempfile.TemporaryDirectory()
    self.root = Path(self.directory.name)
    self.requests = asyncio.Queue()
    self.guest_connected = asyncio.Event()
    self.connections = []
    self.snapshot = {'v': 1, 'type': 'windows.snapshot', 'windows': [
      {'id': 'aa', 'processId': 10, 'minimized': False},
      {'id': 'bb', 'processId': 20, 'minimized': False}]}
    async def guest(reader, writer):
      self.guest_writer = writer
      self.connections.append(writer)
      for event in ({'v': 1, 'type': 'hello'}, {'v': 1, 'type': 'session.ready', 'session': 'epoch'}, self.snapshot):
        writer.write(broker.encode(event))
      await writer.drain()
      self.guest_connected.set()
      try:
        while True:
          await self.requests.put(await broker.read_frame(reader))
      except asyncio.IncompleteReadError:
        pass
    self.guest_server = await asyncio.start_unix_server(guest, path=self.root / 'guest')
    self.bridge = broker.Broker(str(self.root / 'guest'))
    self.host_server = await asyncio.start_unix_server(self.bridge.connect, path=self.root / 'host')
    self.upstream = asyncio.create_task(self.bridge.upstream())
    await self.guest_connected.wait()

  async def asyncTearDown(self):
    self.upstream.cancel()
    with contextlib.suppress(asyncio.CancelledError):
      await self.upstream
    for writer in self.connections:
      writer.close()
      with contextlib.suppress(OSError):
        await writer.wait_closed()
    for server in (self.host_server, self.guest_server):
      server.close()
      await server.wait_closed()
    self.directory.cleanup()

  async def client(self):
    reader, writer = await asyncio.open_unix_connection(self.root / 'host')
    self.connections.append(writer)
    kinds = []
    while 'windows.snapshot' not in kinds:
      event = await asyncio.wait_for(broker.read_frame(reader), 2)
      kinds.append(event['type'])
    self.assertIn('hello', kinds)
    self.assertIn('session.ready', kinds)
    return reader, writer

  async def send(self, writer, identifier, method, **fields):
    writer.write(broker.encode({'v': 1, 'id': identifier, 'method': method, 'session': 'epoch', **fields}))
    await writer.drain()

  async def test_two_clients_with_same_request_id_get_their_own_reply(self):
    a, aw = await self.client()
    b, bw = await self.client()
    await self.send(aw, 'same', 'window.focus', windowId='aa', processId=10)
    await self.send(bw, 'same', 'window.focus', windowId='bb', processId=20)
    requests = [await asyncio.wait_for(self.requests.get(), 2) for _ in range(2)]
    self.assertNotEqual(requests[0]['id'], requests[1]['id'])
    for request in reversed(requests):
      self.guest_writer.write(broker.encode({'v': 1, 'id': request['id'], 'result': request['windowId']}))
    await self.guest_writer.drain()
    self.assertEqual(await broker.read_frame(a), {'v': 1, 'id': 'same', 'result': 'aa'})
    self.assertEqual(await broker.read_frame(b), {'v': 1, 'id': 'same', 'result': 'bb'})

  async def test_resizes_include_both_applications_and_disconnect_keeps_other_client(self):
    a, aw = await self.client()
    b, bw = await self.client()
    await self.send(aw, 'a', 'window.resize', windowId='aa', processId=10, width=621, height=670)
    first = await asyncio.wait_for(self.requests.get(), 2)
    self.assertEqual(first['method'], 'windows.layout')
    await self.send(bw, 'b', 'window.resize', windowId='bb', processId=20, width=621, height=670)
    second = await asyncio.wait_for(self.requests.get(), 2)
    self.assertEqual([w['windowId'] for w in second['windows']], ['aa', 'bb'])
    self.assertNotIn('windowId', second)
    aw.close(); await aw.wait_closed()
    self.guest_writer.write(broker.encode(self.snapshot)); await self.guest_writer.drain()
    self.assertEqual((await asyncio.wait_for(broker.read_frame(b), 2))['type'], 'windows.snapshot')
    await self.send(bw, 'focus', 'window.focus', windowId='bb', processId=20)
    self.assertEqual((await asyncio.wait_for(self.requests.get(), 2))['windowId'], 'bb')

  async def test_stale_resize_cannot_change_layout(self):
    reader, writer = await self.client()
    writer.write(broker.encode({'v': 1, 'id': 'old', 'session': 'old', 'method': 'window.resize',
      'windowId': 'aa', 'processId': 10, 'width': 600, 'height': 600}))
    await writer.drain()
    self.assertEqual((await broker.read_frame(reader))['error'], 'stale_session')
    self.assertFalse(self.bridge.layouts)
    self.assertTrue(self.requests.empty())


if __name__ == '__main__':
  unittest.main()
