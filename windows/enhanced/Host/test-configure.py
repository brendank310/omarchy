#!/usr/bin/python3
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('configure', Path(__file__).with_name('configure.py'))
configure = importlib.util.module_from_spec(spec)
spec.loader.exec_module(configure)


class ConfigurationTests(unittest.TestCase):
  def test_repeated_migration_preserves_firmware_and_one_channel(self):
    original = ('-rtc base=localtime,clock=host,driftfix=slew -acpitable file=/oem-firmware/msdm.bin'
      ' -vga qxl -spice unix=on,addr=/storage/omarchy-enhanced/spice.sock,disable-ticketing=on')
    migrated = configure.arguments(original, 1000, 1000)
    self.assertEqual(configure.arguments(migrated, 1000, 1000), migrated)
    self.assertIn('-acpitable file=/oem-firmware/msdm.bin', migrated)
    self.assertNotIn('-spice', migrated)
    self.assertNotIn('qxl', migrated)
    self.assertEqual(migrated.count('-chardev'), 1)
    self.assertEqual(migrated.count('-run-with'), 1)
    self.assertIn('-run-with user=1000:1000', migrated)

  def test_missing_or_duplicate_arguments_fail_before_mutation(self):
    for text in ('services:\n', '      ARGUMENTS: "a"\n      ARGUMENTS: "b"\n'):
      with self.assertRaises(SystemExit):
        configure.replace_arguments(text, 1000, 1000)


if __name__ == '__main__':
  unittest.main()
