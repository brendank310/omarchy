#!/usr/bin/python3
"""Supervise the container-local listener relay alongside dockurr's entrypoint."""
import os
from pathlib import Path
import signal
import stat
import subprocess
import sys
import time


def main():
  uid = int(os.environ['OMARCHY_UID'])
  gid = int(os.environ['OMARCHY_GID'])
  if uid <= 0 or gid <= 0:
    raise SystemExit('The renderer requires an unprivileged VM owner')
  directory = Path('/storage/omarchy-enhanced')
  created = False
  try:
    directory.mkdir(mode=0o700)
    created = True
  except FileExistsError:
    pass
  directory_fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
  info = os.fstat(directory_fd)
  if created and info.st_uid == 0:
    os.fchown(directory_fd, uid, gid)
    info = os.fstat(directory_fd)
  if not stat.S_ISDIR(info.st_mode) or info.st_uid != uid or info.st_mode & 0o077:
    raise SystemExit('The display socket directory must belong to the VM owner with mode 0700')
  # The bus runs on the host: EXTERNAL authentication needs host-visible peer
  # credentials for the unprivileged viewer connecting to it.
  if not stat.S_ISSOCK(os.stat('display-bus.sock', dir_fd=directory_fd, follow_symlinks=False).st_mode):
    raise SystemExit('Start the private host display bus before starting the VM')
  relay = subprocess.Popen([sys.executable, '/opt/omarchy/listener-relay.py', str(directory / 'listener-relay.sock')],
    user=uid, group=gid, extra_groups=[])
  vm = subprocess.Popen(['/run/entry.sh', *sys.argv[1:]])
  stopping = False

  def stop(signum, frame):
    nonlocal stopping
    stopping = True
    if vm.poll() is None:
      vm.send_signal(signum)

  signal.signal(signal.SIGTERM, stop)
  signal.signal(signal.SIGINT, stop)
  try:
    while vm.poll() is None:
      if relay.poll() is not None and not stopping:
        print('Listener relay exited; stopping VM', file=sys.stderr, flush=True)
        vm.terminate()
        stopping = True
      # QEMU creates its chardev before dropping privileges. Open with O_PATH
      # and O_NOFOLLOW, then operate on that pinned inode so path replacement
      # cannot redirect a privileged ownership change.
      try:
        fd = os.open('enhanced.sock', os.O_PATH | os.O_NOFOLLOW, dir_fd=directory_fd)
      except FileNotFoundError:
        pass
      else:
        try:
          info = os.fstat(fd)
          if stat.S_ISSOCK(info.st_mode) and info.st_uid in (0, uid):
            pinned = f'/proc/self/fd/{fd}'
            if info.st_uid != uid or info.st_gid != gid:
              os.chown(pinned, uid, gid)
            if stat.S_IMODE(info.st_mode) != 0o600:
              os.chmod(pinned, 0o600)
        finally:
          os.close(fd)
      time.sleep(.2)
    return vm.returncode
  finally:
    if vm.poll() is None:
      vm.terminate()
      vm.wait(timeout=120)
    if relay.poll() is None:
      relay.terminate()
    relay.wait(timeout=5)
    os.close(directory_fd)


if __name__ == '__main__':
  raise SystemExit(main())
