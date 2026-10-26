import { spawn, spawnSync } from 'node:child_process'
import { mkdirSync, writeFileSync, unlinkSync, readFileSync, readdirSync, openSync, closeSync, existsSync } from 'node:fs'
import { randomBytes } from 'node:crypto'
import { join, resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import net from 'node:net'
import { setTimeout as delay } from 'node:timers/promises'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const test = process.argv.includes('--test')
const port = 5092, pgPort = 55442
const run = join(root, 'artifacts', 'demo-' + new Date().toISOString().replaceAll(/[:.]/g, '-'))
mkdirSync(run, { recursive: true })
const password = randomBytes(32).toString('hex')
const env = { ...process.env, PGPASSWORD: password }
const pg = process.env.DEMO_POSTGRES_BIN ?? (process.platform === 'win32' ? 'C:/Program Files/PostgreSQL/18/bin' : '/usr/lib/postgresql/18/bin')
const pgTool = name => join(pg, name + (process.platform === 'win32' ? '.exe' : ''))
let connection = process.env.SHELFSYNC_DEMO_TEST_CONNECTION
let server, databaseStarted = false, stopping = false
const log = openSync(join(run, 'demo.log'), 'a')
function command(exe, args, options = {}) {
  const result = spawnSync(exe, args, { cwd: root, windowsHide: true, env, encoding: 'utf8',
    ...(/pg_ctl(?:\.exe)?$/.test(exe) ? { stdio: 'ignore' } : {}), ...options })
  if (result.error || result.status !== 0) {
    writeFileSync(join(run, 'command-error.log'), (result.stderr ?? '') + (result.stdout ?? ''))
    throw new Error('Demo command failed: ' + exe + '. Inspect ' + join(run, 'command-error.log'))
  }
  return result.stdout
}
async function freePort(number) {
  await new Promise((resolvePromise, reject) => {
    const probe = net.createServer()
    probe.once('error', reject)
    probe.listen(number, '127.0.0.1', () => probe.close(resolvePromise))
  })
}
function sql(text) {
  if (connection) {
    const fields = Object.fromEntries(connection.split(';').filter(Boolean).map(part => {
      const index = part.indexOf('=')
      return [part.slice(0, index).trim().toLowerCase(), part.slice(index + 1)]
    }))
    const client = process.platform === 'win32' ? pgTool('psql') : 'psql'
    return command(client, ['-X', '-w', '-h', fields.host, '-p', fields.port ?? '5432', '-U', fields.username, '-d', 'shelfsync_test', '-v', 'ON_ERROR_STOP=1'], {
      input: text, env: { ...env, PGPASSWORD: fields.password ?? '' }
    })
  }
  return command(pgTool('psql'), ['-X', '-w', '-h', '127.0.0.1', '-p', String(pgPort), '-U', 'shelfsync_demo', '-d', 'shelfsync_demo', '-v', 'ON_ERROR_STOP=1'], { input: text })
}
process.on('SIGINT', () => { stopping = true })
process.on('SIGTERM', () => { stopping = true })
try {
  await freePort(port)
  if (connection) {
    if (!/(?:^|;)Database=shelfsync_test(?:;|$)/i.test(connection) || !/(?:^|;)Host=(localhost|127\.0\.0\.1)(?:;|$)/i.test(connection))
      throw new Error('Supplied demo connections must point to loopback shelfsync_test; never use the store database.')
  } else {
    await freePort(pgPort)
    if (!existsSync(pgTool('initdb'))) throw new Error('PostgreSQL 18 tools required. Set DEMO_POSTGRES_BIN if installed elsewhere.')
    const passwordFile = join(run, 'bootstrap-password.txt')
    writeFileSync(passwordFile, password)
    try { command(pgTool('initdb'), ['-D', join(run, 'data'), '-U', 'shelfsync_demo', '--auth-host=scram-sha-256', '--auth-local=scram-sha-256', '--pwfile=' + passwordFile, '--no-locale', '-E', 'UTF8']) }
    finally { unlinkSync(passwordFile) }
    command(pgTool('pg_ctl'), ['-D', join(run, 'data'), '-l', join(run, 'postgres.log'), '-o', `-h 127.0.0.1 -p ${pgPort}`, '-w', '-t', '30', 'start'])
    databaseStarted = true
    command(pgTool('createdb'), ['-h', '127.0.0.1', '-p', String(pgPort), '-U', 'shelfsync_demo', 'shelfsync_demo'])
    sql(readFileSync(join(root, 'database/schema.sql'), 'utf8'))
  }
  if (!connection) {
    // Seed products before migration 001 so fictional location profiles are generated too.
    sql(readFileSync(join(root, 'database/seed.sql'), 'utf8'))
    for (const file of readdirSync(join(root, 'database/migrations')).filter(f => f.endsWith('.sql')).sort())
      sql(readFileSync(join(root, 'database/migrations', file), 'utf8'))
  } else {
    sql(readFileSync(join(root, 'database/seed.sql'), 'utf8'))
    sql(readFileSync(join(root, 'database/migrations/001_product_profiles.sql'), 'utf8'))
  }
  sql(`INSERT INTO products(id,sku,name,price_cents)
    SELECT ('d0000000-0000-4000-8000-'||lpad(i::text,12,'0'))::uuid,'DEMO-'||lpad(i::text,3,'0'),'Demo pantry beans '||i,100+i FROM generate_series(1,48) i ON CONFLICT DO NOTHING;
    INSERT INTO inventory(product_id,shelf_units) SELECT id,20 FROM products WHERE sku LIKE 'DEMO-%' ON CONFLICT DO NOTHING;`)
  const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm'
  function npmRun(args, options = {}) {
    // Execute npm's JavaScript entry point on Windows without shell interpolation.
    if (process.platform === 'win32') return command(process.execPath, [join(dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js'), ...args], options)
    return command(npm, args, options)
  }
  if (!existsSync(join(root, 'frontend/node_modules'))) npmRun(['--prefix', 'frontend', 'ci'])
  console.log('Building the isolated demo...')
  npmRun(['--prefix', 'frontend', 'run', 'build'])
  command('dotnet', ['build', 'tools/ShelfSync.DemoHost/ShelfSync.DemoHost.csproj', '-c', 'Release'])
  const appConnection = connection ?? `Host=127.0.0.1;Port=${pgPort};Database=shelfsync_demo;Username=shelfsync_demo;Password=${password}`
  if (process.argv.includes('--stripe-webhook')) {
    const { verifyStripeWebhook } = await import('./Verify-StripeWebhook.mjs')
    await verifyStripeWebhook({ root, run, connection: appConnection, env, command, sql })
  } else {
  server = spawn('dotnet', ['tools/ShelfSync.DemoHost/bin/Release/net10.0/ShelfSync.DemoHost.dll'], {
    cwd: root, windowsHide: true, stdio: ['ignore', log, log], env: { ...env,
      SHELFSYNC_DEMO_CONNECTION: appConnection, SHELFSYNC_DEMO_PORT: String(port),
      ASPNETCORE_TEST_CONTENTROOT_SHELFSYNC_API: join(root, 'backend/ShelfSync.Api') }
  })
  server.on('error', () => { stopping = true })
  let ready = false
  for (let attempt = 0; attempt < 90; attempt++) {
    if (server.exitCode !== null) throw new Error('Demo host exited. Inspect ' + join(run, 'demo.log'))
    if (readFileSync(join(run, 'demo.log'), 'utf8').includes('DEMO_READY')) { ready = true; break }
    await delay(500)
  }
  if (!ready) throw new Error('Demo startup timed out. Inspect ' + join(run, 'demo.log'))
  console.log('Demo: http://127.0.0.1:5092')
  console.log('Accounts: demo-customer, demo-associate, demo-manager, demo-admin')
  console.log('Temporary demo password: ShelfSync-Demo-Only-2026!')
  console.log('Simulated payments only. Ctrl+C stops this host and its disposable database.')
  if (test) {
    npmRun(['--prefix', 'frontend', 'run', 'test:e2e'], { env: { ...env, DEMO_BASE_URL: `http://127.0.0.1:${port}`, DEMO_RECORD: process.env.DEMO_RECORD ?? '1' }, stdio: 'inherit' })
  } else {
    while (!stopping && server.exitCode === null) await delay(500)
  }
  }
} finally {
  if (server && server.exitCode === null) {
    const exited = new Promise(resolvePromise => server.once('exit', resolvePromise))
    server.kill(); await Promise.race([exited, delay(5000)])
    if (server.exitCode === null) { server.kill('SIGKILL'); await exited }
  }
  closeSync(log)
  if (databaseStarted) command(pgTool('pg_ctl'), ['-D', join(run, 'data'), '-m', 'fast', '-w', '-t', '30', 'stop'])
}
