import http from 'k6/http';
import { sleep } from 'k6';
import exec from 'k6/execution';
import { Counter, Rate, Trend } from 'k6/metrics';

// Cada VU cria exatamente um arquivo. Os grupos de lotes são sequenciais;
// dentro de cada grupo, até BATCH_CONCURRENCY lotes são enviados em paralelo.
function integerEnv(name, fallback, min, max) {
  const raw = __ENV[name];
  const value = raw === undefined ? fallback : Number(raw);
  if (!Number.isSafeInteger(value) || value < min || value > max) {
    throw new Error(`${name} deve ser um inteiro entre ${min} e ${max}.`);
  }
  return value;
}

const baseUrl = (__ENV.CENTRAL_DOWNLOADS_API_URL || '').replace(/\/$/, '');
const apiKey = __ENV.CENTRAL_DOWNLOADS_API_KEY;
if (!/^https?:\/\/[^/]+/.test(baseUrl) || !apiKey) {
  throw new Error('Defina CENTRAL_DOWNLOADS_API_URL e CENTRAL_DOWNLOADS_API_KEY antes de executar.');
}

const files = integerEnv('FILES', 1, 1, 50);
const rowsPerFile = integerEnv('ROWS_PER_FILE', 1000, 1, 10000000);
const rowsPerBatch = integerEnv('ROWS_PER_BATCH', 100, 1, 1000);
const batchConcurrency = integerEnv('BATCH_CONCURRENCY', 1, 1, 32);
const payloadChars = integerEnv('PAYLOAD_CHARS', 128, 0, 8000);
const pollSeconds = integerEnv('POLL_SECONDS', 5, 1, 60);
const maxWaitSeconds = integerEnv('MAX_WAIT_SECONDS', 2100, 30, 7200);
const format = (__ENV.FORMAT || 'xlsx').toLowerCase();
if (!['xlsx', 'csv', 'json'].includes(format)) {
  throw new Error('FORMAT deve ser xlsx, csv ou json.');
}

export const options = {
  batch: batchConcurrency,
  batchPerHost: batchConcurrency,
  scenarios: {
    files: {
      executor: 'per-vu-iterations',
      vus: files,
      iterations: 1,
      maxDuration: __ENV.MAX_DURATION || '90m',
    },
  },
  thresholds: {
    job_success: ['rate==1'],
    test_errors: ['count==0'],
  },
};

const errors = new Counter('test_errors');
const success = new Rate('job_success');
const createMs = new Trend('job_create_ms', true);
const sendMs = new Trend('job_send_ms', true);
const closeMs = new Trend('job_close_ms', true);
const ingestMs = new Trend('job_ingest_ms', true);
const queueMs = new Trend('job_queue_ms', true);
const generationMs = new Trend('job_generation_ms', true);
const totalMs = new Trend('job_total_ms', true);
const pollingMs = new Trend('job_polling_ms', true);
const endToEndMs = new Trend('job_end_to_end_ms', true);
const pollsPerJob = new Trend('job_polls');
const outputBytes = new Trend('job_output_bytes');

const headers = { 'X-Api-Key': apiKey, 'Content-Type': 'application/json' };

function requestParams(name) {
  return { headers, timeout: '60s', tags: { name } };
}

function parseResponse(response, expectedStatus, step) {
  if (response.status !== expectedStatus) {
    throw new Error(`${step}: HTTP ${response.status}, corpo=${String(response.body).slice(0, 250)}`);
  }
  try {
    return response.json();
  } catch (_) {
    throw new Error(`${step}: resposta JSON inválida.`);
  }
}

function failJob(message) {
  errors.add(1);
  success.add(false);
  console.error(message);
}

// Hexadecimal pseudoaleatório por linha: evita que dados repetidos comprimam
// artificialmente o XLSX. Só um lote é mantido em memória por VU.
function payload(seed, size) {
  let state = (seed ^ 0x9e3779b9) >>> 0;
  let result = '';
  while (result.length < size) {
    state ^= state << 13;
    state ^= state >>> 17;
    state ^= state << 5;
    result += (state >>> 0).toString(16).padStart(8, '0');
  }
  return result.slice(0, size);
}

export default function () {
  const vu = exec.vu.idInTest;
  const startedAt = Date.now();
  let id;
  let polls = 0;
  try {
    const created = parseResponse(
      http.post(baseUrl + '/v1/arquivos', JSON.stringify({ nome: `Carga k6 ${vu}-${startedAt}`, formato: format }),
        requestParams('POST /v1/arquivos')),
      201, 'criar arquivo',
    );
    id = created.id;
    if (!id) throw new Error('criar arquivo: resposta sem id.');
    const createdAt = Date.now();
    createMs.add(createdAt - startedAt);

    let batches = 0;
    const sendingAt = Date.now();
    for (let offset = 0; offset < rowsPerFile;) {
      const requests = [];
      while (requests.length < batchConcurrency && offset < rowsPerFile) {
        const rows = [];
        const end = Math.min(offset + rowsPerBatch, rowsPerFile);
        for (let row = offset; row < end; row++) {
          rows.push({ Sequencia: row + 1, Carga: payload((vu * 1000003 + row) >>> 0, payloadChars) });
        }
        const body = JSON.stringify({ id, idLote: `k6-${vu}-${batches + requests.length + 1}`, dados: rows });
        if (body.length > 1024 * 1024) {
          throw new Error('lote excedeu 1 MiB; reduza PAYLOAD_CHARS ou ROWS_PER_BATCH.');
        }
        requests.push({ method: 'POST', url: `${baseUrl}/v1/arquivos/${id}/lotes`, body,
          params: requestParams('POST /v1/arquivos/{id}/lotes') });
        offset = end;
      }
      const responses = requests.length === 1
        ? [http.post(requests[0].url, requests[0].body, requests[0].params)]
        : http.batch(requests);
      for (let index = 0; index < responses.length; index++) {
        parseResponse(responses[index], 202, `lote ${batches + index + 1} de ${id}`);
      }
      batches += requests.length;
      if (batches % 500 === 0) {
        console.log(`${id}: envio=${Math.min(offset, rowsPerFile)}/${rowsPerFile} linhas; lotes=${batches}; tempo_ms=${Date.now() - sendingAt}`);
      }
    }
    const sentAt = Date.now();
    sendMs.add(sentAt - sendingAt);

    const closed = parseResponse(
      http.post(`${baseUrl}/v1/arquivos/${id}/concluir`,
        JSON.stringify({ totalLotes: batches, totalItens: rowsPerFile }),
        requestParams('POST /v1/arquivos/{id}/concluir')),
      202, `concluir ${id}`,
    );
    if (closed.totalLotes !== batches || closed.totalItens !== rowsPerFile) {
      throw new Error(`${id}: contagens divergentes na conclusão.`);
    }
    const closedAtClient = Date.now();
    closeMs.add(closedAtClient - sentAt);
    ingestMs.add(closedAtClient - startedAt);

    const pollDeadline = closedAtClient + maxWaitSeconds * 1000;
    let lastStatus;
    while (Date.now() < pollDeadline) {
      const state = parseResponse(
        http.get(`${baseUrl}/v1/arquivos/${id}`, requestParams('GET /v1/arquivos/{id}')),
        200, `consultar ${id}`,
      );
      polls++;
      if (state.status !== lastStatus) {
        console.log(`${id}: status=${state.status}; desde_conclusao_ms=${Date.now() - closedAtClient}; consultas=${polls}`);
        lastStatus = state.status;
      }
      if (state.status === 'falhou' || state.status === 'expirou') {
        throw new Error(`${id}: ${state.status}: ${state.erro || 'sem detalhe'}`);
      }
      if (state.status === 'pronto') {
        const expectedFormat = format === 'xlsx' && rowsPerFile > 1000000 ? 'csv' : format;
        if (state.formato !== expectedFormat || state.formatoSolicitado !== format) {
          throw new Error(`${id}: formato inesperado ${state.formato} (solicitado ${state.formatoSolicitado}).`);
        }
        if (state.totalItens !== rowsPerFile || state.totalLotes !== batches ||
            !Number.isFinite(state.tamanhoBytes) || state.tamanhoBytes <= 0) {
          throw new Error(`${id}: contagens ou tamanho final inválidos.`);
        }
        const closedAt = Date.parse(state.fechadoEm);
        const begunAt = Date.parse(state.iniciadoEm);
        const readyAt = Date.parse(state.prontoEm);
        if (![closedAt, begunAt, readyAt].every(Number.isFinite) ||
            begunAt < closedAt || readyAt < begunAt) {
          throw new Error(`${id}: datas de processamento inválidas.`);
        }
        queueMs.add(begunAt - closedAt);
        generationMs.add(readyAt - begunAt);
        totalMs.add(readyAt - closedAt);
        pollingMs.add(Date.now() - closedAtClient);
        endToEndMs.add(Date.now() - startedAt);
        pollsPerJob.add(polls);
        outputBytes.add(state.tamanhoBytes);
        success.add(true);
        console.log(`${id}: pronto; linhas=${rowsPerFile}; lotes=${batches}; bytes=${state.tamanhoBytes}; envio_ms=${sentAt - sendingAt}; ingestao_ms=${closedAtClient - startedAt}; fila_ms=${begunAt - closedAt}; geracao_ms=${readyAt - begunAt}; ate_get_pronto_ms=${Date.now() - closedAtClient}; ponta_a_ponta_ms=${Date.now() - startedAt}; consultas=${polls}`);
        return;
      }
      sleep(pollSeconds);
    }
    throw new Error(`${id}: sem estado final após ${maxWaitSeconds}s de consulta.`);
  } catch (error) {
    // A V1 não deduplica lotes: uma resposta incerta nunca é reenviada.
    failJob(`${id || 'sem id'}: ${error.message}; consultas=${polls}`);
  }
}
