#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

python3 - <<'PY'
import json
import os
from pathlib import Path
import re
import shlex
import subprocess

outputs = json.loads(subprocess.check_output(["terraform", "output", "-json"], text=True))
values = {
    "Aws__Region": outputs["aws_region"]["value"],
    "Aws__Bucket": outputs["export_bucket"]["value"],
    "Queue__Provider": "SQS",
    "Aws__QueueUrl": outputs["queue_url"]["value"],
}
path = Path("../../.env.local")
if not path.exists():
    raise SystemExit("Crie .env.local com a conexão do Atlas antes de sincronizar.")

lines = path.read_text().splitlines()
found = set()
updated = []
for line in lines:
    match = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)=", line)
    if match and match.group(1) in values:
        key = match.group(1)
        updated.append(f"{key}={shlex.quote(values[key])}")
        found.add(key)
    else:
        updated.append(line)
for key, value in values.items():
    if key not in found:
        updated.append(f"{key}={shlex.quote(value)}")

path.write_text("\n".join(updated).rstrip() + "\n")
os.chmod(path, 0o600)
print(".env.local atualizado com região, bucket e fila SQS (sem alterar a conexão MongoDB).")
PY
