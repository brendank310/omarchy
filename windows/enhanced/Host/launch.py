#!/usr/bin/python3
"""Launch or select a Windows application in the standalone Wayland renderer."""
import argparse
import os
from pathlib import Path
import socket
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from Smoke import read_frame, send_frame


def select(path, app, requested, list_only):
  with socket.socket(socket.AF_UNIX) as channel:
    channel.settimeout(30)
    channel.connect(str(path))
    deadline = time.monotonic() + 30
    session = None
    launched_pid = None
    launch_sent = False
    office_class = {'word': 'OpusApp', 'excel': 'XLMAIN', 'powerpoint': 'PPTFrameClass', 'outlook': 'rctrl_renwnd32'}.get(app)
    snapshots = []
    while time.monotonic() < deadline:
      event = read_frame(channel)
      if event.get('type') == 'session.ready':
        session = event['session']
      if event.get('id') == 'launch':
        if 'error' in event:
          raise RuntimeError(event.get('message', event['error']))
        launched_pid = event.get('result', {}).get('processId')
      if event.get('type') == 'windows.snapshot':
        snapshots = event['windows']
      if not session or event.get('type') != 'windows.snapshot':
        continue
      if list_only:
        for window in snapshots:
          print(f"{window['id']}\t{window['processId']}\t{window['title']}")
        return None
      candidates = [w for w in snapshots if (app or requested or not w['minimized']) and not w.get('ownerId')
        and w['className'] not in ('Progman', 'Shell_TrayWnd')]
      if app and not launch_sent:
        existing = [w for w in candidates if office_class and w['className'] == office_class]
        if existing:
          existing.sort(key=lambda w: (w['minimized'], not w['foreground'], w['zOrder']))
          return existing[0]['id']
        send_frame(channel, {'v': 1, 'id': 'launch', 'session': session, 'method': 'app.launch', 'appId': app})
        launch_sent = True
        continue
      if requested:
        candidates = [w for w in candidates if w['id'].lower() == requested.lower()]
      elif app:
        # Office can hand the launch to its existing process and exit the new
        # one. Its catalog IDs have stable top-level window classes.
        if office_class:
          candidates = [w for w in candidates if w['className'] == office_class]
        else:
          candidates = [w for w in candidates if launched_pid and w['processId'] == launched_pid]
      if candidates:
        candidates.sort(key=lambda w: (not w['foreground'], w['zOrder']))
        return candidates[0]['id']
    raise RuntimeError('No matching visible application window appeared within 30 seconds')


def main():
  parser = argparse.ArgumentParser(description=__doc__)
  choice = parser.add_mutually_exclusive_group()
  choice.add_argument('--app', help='Guest application catalog ID, for example word or excel')
  choice.add_argument('--window', help='Window ID from --list')
  choice.add_argument('--list', action='store_true', help='List visible guest application windows')
  choice.add_argument('--desktop', action='store_true', help='Show the whole Windows desktop, including the taskbar and sign-in screen')
  parser.add_argument('--start-vm', action='store_true', help='Start the configured VM through omarchy-windows-vm launch -k')
  parser.add_argument('--directory', type=Path, default=Path.home() / '.windows/omarchy-enhanced')
  parser.add_argument('--render-node', type=Path, default=Path('/dev/dri/by-path/pci-0000:00:02.0-render'))
  args = parser.parse_args()
  directory = args.directory.absolute()
  subprocess.run([sys.executable, str(HERE / 'start-bus.py'), str(directory)], check=True, stdout=subprocess.DEVNULL)
  if args.start_vm:
    probe = subprocess.run(['gdbus', 'call', '--address', 'unix:path=' + str(directory / 'display-bus.sock'),
      '--dest', 'org.freedesktop.DBus', '--object-path', '/org/freedesktop/DBus',
      '--method', 'org.freedesktop.DBus.NameHasOwner', 'org.qemu'], capture_output=True, text=True, timeout=5)
    if probe.returncode or probe.stdout.strip() != '(true,)':
      subprocess.run(['omarchy-windows-vm', 'launch', '-k'], check=True)
  subprocess.run([sys.executable, str(HERE / 'broker.py'), str(directory), '--ensure'], check=True)
  selected = 'desktop' if args.desktop else select(directory / 'broker.sock', args.app, args.window, args.list)
  if selected is None:
    return
  environment = os.environ.copy()
  # Mesa's PCI selector avoids numbering changes and accidental NVIDIA offload.
  if args.render_node.parent == Path('/dev/dri/by-path') and args.render_node.name.endswith('-render'):
    environment['DRI_PRIME'] = args.render_node.name.removesuffix('-render').replace(':', '_').replace('.', '_')
  address = 'unix:path=' + str(directory / 'display-bus.sock')
  os.execve(HERE / 'enhanced-viewer', [str(HERE / 'enhanced-viewer'), address,
    str(directory / 'broker.sock'), selected, str(directory / 'listener-relay.sock')], environment)


if __name__ == '__main__':
  try:
    main()
  except (OSError, RuntimeError, EOFError, subprocess.CalledProcessError) as error:
    raise SystemExit(f'Windows viewer: {error}')
