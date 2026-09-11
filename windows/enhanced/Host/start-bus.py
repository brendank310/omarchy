#!/usr/bin/python3
"""Start the private QEMU display bus in a VM-owner-only directory."""
import argparse
import os
from pathlib import Path
import socket
import stat
import subprocess
import tempfile
from xml.sax.saxutils import escape

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('directory', type=Path)
args = parser.parse_args()
directory = args.directory.absolute()
directory.mkdir(mode=0o700, parents=True, exist_ok=True)
info = directory.lstat()
if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
  raise SystemExit('Socket directory must belong to this user and have mode 0700')
path = directory / 'display-bus.sock'
if path.exists():
  with socket.socket(socket.AF_UNIX) as connection:
    try:
      connection.connect(str(path))
    except ConnectionRefusedError:
      path.unlink()
    else:
      print('unix:path=' + str(path))
      raise SystemExit(0)
source = Path(__file__).with_name('display-bus.conf').read_text()
with tempfile.NamedTemporaryFile(mode='w', suffix='.conf') as config:
  config.write(source.replace('@SOCKET@', escape(str(path))))
  config.flush()
  subprocess.run(['dbus-daemon', '--config-file=' + config.name, '--fork', '--print-address', '--print-pid'], check=True)
