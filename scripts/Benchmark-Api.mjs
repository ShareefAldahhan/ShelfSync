// Local, disposable API benchmark. No application secrets or external payments.
import { spawn, spawnSync } from 'node:child_process'
import { randomBytes, randomUUID, pbkdf2Sync } from 'node:crypto'
import { mkdirSync, writeFileSync, unlinkSync, readdirSync, openSync, closeSync } from 'node:fs'
import { resolve, join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import os from 'node:os'
import net from 'node:net'
import { setTimeout as delay } from 'node:timers/promises'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const seconds = Number(process.env.BENCH_SECONDS ?? 10)
const warmupSeconds = Number(process.env.BENCH_WARMUP_SECONDS ?? 10)
const levels = (process.env.BENCH_CONCURRENCY ?? '1,10,25,50,100').split(',').map(Number)
if (!Number.isInteger(seconds) || seconds < 2 || seconds > 60 ||
    !Number.isInteger(warmupSeconds) || warmupSeconds < 2 || warmupSeconds > 30 ||
    levels.length > 10 || levels.some(n => !Number.isInteger(n) || n < 1 || n > 200)) {
  throw new Error('Use 2-60 seconds and up to ten concurrency levels between 1 and 200.')
}
const pg = process.env.BENCH_POSTGRES_BIN ?? 'C:/Program Files/PostgreSQL/18/bin'
const run = join(root, 'artifacts', `benchmark-${new Date().toISOString().replaceAll(/[:.]/g, '-')}`)
mkdirSync(run, { recursive: true })
const pgPort = 55440, apiPort = 5091, base = `http://127.0.0.1:${apiPort}`
const password = randomBytes(32).toString('hex')
const env = { ...process.env, PGPASSWORD: password }
const data = join(run, 'data')
let server, databaseStarted = false
let interrupted = false
process.on('SIGINT', () => {
  interrupted = true
  console.log('Stopping after outstanding requests finish; isolated processes will be shut down.')
})
const results = []

function command(exe, args, options = {}) {
  // PostgreSQL's Windows child can inherit pipe handles; use files/ignore for
  // pg_ctl so spawnSync does not wait for the database lifetime to reach EOF.
  const result = spawnSync(exe, args, { cwd: root, env, windowsHide: true, encoding: 'utf8',
    ...(exe.endsWith('pg_ctl.exe') ? { stdio: 'ignore' } : {}), ...options })
  if (result.error || result.status !== 0) {
    // Never include command environment or credentials in output.
    throw new Error(`${exe} failed: ${result.error?.message ?? result.stderr?.slice(-2500)}`)
  }
  return result.stdout
}
function sql(text, database = 'shelfsync_bench') {
  return command(join(pg, 'psql.exe'), ['-X', '-w', '-h', '127.0.0.1', '-p', String(pgPort),
    '-U', 'shelfsync_bench', '-d', database, '-v', 'ON_ERROR_STOP=1', '-At'], { input: text })
}
async function requireFreePort(port) {
  await new Promise((resolvePromise, reject) => {
    const probe = net.createServer()
    probe.once('error', reject)
    probe.listen(port, '127.0.0.1', () => probe.close(resolvePromise))
  })
}
const cookies = new Map()
let token
async function request(path, body, headers = {}) {
  const response = await fetch(base + path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { Cookie: [...cookies].map(([k, v]) => `${k}=${v}`).join('; '),
      ...(body === undefined ? {} : { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token }), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(10000),
  })
  for (const cookie of response.headers.getSetCookie()) {
    const pair = cookie.split(';')[0], index = pair.indexOf('=')
    cookies.set(pair.slice(0, index), pair.slice(index + 1))
  }
  const text = await response.text()
  return { status: response.status, text }
}
function percentile(sorted, p) { return sorted[Math.max(0, Math.ceil(sorted.length * p) - 1)] ?? 0 }
async function stage(name, concurrency, duration, measure = true) {
  const latencies = [], statuses = {}, endpoints = {}
  let failures = 0, completed = 0, sequence = 0
  const started = performance.now(), deadline = started + duration * 1000
  async function timed(path, body, expected, label) {
    const start = performance.now()
    let status = 'NETWORK', result
    try { result = await request(path, body, body === undefined ? {} : { 'Idempotency-Key': randomUUID() }); status = result.status }
    catch { /* Timeouts and connection failures count against the error budget. */ }
    const elapsed = performance.now() - start
    latencies.push(elapsed); completed++
    statuses[status] = (statuses[status] ?? 0) + 1
    const metric = endpoints[label] ??= { count: 0, errors: 0, totalMs: 0 }
    metric.count++; metric.totalMs += elapsed
    if (status !== expected) { failures++; metric.errors++ }
    return result
  }
  await Promise.all(Array.from({ length: concurrency }, async () => {
    while (!interrupted && performance.now() < deadline) {
      const n = sequence++
      const product = `00000000-0000-4000-8000-${String(name === 'hot-product' ? 1 : n % 500 + 1).padStart(12, '0')}`
      if (name !== 'browse') {
        const created = await timed('/api/reservations', { productId: product, quantity: 1 }, 201, 'reserve')
        if (created?.status === 201) {
          const order = JSON.parse(created.text).orderId
          await timed(`/api/reservations/${order}/cancel`, {}, 200, 'cancel')
        }
      } else {
        // 60% catalog, 30% item reads, 10% paginated staff order list.
        const path = n % 10 < 6 ? '/api/products' : n % 10 < 9 ? `/api/products/${product}` : '/api/orders?page=1'
        await timed(path, undefined, 200, n % 10 < 6 ? 'catalog' : n % 10 < 9 ? 'item' : 'orders')
      }
    }
  }))
  const elapsedSeconds = (performance.now() - started) / 1000
  latencies.sort((a, b) => a - b)
  const result = { scenario: name, concurrency, durationSeconds: elapsedSeconds, requests: completed,
    requestsPerSecond: completed / elapsedSeconds, successfulRequestsPerSecond: (completed - failures) / elapsedSeconds,
    errorPercent: completed ? failures / completed * 100 : 100, p50Ms: percentile(latencies, .5),
    p95Ms: percentile(latencies, .95), p99Ms: percentile(latencies, .99), maxMs: latencies.at(-1) ?? 0,
    statuses, endpoints }
  result.meetsTargets = result.p95Ms < 500 && result.errorPercent < 1
  if (measure) {
    results.push(result)
    writeFileSync(join(run, 'results.json'), JSON.stringify({ metadata, results }, null, 2))
    console.log(`${name} concurrency=${concurrency}: ${result.requestsPerSecond.toFixed(1)} req/s, p95=${result.p95Ms.toFixed(1)}ms, errors=${result.errorPercent.toFixed(2)}%`)
  }
}
const metadata = { timestamp: new Date().toISOString(), platform: os.platform(), node: process.version,
  cpu: os.cpus()[0]?.model, logicalCpus: os.cpus().length, ramGB: os.totalmem() / 2 ** 30,
  seconds, warmupSeconds, levels, products: 500, seededOrders: 5000, authentication: 'One shared authenticated Admin session',
  model: 'Closed-loop, no think time; one in-flight request per worker',
  scope: 'Loopback HTTP; API, database and generator on the same computer; Stripe disabled; Development middleware, Release build',
  thresholds: { p95Ms: 500, maximumErrorPercent: 1 },
}

try {
  await requireFreePort(pgPort); await requireFreePort(apiPort)
  console.log('Creating isolated benchmark database and Release API...')
  command('dotnet', ['publish', 'backend/ShelfSync.Api/ShelfSync.Api.csproj', '-c', 'Release', '-o', join(run, 'app'), '--no-restore'])
  const passwordFile = join(run, 'bootstrap-password.txt')
  writeFileSync(passwordFile, password)
  try {
    command(join(pg, 'initdb.exe'), ['-D', data, '-U', 'shelfsync_bench', '--auth-host=scram-sha-256',
      '--auth-local=scram-sha-256', `--pwfile=${passwordFile}`, '--no-locale', '-E', 'UTF8'])
  } finally { unlinkSync(passwordFile) }
  command(join(pg, 'pg_ctl.exe'), ['-D', data, '-l', join(run, 'postgres.log'), '-o', `-h 127.0.0.1 -p ${pgPort}`, '-w', '-t', '30', 'start'])
  databaseStarted = true
  command(join(pg, 'createdb.exe'), ['-w', '-h', '127.0.0.1', '-p', String(pgPort), '-U', 'shelfsync_bench', 'shelfsync_bench'])
  const { readFileSync } = await import('node:fs')
  sql(readFileSync(join(root, 'database/schema.sql'), 'utf8'))
  for (const file of readdirSync(join(root, 'database/migrations')).filter(f => f.endsWith('.sql')).sort()) {
    sql(readFileSync(join(root, 'database/migrations', file), 'utf8'))
  }
  if (process.env.BENCH_VERIFY === 'true') {
    sql('CREATE DATABASE shelfsync_test;')
    sql(readFileSync(join(root, 'database/schema.sql'), 'utf8'), 'shelfsync_test')
    for (const file of readdirSync(join(root, 'database/migrations')).filter(f => f.endsWith('.sql')).sort()) {
      sql(readFileSync(join(root, 'database/migrations', file), 'utf8'), 'shelfsync_test')
    }
    const output = command('dotnet', ['test', 'tests/ShelfSync.Tests', '-c', 'Release', '--no-restore', '--logger', 'console;verbosity=minimal'], {
      env: { ...env, SHELFSYNC_TEST_CONNECTION: `Host=127.0.0.1;Port=${pgPort};Database=shelfsync_test;Username=shelfsync_bench;Password=${password}` },
    })
    writeFileSync(join(run, 'tests.log'), output)
    console.log(output.trim())
  }
  sql(`INSERT INTO products(id,sku,name,price_cents)
    SELECT ('00000000-0000-4000-8000-'||lpad(i::text,12,'0'))::uuid, 'BENCH-'||lpad(i::text,4,'0'), 'Benchmark product '||i, 297 FROM generate_series(1,500) i;
    INSERT INTO inventory(product_id,shelf_units) SELECT id,10000 FROM products;
    INSERT INTO orders(id,product_id,quantity,unit_price_cents,state,expires_at)
    SELECT gen_random_uuid(), ('00000000-0000-4000-8000-'||lpad((i%500+1)::text,12,'0'))::uuid, 1,297,'CANCELLED',now()+interval '1 day' FROM generate_series(1,5000) i;
    ANALYZE;`)
  const salt = randomBytes(16), header = Buffer.alloc(13)
  header[0] = 1; header.writeUInt32BE(2, 1); header.writeUInt32BE(100000, 5); header.writeUInt32BE(16, 9)
  const hash = Buffer.concat([header, salt, pbkdf2Sync(password, salt, 100000, 32, 'sha512')]).toString('base64')
  const log = openSync(join(run, 'api.log'), 'a')
  server = spawn('dotnet', [join(run, 'app/ShelfSync.Api.dll'), '--contentRoot', join(run, 'app'), '--urls', base], {
    cwd: root, windowsHide: true, stdio: ['ignore', log, log],
    env: { ...env, ASPNETCORE_ENVIRONMENT: 'Development', Stripe__Enabled: 'false', Operator__Username: 'benchmark',
      Operator__PasswordHash: hash, Logging__LogLevel__Default: 'Warning',
      ConnectionStrings__ShelfSync: `Host=127.0.0.1;Port=${pgPort};Database=shelfsync_bench;Username=shelfsync_bench;Password=${password}` },
  })
  closeSync(log)
  server.on('error', error => console.error('API process error:', error.message))
  let ready = false
  for (let attempt = 0; attempt < 60; attempt++) {
    if (server.exitCode !== null) throw new Error('Benchmark API exited; inspect api.log.')
    try { if ((await request('/health/ready')).status === 200) { ready = true; break } } catch { /* startup */ }
    await delay(500)
  }
  if (!ready) throw new Error('Benchmark API readiness timeout.')
  token = JSON.parse((await request('/api/auth/session')).text).token
  if ((await request('/api/auth/login', { username: 'benchmark', password })).status !== 200) throw new Error('Benchmark sign-in failed.')
  token = JSON.parse((await request('/api/auth/session')).text).token
  for (const scenario of ['browse', 'reserve-cancel', 'hot-product']) {
    if (interrupted) break
    await stage(scenario, 2, warmupSeconds, false)
    for (const concurrency of levels) {
      if (interrupted) break
      await stage(scenario, concurrency, seconds)
      if (results.at(-1).errorPercent > 5 || results.at(-1).p95Ms > 5000) {
        console.log('Stopping this scenario ramp: error/latency safety threshold reached.'); break
      }
    }
  }
  const invariants = sql(`SELECT json_build_object(
    'activeReservations', (SELECT count(*) FROM orders WHERE state='RESERVED'),
    'changedStockRows', (SELECT count(*) FROM inventory WHERE shelf_units<>10000 OR picked_units<>0),
    'products', (SELECT count(*) FROM products));`).trim()
  metadata.invariants = JSON.parse(invariants)
  metadata.interrupted = interrupted
  writeFileSync(join(run, 'results.json'), JSON.stringify({ metadata, results }, null, 2))
  const rows = results.map(r => `| ${r.scenario} | ${r.concurrency} | ${r.requestsPerSecond.toFixed(1)} | ${r.p50Ms.toFixed(1)} | ${r.p95Ms.toFixed(1)} | ${r.p99Ms.toFixed(1)} | ${r.errorPercent.toFixed(2)} |`)
  writeFileSync(join(run, 'report.md'), `# ShelfSync local API benchmark\n\n${JSON.stringify(metadata, null, 2)}\n\n| Scenario | Concurrent workers | Req/s | p50 ms | p95 ms | p99 ms | Errors % |\n|---|---:|---:|---:|---:|---:|---:|\n${rows.join('\n')}\n\nNot a production-user capacity guarantee. Shared session, closed-loop requests, synthetic data, no TLS or external Stripe traffic. Write workflows make two API calls each. No automatic tuning or bypass of security controls.\n`)
  console.log(`Report: ${join(run, 'report.md')}`)
  if (interrupted) process.exitCode = 130
  if (metadata.invariants.activeReservations !== 0 || metadata.invariants.changedStockRows !== 0) {
    throw new Error('Post-load stock/order invariant failed; inspect results before accepting capacity.')
  }
} finally {
  if (server && server.exitCode === null) {
    const exited = new Promise(resolvePromise => server.once('exit', resolvePromise))
    server.kill(); await Promise.race([exited, delay(5000)])
  }
  if (databaseStarted) command(join(pg, 'pg_ctl.exe'), ['-D', data, '-m', 'fast', '-w', '-t', '30', 'stop'])
}
