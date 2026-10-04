"""Gera a coleção Postman 2.1 a partir dos exemplos versionados neste arquivo."""

import json
from pathlib import Path

ROOT = Path(__file__).parent
OUT = ROOT / "CentralDeDownloads.postman_collection.json"


def event(source):
    return [{"listen": "test", "script": {"type": "text/javascript", "exec": source.strip().splitlines()}}]


def request(name, method, url, body=None, tests="", description="", api_key=True):
    headers = [{"key": "X-Api-Key", "value": "{{apiKey}}"}] if api_key else []
    if body is not None:
        headers.append({"key": "Content-Type", "value": "application/json"})
    definition = {"method": method, "header": headers, "url": url, "description": description}
    if body is not None:
        definition["body"] = {
            "mode": "raw",
            "raw": json.dumps(body, ensure_ascii=False, indent=2),
            "options": {"raw": {"language": "json"}},
        }
    return {"name": name, "request": definition, "event": event(tests)}


def create_tests(prefix):
    return f"""
pm.test("Criação retorna 201", () => pm.response.to.have.status(201));
let job = {{}};
try {{ job = pm.response.json(); }} catch (error) {{}}
pm.test("Resposta contém id e status recebendo", () => {{
    pm.expect(job.id).to.be.a("string").and.not.empty;
    pm.expect(job.status).to.eql("recebendo");
}});
if (pm.response.code === 201 && job.id) {{
    pm.collectionVariables.set("{prefix}JobId", job.id);
    pm.collectionVariables.unset("{prefix}DownloadUrl");
    pm.collectionVariables.set("{prefix}PollCount", "0");
}} else {{
    pm.execution.setNextRequest(null);
}}
"""


def batch_tests(prefix, sequence, count):
    return f"""
pm.test("Lote retorna 202", () => pm.response.to.have.status(202));
let batch = {{}};
try {{ batch = pm.response.json(); }} catch (error) {{}}
pm.test("Lote e sequência confirmados", () => {{
    pm.expect(batch.id).to.eql(pm.collectionVariables.get("{prefix}JobId"));
    pm.expect(batch.sequencia).to.eql({sequence});
    pm.expect(batch.totalItens).to.eql({count});
}});
if (pm.response.code !== 202) pm.execution.setNextRequest(null);
"""


def finish_tests(total_batches, total_items):
    return f"""
pm.test("Conclusão retorna 202", () => pm.response.to.have.status(202));
let job = {{}};
try {{ job = pm.response.json(); }} catch (error) {{}}
pm.test("Totais recebidos sem divergência", () => {{
    pm.expect(job.totalLotes).to.eql({total_batches});
    pm.expect(job.totalItens).to.eql({total_items});
    pm.expect(job.status).not.to.eql("falhou");
}});
if (pm.response.code !== 202) pm.execution.setNextRequest(null);
"""


def status_tests(prefix, total_batches, total_items):
    return f"""
pm.test("Consulta retorna 200", () => pm.response.to.have.status(200));
let job = {{}};
try {{ job = pm.response.json(); }} catch (error) {{}}
if (pm.response.code !== 200) {{
    pm.execution.setNextRequest(null);
}} else if (job.status === "pronto") {{
    pm.test("Arquivo pronto com totais corretos e link", () => {{
        pm.expect(job.totalLotes).to.eql({total_batches});
        pm.expect(job.totalItens).to.eql({total_items});
        pm.expect(job.linkDownload).to.match(/^https?:\\/\\//);
    }});
    if (job.linkDownload) pm.collectionVariables.set("{prefix}DownloadUrl", job.linkDownload);
    else pm.execution.setNextRequest(null);
}} else if (job.status === "falhou" || job.status === "expirou") {{
    pm.test("Geração sem erro", () => {{ throw new Error(job.erro || job.status); }});
    pm.execution.setNextRequest(null);
}} else {{
    const count = Number(pm.collectionVariables.get("{prefix}PollCount") || 0) + 1;
    pm.collectionVariables.set("{prefix}PollCount", String(count));
    if (count >= 180) {{
        pm.test("Arquivo pronto em até 180 consultas", () => {{ throw new Error("Último status: " + job.status); }});
        pm.execution.setNextRequest(null);
    }} else {{
        pm.test("Status de processamento válido", () =>
            pm.expect(["recebendo", "fechando", "na_fila", "processando"]).to.include(job.status));
        pm.execution.setNextRequest(pm.info.requestId);
    }}
}}
"""


def batch_body(prefix, batch_id, rows):
    body = {"id": "{{" + prefix + "JobId}}"}
    if batch_id:
        body["idLote"] = batch_id
    body["dados"] = rows
    return body


simple_rows = [
    {"Pedido": "PED-1001", "Cliente": "Ana", "Valor": 149.90},
    {"Pedido": "PED-1002", "Cliente": "Bia", "Valor": 250.00},
]

complete_rows = [
    [
        {"Pedido": "PED-2001", "Data": "2026-09-01", "Cliente": "Ana", "Valor": 149.90, "Pago": True},
        {"Pedido": "PED-2002", "Data": "2026-09-01", "Cliente": "Bia", "Valor": 250.00, "Pago": False},
        {"Pedido": "PED-2003", "Data": "2026-09-02", "Cliente": "Caio", "Valor": 85.50, "Pago": True},
    ],
    [
        {"Pedido": "PED-2004", "Data": "2026-09-03", "Cliente": "Duda", "Valor": 99.00, "Pago": True},
        {"Pedido": "PED-2005", "Data": "2026-09-03", "Cliente": "Eva", "Valor": 310.75, "Pago": False},
        {"Pedido": "PED-2006", "Data": "2026-09-04", "Cliente": "Fábio", "Valor": 120.00, "Pago": True},
    ],
    [
        {"Pedido": "PED-2007", "Data": "2026-09-05", "Cliente": "Gabi", "Valor": 45.25, "Pago": True},
        {"Pedido": "PED-2008", "Data": "2026-09-05", "Cliente": "Hugo", "Valor": 170.00, "Pago": False},
        {"Pedido": "PED-2009", "Data": "2026-09-06", "Cliente": "Iara", "Valor": 199.99, "Pago": True},
    ],
]

simple = [
    request("01 Criar CSV", "POST", "{{baseUrl}}/v1/arquivos",
            {"nome": "Exemplo Postman - 1 lote", "formato": "csv"}, create_tests("simple"),
            "Cria um CSV sem período nem notificações. Guarde o id capturado automaticamente."),
    request("02 Enviar lote único", "POST", "{{baseUrl}}/v1/arquivos/{{simpleJobId}}/lotes",
            batch_body("simple", None, simple_rows), batch_tests("simple", 1, 2),
            "Dois pedidos planos. Não envie mais de 1000 registros em um lote ou 1 MiB no corpo."),
    request("03 Concluir com totais", "POST", "{{baseUrl}}/v1/arquivos/{{simpleJobId}}/concluir",
            {"totalLotes": 1, "totalItens": 2}, finish_tests(1, 2),
            "Conclui o envio. Totais divergentes causam falha permanente do trabalho."),
    request("04 Consultar status até pronto", "GET", "{{baseUrl}}/v1/arquivos/{{simpleJobId}}",
            tests=status_tests("simple", 1, 2),
            description="No Collection Runner, repete esta consulta enquanto o worker processa. Não reenvia lotes."),
    request("05 Baixar CSV pelo link", "GET", "{{simpleDownloadUrl}}",
            tests=r"""
pm.test("Download retorna 200", () => pm.response.to.have.status(200));
pm.test("CSV contém cabeçalho e duas linhas", () => {
    const rows = pm.response.text().replace(/^\uFEFF/, "").trimEnd().split(/\r?\n/);
    pm.expect(rows).to.have.lengthOf(3);
    pm.expect(rows[0]).to.eql("Pedido;Cliente;Valor");
});
""",
            description="Abre linkDownload, sem X-Api-Key. Mantenha Follow redirects ativado.", api_key=False),
]

complete = [
    request("01 Criar XLSX com período", "POST", "{{baseUrl}}/v1/arquivos",
            {"nome": "Exemplo Postman - 3 lotes", "formato": "xlsx",
             "periodo": {"inicio": "2026-09-01", "fim": "2026-09-30"}}, create_tests("complete"),
            "Período é metadado opcional. Para testar notificações, adicione webhook e/ou email válidos."),
]
for number, rows in enumerate(complete_rows, 1):
    complete.append(request(
        f"0{number + 1} Enviar lote {number}", "POST",
        "{{baseUrl}}/v1/arquivos/{{completeJobId}}/lotes",
        batch_body("complete", f"lote-{number:03}", rows), batch_tests("complete", number, 3),
        "idLote é apenas um identificador de rastreamento; não deduplica reenvios."
    ))
complete += [
    request("05 Concluir com totais", "POST", "{{baseUrl}}/v1/arquivos/{{completeJobId}}/concluir",
            {"totalLotes": 3, "totalItens": 9}, finish_tests(3, 9),
            "Aguarde os três lotes antes desta chamada. Confere 3 lotes e 9 itens."),
    request("06 Consultar status até pronto", "GET", "{{baseUrl}}/v1/arquivos/{{completeJobId}}",
            tests=status_tests("complete", 3, 9),
            description="No Collection Runner, repete a consulta até pronto ou falhou."),
    request("07 Baixar XLSX pelo link", "GET", "{{completeDownloadUrl}}",
            tests=r"""
pm.test("Download retorna 200", () => pm.response.to.have.status(200));
pm.test("Conteúdo é XLSX", () => {
    pm.expect(pm.response.headers.get("Content-Type") || "").to.include("spreadsheetml.sheet");
    pm.expect(Number(pm.response.headers.get("Content-Length") || 1)).to.be.above(0);
});
""",
            description="Abre linkDownload, sem X-Api-Key. Mantenha Follow redirects ativado.", api_key=False),
]

collection = {
    "info": {
        "name": "CentralDeDownloads - fluxos de geração",
        "description": "Dois roteiros executáveis: CSV com um lote e XLSX com três lotes. Configure baseUrl e apiKey nas variáveis da coleção e execute uma pasta no Collection Runner com Delay de 2000 ms.",
        "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
    },
    "item": [
        {"name": "01 - Simples: CSV com 1 lote", "description": "5 chamadas: criar, enviar, concluir, consultar e baixar.", "item": simple},
        {"name": "02 - Completo: XLSX com 3 lotes", "description": "7 chamadas: criar, enviar 3 lotes, concluir, consultar e baixar.", "item": complete},
    ],
    "variable": [
        {"key": "baseUrl", "value": "http://localhost:5151", "type": "string"},
        {"key": "apiKey", "value": "", "type": "string"},
        {"key": "simpleJobId", "value": "", "type": "string"},
        {"key": "simpleDownloadUrl", "value": "", "type": "string"},
        {"key": "simplePollCount", "value": "0", "type": "string"},
        {"key": "completeJobId", "value": "", "type": "string"},
        {"key": "completeDownloadUrl", "value": "", "type": "string"},
        {"key": "completePollCount", "value": "0", "type": "string"},
    ],
}

OUT.write_text(json.dumps(collection, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(OUT)
