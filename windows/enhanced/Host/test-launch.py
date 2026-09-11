#!/usr/bin/python3
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('launch', Path(__file__).with_name('launch.py'))
launch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launch)


class SelectionTests(unittest.TestCase):
  def test_office_launch_waits_for_main_window_after_splash(self):
    splash = {'id': 'splash', 'processId': 12, 'className': 'MsoSplash',
      'minimized': False, 'foreground': True, 'zOrder': 0}
    main = {**splash, 'id': 'main', 'className': 'OpusApp'}
    events = [{'type': 'session.ready', 'session': 'epoch'},
      {'type': 'windows.snapshot', 'windows': []},
      {'id': 'launch', 'result': {'processId': 12}},
      {'type': 'windows.snapshot', 'windows': [splash]},
      {'type': 'windows.snapshot', 'windows': [main]}]
    with patch.object(launch.socket, 'socket'), patch.object(launch, 'read_frame', side_effect=events), \
        patch.object(launch, 'send_frame') as send:
      self.assertEqual(launch.select('/unused', 'word', None, False), 'main')
      send.assert_called_once()

  def test_explicit_launch_selects_existing_minimized_office_window(self):
    events = [{'type': 'session.ready', 'session': 'epoch'},
      {'type': 'windows.snapshot', 'windows': [{'id': 'aa', 'processId': 12,
        'className': 'XLMAIN', 'minimized': True, 'foreground': False, 'zOrder': 1}]}]
    with patch.object(launch.socket, 'socket'), patch.object(launch, 'read_frame', side_effect=events), \
        patch.object(launch, 'send_frame') as send:
      self.assertEqual(launch.select('/unused', 'excel', None, False), 'aa')
      send.assert_not_called()


if __name__ == '__main__':
  unittest.main()
