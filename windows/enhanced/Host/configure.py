#!/usr/bin/python3
"""Configure an existing Omarchy VM for the standalone DMA-BUF renderer.

Run as root through sudo or pkexec after building omarchy-windows-dmabuf.
Configuration is backed up and validated; starting/restarting the VM is separate.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import stat
import subprocess
import tempfile

ROOT = Path('/var/lib/omarchy/windows')


def trusted(path, directory=False):
  info = path.lstat()
  expected = stat.S_ISDIR if directory else stat.S_ISREG
  if not expected(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
    raise SystemExit(f'Untrusted root configuration: {path}')
  return info


def arguments(value, uid, gid):
  patterns = [
    r'-spice\s+unix=on,addr=/storage/omarchy-enhanced/spice.sock,disable-ticketing=on',
    r'-vga\s+(?:qxl|virtio)',
    r'-run-with\s+user=\d+:\d+',
    r'-device\s+virtio-serial-pci,id=omarchy-serial',
    r'-chardev\s+socket,id=omarchy-enhanced,\S+',
    r'-device\s+virtserialport,bus=omarchy-serial\.0,\S+',
  ]
  for pattern in patterns:
    value = re.sub(r'(?<!\S)' + pattern + r'(?=\s|$)', '', value)
  return ' '.join(value.split()) + (
    f' -vga virtio -run-with user={uid}:{gid}'
    ' -device virtio-serial-pci,id=omarchy-serial'
    ' -chardev socket,id=omarchy-enhanced,path=/storage/omarchy-enhanced/enhanced.sock,server=on,wait=off'
    ' -device virtserialport,bus=omarchy-serial.0,chardev=omarchy-enhanced,name=org.omarchy.enhanced.0,id=omarchy-port')


def replace_arguments(text, uid, gid):
  pattern = r'(?m)^      ARGUMENTS: (".*")$'
  matches = list(re.finditer(pattern, text))
  if len(matches) != 1:
    raise SystemExit('Expected exactly one quoted ARGUMENTS entry')
  value = json.loads(matches[0][1])
  return re.sub(pattern, lambda _: '      ARGUMENTS: ' + json.dumps(arguments(value, uid, gid)), text)


def atomic(path, text, info):
  fd, name = tempfile.mkstemp(prefix='.rendering-', dir=ROOT)
  try:
    with os.fdopen(fd, 'w') as output:
      output.write(text)
      output.flush()
      os.fchmod(output.fileno(), stat.S_IMODE(info.st_mode))
      os.fchown(output.fileno(), info.st_uid, info.st_gid)
    os.replace(name, path)
  finally:
    Path(name).unlink(missing_ok=True)


def main():
  parser = argparse.ArgumentParser(description=__doc__)
  parser.add_argument('--render-node', type=Path, required=True, help='Stable /dev/dri/by-path PCI render-device path')
  args = parser.parse_args()
  if os.geteuid() != 0:
    raise SystemExit('Run configure.py through sudo or pkexec')
  uid = int(os.environ.get('PKEXEC_UID', os.environ.get('SUDO_UID', '0')))
  if uid <= 0:
    raise SystemExit('Cannot identify the authorizing VM owner')
  gid = pwd.getpwuid(uid).pw_gid
  node = args.render_node.absolute()
  if node.parent != Path('/dev/dri/by-path') or not re.fullmatch(r'pci-[0-9a-f:.]+-render', node.name):
    raise SystemExit('Choose a PCI render node under /dev/dri/by-path')
  if not stat.S_ISCHR(node.stat().st_mode):
    raise SystemExit('Render node is not a character device')
  for parent in [Path('/var'), Path('/var/lib'), ROOT]:
    trusted(parent, directory=True)
  main_file = ROOT / 'docker-compose.yml'
  display_file = ROOT / 'docker-compose.display.yml'
  oem_file = ROOT / 'docker-compose.oem.yml'
  files = [main_file] + ([oem_file] if oem_file.exists() else []) + [display_file]
  metadata = {p: trusted(p) for p in files}
  originals = {p: p.read_text() for p in files}
  subprocess.run(['/usr/bin/docker', 'image', 'inspect', 'omarchy-windows-dmabuf'], stdout=subprocess.DEVNULL, check=True)
  compose = ['/usr/bin/docker-compose']
  for path in files:
    compose.extend(['-f', str(path)])
  config = json.loads(subprocess.check_output(compose + ['config', '--format', 'json']))
  storage = [v['source'] for v in config['services']['windows']['volumes'] if v['target'] == '/storage']
  expected = str(ROOT / f'mounts/users/{uid}/storage')
  if storage != [expected]:
    raise SystemExit('VM storage does not match the authorizing account; use the Omarchy VM launcher to prepare its protected mounts first')
  changes = {main_file: replace_arguments(originals[main_file], uid, gid)}
  if oem_file in originals:
    changes[oem_file] = replace_arguments(originals[oem_file], uid, gid)
  changes[display_file] = f'''services:
  windows:
    image: omarchy-windows-dmabuf
    environment:
      OMARCHY_RENDERER: "dmabuf"
      OMARCHY_UID: "{uid}"
      OMARCHY_GID: "{gid}"
      DISPLAY: "dbus,addr=unix:path=/storage/omarchy-enhanced/display-bus.sock,gl=on,rendernode=/dev/dri/renderD128"
    devices:
      - source: "{node}"
        target: "/dev/dri/renderD128"
        permissions: "rwm"
'''
  stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
  try:
    for path, content in changes.items():
      shutil.copy2(path, path.with_name(path.name + '.before-rendering-' + stamp))
      atomic(path, content, metadata[path])
    subprocess.run(compose + ['config', '--quiet'], check=True)
  except BaseException:
    for path, content in originals.items():
      atomic(path, content, metadata[path])
    raise
  print('DMA-BUF configuration validated. Restart the VM after starting the private host display bus.')


if __name__ == '__main__':
  main()
