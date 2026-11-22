// Runs edge/api/index.js's real fetch handler with the origin faked: what it forwards and the log line it writes.
// Run: node --test tests/infra/edge-worker.test.mjs   (Edge.Tests.ps1 runs it)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Copied to a .mjs file so any Node version loads it as a module (the Worker file has no package.json).
const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'edge', 'api', 'index.js'), 'utf8');
const copy = join(mkdtempSync(join(tmpdir(), 'edge-worker-')), 'index.mjs');
writeFileSync(copy, source);
const worker = (await import(pathToFileURL(copy).href)).default;

const env = { ORIGIN: 'https://origin.example', ORIGIN_AUTH: 'secret-value-for-the-test-only-0123456789' };

async function run(url, { headers = {}, status = 200, fail = false } = {}) {
    const sent = [];
    const lines = [];
    const realFetch = globalThis.fetch;
    const realLog = console.log;
    globalThis.fetch = async (target, init) => {
        sent.push({ target: String(target), headers: new Headers(init.headers) });
        if (fail) throw new TypeError('network');
        return new Response('ok', { status });
    };
    console.log = (text) => lines.push(text);
    try {
        const response = await worker.fetch(new Request(url, { headers }), env);
        return { response, sent, lines };
    } finally {
        globalThis.fetch = realFetch;
        console.log = realLog;
    }
}

test('forwards the visitor ray as X-CF-Ray, replacing any copy the visitor sent', async () => {
    const { sent } = await run('https://api.lifestream.himaiz.com/api/v1/donors/7', {
        headers: { 'cf-ray': '8a1b2c3d4e5f6a7b-LHR', 'x-cf-ray': 'forged-by-visitor' },
    });
    assert.equal(sent.length, 1);
    assert.equal(sent[0].target, 'https://origin.example/api/v1/donors/7');
    assert.equal(sent[0].headers.get('x-cf-ray'), '8a1b2c3d4e5f6a7b-LHR');
    assert.equal(sent[0].headers.get('x-origin-auth'), env.ORIGIN_AUTH);
});

test('sends no X-CF-Ray when the request has no ray, and drops a forged one', async () => {
    const { sent } = await run('https://api.lifestream.himaiz.com/healthz', { headers: { 'x-cf-ray': 'forged-by-visitor' } });
    assert.equal(sent[0].headers.get('x-cf-ray'), null);
});

test('logs one JSON line in the LOGGING.md shape: route pattern, never the raw path, query or secret', async () => {
    const { lines } = await run('https://api.lifestream.himaiz.com/api/v1/donors/7/eligibility?search=Zephyrina', {
        headers: { 'cf-ray': '8a1b2c3d4e5f6a7b-LHR' },
        status: 404,
    });
    assert.equal(lines.length, 1);
    const line = JSON.parse(lines[0]);
    assert.match(line.time, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/);
    assert.equal(line.level, 'info');
    assert.equal(line.app, 'lifestream');
    assert.equal(line.part, 'edge');
    assert.equal(line.event, 'request');
    assert.equal(line.ray, '8a1b2c3d4e5f6a7b-LHR');
    assert.equal(line.method, 'GET');
    assert.equal(line.route, '/api/v1/donors/:id/eligibility');
    assert.equal(line.status, 404);
    assert.equal(typeof line.ms, 'number');
    assert.equal(line.msg, 'GET /api/v1/donors/:id/eligibility 404');
    assert.ok(!lines[0].includes('Zephyrina'));
    assert.ok(!lines[0].includes(env.ORIGIN_AUTH));
});

test('replaces a path segment that is not a word or an id', async () => {
    const { lines } = await run('https://api.lifestream.himaiz.com/api/v1/Some%20Name/x');
    assert.equal(JSON.parse(lines[0]).route, '/api/v1/:param/x');
});

test('health checks are debug, a 5xx is an error, and a failed fetch is a logged 502', async () => {
    assert.equal(JSON.parse((await run('https://api.lifestream.himaiz.com/readyz', { status: 503 })).lines[0]).level, 'debug');
    assert.equal(JSON.parse((await run('https://api.lifestream.himaiz.com/api/v1/donors', { status: 500 })).lines[0]).level, 'error');
    const failed = await run('https://api.lifestream.himaiz.com/api/v1/donors', { fail: true });
    assert.equal(failed.response.status, 502);
    const line = JSON.parse(failed.lines[0]);
    assert.equal(line.status, 502);
    assert.equal(line.level, 'error');
});
