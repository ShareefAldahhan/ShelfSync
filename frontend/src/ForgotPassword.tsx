import { useState } from 'react'
import { secureFetch, session } from './session'

export default function ForgotPassword({ back }: { back: (message?: string) => void }) {
  const [username, setUsername] = useState('')
  const [code, setCode] = useState('')
  const [password, setPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const [notice,setNotice]=useState('')
  async function requestRecovery(){if(pending||!username.trim())return;setPending(true);setError('');try{await session();const r=await secureFetch('/api/auth/forgot-password',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username})});if(!r.ok)throw new Error(r.status===429?'Too many attempts. Try again in a minute.':'Unable to request recovery.');setNotice((await r.json()).message)}catch(e){setError(String(e))}finally{setPending(false)}}
  async function reset(event: React.FormEvent) {
    event.preventDefault()
    if (pending) return
    if (password !== confirmation) { setError('The passwords do not match.'); return }
    setPending(true); setError('')
    try {
      await session()
      const response = await secureFetch('/api/auth/reset-password', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, code, newPassword: password }),
      })
      if (!response.ok) {
        const data = await response.json().catch(() => ({}))
        throw new Error(response.status === 429 ? 'Too many attempts. Try again in a minute.' : data.error ?? 'Unable to reset your password.')
      }
      setPassword(''); setConfirmation(''); setCode('')
      await session()
      back('Password reset. Sign in with your new password.')
    } catch (e) { setError(e instanceof Error ? e.message : 'Unable to reset your password.') }
    finally { setPending(false) }
  }
  return <main className="login-shell"><form className="table-section pos-form" onSubmit={e => void reset(e)}>
    <h1>Forgot password?</h1>
    <p>Ask your store administrator for a recovery code after they verify your identity. Enter the code here within 30 minutes and choose your new password.</p>
    <p>For the protected operator account, use the local Set-Operator.ps1 setup script instead.</p>
    <label>Username<input required autoComplete="username" maxLength={64} value={username} onChange={e => setUsername(e.target.value)} /></label>
    <button type="button" disabled={pending||!username.trim()} onClick={()=>void requestRecovery()}>Request recovery email preview</button>{notice&&<p role="status">{notice}</p>}<label>Recovery code<textarea required maxLength={4096} autoComplete="off" spellCheck={false} rows={3} value={code} onChange={e => setCode(e.target.value)} /></label>
    <label>New password<input required type="password" autoComplete="new-password" minLength={12} maxLength={1024} value={password} onChange={e => setPassword(e.target.value)} /></label>
    <label>Confirm new password<input required type="password" autoComplete="new-password" minLength={12} maxLength={1024} value={confirmation} onChange={e => setConfirmation(e.target.value)} /></label>
    <p>Use at least 12 characters. After a successful reset, previous sessions and this code stop working.</p>
    <button disabled={pending}>{pending ? 'Resetting…' : 'Reset password'}</button>
    <button type="button" className="login-alternative" disabled={pending} onClick={() => back()}>Back to sign in</button>
    {error && <p role="alert">{error}</p>}
  </form></main>
}
