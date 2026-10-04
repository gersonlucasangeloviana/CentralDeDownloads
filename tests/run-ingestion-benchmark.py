"""Executa a matriz k6 e guarda resumos, logs e uma linha JSON por caso.

Exemplo: CENTRAL_DOWNLOADS_API_URL=... CENTRAL_DOWNLOADS_API_KEY=... python3 tests/run-ingestion-benchmark.py \
    --rows 100000 --sizes 100,1000 --concurrency 1,4,8,16 --output benchmarks/ingestao-local
"""

import argparse
import datetime as dt
import json
import os
import pathlib
import re
import subprocess
import sys
import time


def metric(summary, name, field="avg"):
    return summary.get("metrics", {}).get(name, {}).get(field)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rows", type=int, default=100_000)
    parser.add_argument("--sizes", default="100,1000")
    parser.add_argument("--concurrency", default="1,4,8,16")
    parser.add_argument("--format", choices=("xlsx", "csv", "json"), default="csv")
    parser.add_argument("--payload-chars", type=int, default=512)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if not os.getenv("CENTRAL_DOWNLOADS_API_URL") or not os.getenv("CENTRAL_DOWNLOADS_API_KEY"):
        parser.error("Defina CENTRAL_DOWNLOADS_API_URL e CENTRAL_DOWNLOADS_API_KEY no ambiente.")
    sizes = [int(value) for value in args.sizes.split(",")]
    concurrency = [int(value) for value in args.concurrency.split(",")]
    if args.rows < 1 or any(not 1 <= size <= 1000 for size in sizes) or any(not 1 <= count <= 32 for count in concurrency):
        parser.error("rows deve ser positivo, sizes entre 1 e 1000, concurrency entre 1 e 32.")
    args.output.mkdir(parents=True, exist_ok=True)
    index = args.output / "resultados.jsonl"
    script = pathlib.Path(__file__).with_name("worker-load.k6.js")
    for size in sizes:
        for count in concurrency:
            name = f"{args.format}-{args.rows}-lote{size}-paralelo{count}"
            summary_path = args.output / f"{name}.json"
            log_path = args.output / f"{name}.log"
            if summary_path.exists() or log_path.exists():
                parser.error(f"Arquivos de {name} já existem em {args.output}; escolha outro diretório.")
            environment = os.environ.copy()
            environment.update({
                "FILES": "1", "ROWS_PER_FILE": str(args.rows), "ROWS_PER_BATCH": str(size),
                "BATCH_CONCURRENCY": str(count), "PAYLOAD_CHARS": str(args.payload_chars),
                "POLL_SECONDS": "2", "FORMAT": args.format,
            })
            started_at = dt.datetime.now(dt.timezone.utc).isoformat()
            started = time.perf_counter()
            with log_path.open("w") as log:
                completed = subprocess.run(
                    ["k6", "run", "--summary-export", str(summary_path), str(script)],
                    env=environment, stdout=log, stderr=subprocess.STDOUT, check=False,
                )
            elapsed = round(time.perf_counter() - started, 3)
            summary = json.loads(summary_path.read_text()) if summary_path.exists() else {}
            log_text = log_path.read_text()
            ids = re.findall(r"\b([0-9a-f]{32}): pronto;", log_text)
            result = {
                "started_at_utc": started_at,
                "api_url": environment["CENTRAL_DOWNLOADS_API_URL"],
                "format": args.format,
                "rows": args.rows,
                "rows_per_batch": size,
                "batch_concurrency": count,
                "payload_chars": args.payload_chars,
                "exit_code": completed.returncode,
                "job_ids": ids,
                "wall_seconds": elapsed,
                "send_ms": metric(summary, "job_send_ms"),
                "ingest_ms": metric(summary, "job_ingest_ms"),
                "queue_ms": metric(summary, "job_queue_ms"),
                "generation_ms": metric(summary, "job_generation_ms"),
                "end_to_end_ms": metric(summary, "job_end_to_end_ms"),
                "output_bytes": metric(summary, "job_output_bytes"),
                "http_duration_avg_ms": metric(summary, "http_req_duration"),
                "http_duration_p95_ms": metric(summary, "http_req_duration", "p(95)"),
                "http_fail_rate": metric(summary, "http_req_failed", "value"),
                "http_requests": metric(summary, "http_reqs", "count"),
                "job_success_rate": metric(summary, "job_success", "value"),
                "summary": summary_path.name,
                "log": log_path.name,
            }
            with index.open("a") as file:
                file.write(json.dumps(result, ensure_ascii=False) + "\n")
            print(f"{name}: {completed.returncode=} envio={result['send_ms']}ms total={result['end_to_end_ms']}ms")
            if completed.returncode != 0:
                print(f"Falha: consulte {log_path}", file=sys.stderr)
                return completed.returncode
    return 0


if __name__ == "__main__":
    sys.exit(main())
