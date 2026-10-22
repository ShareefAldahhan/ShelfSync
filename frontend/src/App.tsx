import { useEffect, useState } from 'react'
import Dashboard from './Dashboard'
import { secureFetch, session } from './session'
import './App.css'

export default function App() {
  const [authenticated, setAuthenticated] = useState<boolean | null>(null)
  const [username, setUsername] = useState('operator')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [pending, setPending] = useState(false)
  useEffect(() => { void session().then(setAuthenticated).catch(e => { setError(String(e)); setAuthenticated(false) }) }, [])
  useEffect(() => {
    const expired = () => { setAuthenticated(false); setError('Your session expired. Sign in again.'); void session() }
    window.addEventListener('session-expired', expired)
    return () => window.removeEventListener('session-expired', expired)
  }, [])
  async function login(event: React.FormEvent) {
    event.preventDefault(); setPending(true); setError('')
    try {
      const response = await secureFetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username, password }) })
      if (!response.ok) throw new Error(response.status === 429 ? 'Too many attempts. Try again in a minute.' : 'Sign-in failed. Check your username and password.')
      setPassword(''); await session(); setAuthenticated(true)
    } catch (err) { setError(err instanceof Error ? err.message : 'Sign-in failed.') }
    finally { setPending(false) }
  }
  async function logout() {
    setPending(true)
    try { const response = await secureFetch('/api/auth/logout', { method: 'POST' }); if (!response.ok) throw new Error(); await session(); setAuthenticated(false) }
    catch { setError('Sign-out failed. Try again.') }
    finally { setPending(false) }
  }
  if (authenticated === null) return <p className="empty-state">Connecting…</p>
  if (authenticated) return <><div className="session-bar"><span>Signed in as operator</span><button disabled={pending} onClick={() => void logout()}>Sign out</button>{error && <span role="alert">{error}</span>}</div><Dashboard /></>
  return <main className="login-shell"><form className="table-section pos-form" onSubmit={event => void login(event)}><h1>ShelfSync</h1><p>Sign in to manage your store inventory.</p><label>Username<input autoComplete="username" required value={username} onChange={e => setUsername(e.target.value)} /></label><label>Password<input autoComplete="current-password" type="password" required maxLength={1024} value={password} onChange={e => setPassword(e.target.value)} /></label><button disabled={pending}>{pending ? 'Signing in…' : 'Sign in'}</button>{error && <p role="alert">{error}</p>}</form></main>
}
