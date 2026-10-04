"""Teste de ponta a ponta contra uma API, banco, fila e S3 configurados.

GERADOR_API_URL e GERADOR_API_KEY são obrigatórios. Cria arquivos de teste.
"""

import io
import json
import os
import time
import urllib.error
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor

BASE = os.environ["GERADOR_API_URL"].rstrip("/")
KEY = os.environ["GERADOR_API_KEY"]


def call(method, path, data=None):
    body = None if data is None else json.dumps(data).encode()
    request = urllib.request.Request(
        BASE + path,
        data=body,
        method=method,
        headers={"X-Api-Key": KEY, "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


for format_name in ("json", "csv", "xlsx"):
    _, job = call("POST", "/v1/arquivos", {"nome": f"Smoke {format_name}", "formato": format_name})
    job_id = job["id"]
    _, _ = call("POST", f"/v1/arquivos/{job_id}/lotes", {
        "id": job_id, "dados": [{"Pedido": "1", "Cliente": "Ana"}, {"Pedido": "2", "Cliente": "Bia"}]
    })
    _, _ = call("POST", f"/v1/arquivos/{job_id}/lotes", {
        "id": job_id, "idLote": "segundo", "dados": [{"Pedido": "3", "Cliente": "Caio"}]
    })
    _, _ = call("POST", f"/v1/arquivos/{job_id}/concluir", {"totalLotes": 2, "totalItens": 3})
    for _ in range(30):
        _, state = call("GET", f"/v1/arquivos/{job_id}")
        if state["status"] in ("pronto", "falhou"):
            break
        time.sleep(1)
    assert state["status"] == "pronto", state
    with urllib.request.urlopen(state["linkDownload"], timeout=30) as response:
        content = response.read()
    if format_name == "json":
        assert len(json.loads(content)) == 3
    elif format_name == "csv":
        assert content.decode("utf-8-sig").count("\n") == 4
    else:
        with zipfile.ZipFile(io.BytesIO(content)) as archive:
            assert "xl/worksheets/sheet1.xml" in archive.namelist()
    print(format_name, state["status"], len(content), "bytes")

_, job = call("POST", "/v1/arquivos", {"nome": "Smoke paralelo", "formato": "csv"})
job_id = job["id"]


def send_parallel_batch(number):
    status, result = call("POST", f"/v1/arquivos/{job_id}/lotes", {
        "id": job_id, "idLote": str(number),
        "dados": [{"Pedido": str(number * 100 + item)} for item in range(100)],
    })
    assert status == 202, (status, result)


with ThreadPoolExecutor(max_workers=8) as executor:
    list(executor.map(send_parallel_batch, range(8)))
status, state = call("POST", f"/v1/arquivos/{job_id}/concluir", {"totalLotes": 8, "totalItens": 800})
assert status == 202, (status, state)
for _ in range(30):
    _, state = call("GET", f"/v1/arquivos/{job_id}")
    if state["status"] in ("pronto", "falhou"):
        break
    time.sleep(1)
assert state["status"] == "pronto", state
with urllib.request.urlopen(state["linkDownload"], timeout=30) as response:
    rows = response.read().decode("utf-8-sig").splitlines()
assert len(rows) == 801 and {str(number) for number in range(800)} == set(rows[1:])
print("parallel batches", state["status"], len(rows) - 1, "items")

_, job = call("POST", "/v1/arquivos", {"nome": "Smoke erro", "formato": "csv"})
job_id = job["id"]
status, _ = call("POST", f"/v1/arquivos/{job_id}/lotes", {"id": job_id, "dados": [{"a": [1]}]})
assert status == 400
status, _ = call("POST", f"/v1/arquivos/{job_id}/lotes", {"id": job_id, "dados": [{"a": n} for n in range(101)]})
assert status == 400
call("POST", f"/v1/arquivos/{job_id}/lotes", {"id": job_id, "dados": [{"a": "ok"}]})
status, state = call("POST", f"/v1/arquivos/{job_id}/concluir", {"totalLotes": 2})
assert status == 422 and state["status"] == "falhou", (status, state)
print("invalid batch and count mismatch", state["status"])
