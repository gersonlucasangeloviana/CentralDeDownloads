import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const html = readFileSync(new URL('../src/CentralDeDownloads.Portal/wwwroot/index.html', import.meta.url), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
assert.ok(script, 'Script do portal não encontrado');

async function run(response) {
  const elements = new Map();
  const element = selector => {
    if (!elements.has(selector)) {
      elements.set(selector, { textContent: '', innerHTML: '', value: '', addEventListener() {}, close() {}, showModal() {} });
    }
    return elements.get(selector);
  };
  let destination;
  vm.runInNewContext(script, {
    document: { querySelector: element },
    window: { location: { replace: path => { destination = path; } } },
    fetch: async () => response,
    URL,
  });
  await new Promise(resolve => setTimeout(resolve, 0));
  return { destination, message: element('#message').textContent };
}

const expired = await run(new Response('', {
  status: 401,
  headers: { 'X-Portal-Session-Expired': '1' },
}));
assert.equal(expired.destination, '/login?expirada=1');

const upstreamUnauthorized = await run(new Response('', { status: 401 }));
assert.equal(upstreamUnauthorized.destination, undefined);
assert.equal(upstreamUnauthorized.message, 'Falha HTTP 401');

const htmlError = await run(new Response('<!doctype html>', {
  status: 200,
  headers: { 'Content-Type': 'text/html' },
}));
assert.equal(htmlError.destination, undefined);
assert.equal(htmlError.message, 'Resposta inesperada do servidor. Tente novamente.');

console.log('portal UI: sessão vencida e respostas inesperadas: ok');
