"""Confere o Swagger/OpenAPI de uma API em execução (CENTRAL_DOWNLOADS_API_URL obrigatório)."""

import json
import os
import urllib.error
import urllib.request

base = os.environ["CENTRAL_DOWNLOADS_API_URL"].rstrip("/")


def get(path):
    with urllib.request.urlopen(base + path, timeout=10) as response:
        return response.status, response.read()


status, html = get("/swagger/index.html")
assert status == 200 and b"swagger-ui" in html
status, raw = get("/openapi/v1.json")
assert status == 200
document = json.loads(raw)
assert document["components"]["securitySchemes"]["ApiKey"]["name"] == "X-Api-Key"

paths = document["paths"]
create = paths["/v1/arquivos"]["post"]
batch = paths["/v1/arquivos/{id}/lotes"]["post"]
finish = paths["/v1/arquivos/{id}/concluir"]["post"]

for operation in (create, batch, finish):
    assert operation["security"] == [{"ApiKey": []}]
    assert operation["summary"] and operation["description"]
    assert operation["requestBody"]["content"]["application/json"]["examples"]

create_schema = create["requestBody"]["content"]["application/json"]["schema"]
assert set(create_schema["required"]) == {"nome", "formato"}
assert create_schema["properties"]["formato"]["enum"] == ["xlsx", "csv", "json"]
batch_schema = batch["requestBody"]["content"]["application/json"]["schema"]
assert set(batch_schema["required"]) == {"id", "dados"}
assert batch_schema["properties"]["dados"]["maxItems"] == 100
assert batch_schema["properties"]["dados"]["items"]["additionalProperties"]["type"] == [
    "boolean", "integer", "number", "string"
]
assert "required" not in finish["requestBody"]
assert "202" in batch["responses"] and "413" in batch["responses"]
assert "422" in finish["responses"]
assert "security" not in paths["/v1/downloads/{token}"]["get"]

try:
    get("/v1/arquivos")
    raise AssertionError("Rota operacional aceitou chamada sem X-Api-Key")
except urllib.error.HTTPError as error:
    assert error.code == 401

print("Swagger UI, exemplos, limites e autenticação documentados corretamente")
