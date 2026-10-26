import { detectDemo } from './demo'
import PaymentAdmin from './PaymentAdmin'
import MailPreview from './MailPreview'
import ForgotPassword from './ForgotPassword'
import AccountAdmin from './AccountAdmin'
import PasswordChange from './PasswordChange'
import { currentSession, isAdmin } from './session'
import { useEffect, useState } from 'react'
import Dashboard from './Dashboard'
import { secureFetch, session } from './session'
import './App.css'
import './Storefront.css'

export default function App() {
  const [demo, setDemo] = useState(false)
  useEffect(() => { void detectDemo().then(setDemo) }, [])
  const [authenticated, setAuthenticated] = useState<boolean | null>(null)
  const [username, setUsername] = useState('operator')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [pending, setPending] = useState(false)
  const [registering,setRegistering]=useState(false)
  const [recovering,setRecovering]=useState(false)
  const [notice,setNotice]=useState('')
  const [workspace,setWorkspace]=useState('shop')
  useEffect(() => { void session().then(setAuthenticated).catch(e => { setError(String(e)); setAuthenticated(false) }) }, [])
  useEffect(() => {
    const expired = () => { setAuthenticated(false); setError('Your session expired. Sign in again.'); void session().catch(()=>{}) }
    window.addEventListener('session-expired', expired)
    return () => window.removeEventListener('session-expired', expired)
  }, [])
  async function login(event: React.FormEvent) {
    event.preventDefault(); setPending(true); setError('')
    try {
      await session()
      const response = await secureFetch(registering ? '/api/auth/register' : '/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username, password }) })
      if (!response.ok) { const data=await response.json().catch(()=>({})); throw new Error(response.status === 429 ? 'Too many attempts. Try again in a minute.' : registering ? data.error ?? 'Account could not be created.' : 'Sign-in failed. Check your username and password.') }
      setPassword(''); await session(); window.location.hash='shop-products'; setWorkspace('shop'); setAuthenticated(true)
    } catch (err) { setError(err instanceof Error ? err.message : 'Sign-in failed.') }
    finally { setPending(false) }
  }
  async function logout() {
    setPending(true)
    try { const response = await secureFetch('/api/auth/logout', { method: 'POST' }); if (!response.ok) throw new Error(); await session(); window.location.hash='shop-products'; setAuthenticated(false) }
    catch { setError('Sign-out failed. Try again.') }
    finally { setPending(false) }
  }
  if (authenticated === null) return <p className="empty-state">Connecting…</p>
  if (authenticated) return <>{demo&&<aside className="announcement" role="note">Isolated demo - Payments are simulated locally. No Stripe request or real charge is made.</aside>}<div className="session-bar"><span>{currentSession().username} · {currentSession().roles.join(', ')}</span><button onClick={()=>setWorkspace('shop')}>Shop</button>{isAdmin()&&<><button onClick={()=>setWorkspace('accounts')}>Manage accounts</button><button onClick={()=>setWorkspace('mail')}>Email preview</button><button onClick={()=>setWorkspace('payments')}>Payments</button></>}<button onClick={()=>setWorkspace('password')}>Change password</button><button disabled={pending} onClick={()=>void logout()}>Sign out</button>{error&&<span role="alert">{error}</span>}</div>{workspace==='payments'&&isAdmin()?<PaymentAdmin openOrder={id=>{window.location.hash=`payment/${id}`;setWorkspace('shop')}}/>:workspace==='mail'&&isAdmin()?<MailPreview/>:workspace==='accounts'&&isAdmin()?<AccountAdmin/>:workspace==='password'?<PasswordChange changed={()=>{setAuthenticated(false);setError('Password updated. Sign in with your new password.');void session().catch(()=>{})}}/>:<Dashboard/>}</>
  if (recovering) return <ForgotPassword back={message=>{setRecovering(false);setRegistering(false);setPassword('');setError('');setNotice(message??'')}}/>
  return <main className="login-shell"><form className="table-section pos-form" onSubmit={event=>void login(event)}><h1>ShelfSync</h1>{notice&&<p role="status">{notice}</p>}<p>{registering?'Create your customer account to shop and place pickup orders.':'Your neighborhood favorites, all in one place. Sign in to shop and place pickup orders.'}</p><label>Username<input autoComplete="username" required minLength={registering?3:undefined} maxLength={64} value={username} onChange={e=>setUsername(e.target.value)}/></label><label>Password<input autoComplete={registering?'new-password':'current-password'} type="password" required minLength={registering?12:undefined} maxLength={1024} value={password} onChange={e=>setPassword(e.target.value)}/></label><button disabled={pending}>{pending?'Please wait…':registering?'Create customer account':'Sign in'}</button><button type="button" className="login-alternative" disabled={pending} onClick={()=>{setRegistering(v=>!v);setPassword('');setError('')}}>{registering?'Already have an account? Sign in':'New here? Create a customer account'}</button><button type="button" className="login-alternative" disabled={pending} onClick={()=>{setRecovering(true);setPassword('');setError('');setNotice('')}}>Forgot password?</button>{error&&<p role="alert">{error}</p>}</form></main>
}
