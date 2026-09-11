#!/usr/bin/python3
"""Merge packaged Office menu entries into the current user's extension."""
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import tempfile


def parse(text):
  text = re.sub(r'^\s*//.*$', '', text, flags=re.M)
  return json.loads(re.sub(r',\s*([}\]])', r'\1', text))


def enable(source, target):
  additions = parse(source.read_text())
  existing = parse(target.read_text()) if target.exists() else {}
  if not isinstance(existing, dict) or not isinstance(additions, dict):
    raise ValueError('Menu extension must be a JSON object')
  merged = dict(existing)
  for key, value in additions.items():
    merged[key] = {**value, **existing.get(key, {})}
  if merged == existing:
    return
  target.parent.mkdir(parents=True, exist_ok=True)
  if target.exists():
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
    shutil.copy2(target, target.with_name(target.name + '.before-office-' + stamp))
  with tempfile.NamedTemporaryFile(mode='w', dir=target.parent, delete=False) as output:
    output.write(json.dumps(merged, ensure_ascii=False, indent=2) + '\n')
    temporary = Path(output.name)
  try:
    os.replace(temporary, target)
  finally:
    temporary.unlink(missing_ok=True)


if __name__ == '__main__':
  root = Path(os.environ.get('XDG_CONFIG_HOME', str(Path.home() / '.config')))
  enable(Path('/usr/share/omarchy-windows/menu.jsonc'), root / 'omarchy/extensions/omarchy-menu.jsonc')
