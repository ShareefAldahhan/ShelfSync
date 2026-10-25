import { useEffect, useState } from 'react'
import { secureFetch, currentSession } from './session'
import './Accounts.css'

type User = { id:string;username:string;enabled:boolean;isBootstrap:boolean;roles:string[] }
type Audit = { id:string;action:string;detail:string;createdAt:string;targetUserId:string }
export default function AccountAdmin() {
  const [users,setUsers]=useState<User[]>([])
  const [audit,setAudit]=useState<Audit[]>([])
  const [revision,setRevision]=useState(0)
  const [error,setError]=useState('')
  const [message,setMessage]=useState('')
  const [busy,setBusy]=useState(false)
  const [username,setUsername]=useState('')
  const [password,setPassword]=useState('')
  const [role,setRole]=useState('Associate')
  const [selected,setSelected]=useState('')
  const [newRole,setNewRole]=useState('Associate')
  const [enabled,setEnabled]=useState(true)
  const [reason,setReason]=useState('')
  const [reset,setReset]=useState('')
  const [recovery,setRecovery]=useState<{code:string;username:string}|null>(null)
  useEffect(()=>{
    const controller=new AbortController()
    async function load(){try{
      const [u,a]=await Promise.all(['/api/admin/users','/api/admin/audit'].map(async url=>{const r=await secureFetch(url,{signal:controller.signal});if(!r.ok)throw new Error(`Unable to load accounts (HTTP ${r.status}).`);return r.json()}))
      if(!controller.signal.aborted){setUsers(u);setAudit(a);setError('')}
    }catch(e){if(!controller.signal.aborted)setError(e instanceof Error?e.message:'Unable to load accounts.')}}
    void load();return()=>controller.abort()
  },[revision])
  async function post(url:string,body:object){
    if(busy)return false;setBusy(true);setError('');setMessage('')
    try{const r=await secureFetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});if(!r.ok){const data=await r.json().catch(()=>({}));throw new Error(data.error??`Request rejected (${r.status}).`)}setMessage('Account update saved.');setRevision(v=>v+1);return true}
    catch(e){setError(e instanceof Error?e.message:'Unable to save. Refresh to check the result before retrying.');return false}finally{setBusy(false)}
  }
  async function issueRecovery() {
    if(busy||!selected)return
    setBusy(true);setError('');setRecovery(null)
    try {
      const r=await secureFetch(`/api/admin/users/${selected}/recovery-code`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({reason})})
      const data=await r.json().catch(()=>({}))
      if(!r.ok)throw new Error(data.error??'Unable to issue recovery code.')
      setRecovery(data);setRevision(v=>v+1)
    }catch(e){setError(e instanceof Error?e.message:'Unable to issue recovery code.')}finally{setBusy(false)}
  }
  return <section className="accounts-workspace"><div className="section-heading"><div><h1>Account administration</h1><p>Individual accounts, access and security history.</p></div><button onClick={()=>setRevision(v=>v+1)}>Refresh</button></div>
    {error&&<p className="error-message" role="alert">{error}</p>}{message&&<p role="status">{message}</p>}
    <div className="accounts-columns"><form className="item-panel" onSubmit={e=>{e.preventDefault();void post('/api/admin/users',{username,password,role}).then(ok=>{if(ok){setUsername('');setPassword('')}})}}><h2>Create an account</h2><label>Username<input required minLength={3} maxLength={64} autoComplete="off" value={username} onChange={e=>setUsername(e.target.value)} /></label><label>Initial password<input type="password" required minLength={12} maxLength={1024} autoComplete="new-password" value={password} onChange={e=>setPassword(e.target.value)} /></label><label>Account role<select value={role} onChange={e=>setRole(e.target.value)}>{['Customer','Associate','Manager','Admin'].map(r=><option key={r}>{r}</option>)}</select></label><p>Share the initial password privately. Users can change their password after signing in.</p><button disabled={busy}>Create account</button></form>
    <div className="item-panel"><h2>Change access</h2><label>Account<select value={selected} onChange={e=>{setSelected(e.target.value);setRecovery(null);const u=users.find(u=>u.id===e.target.value);if(u){setNewRole(u.roles[0]??'Customer');setEnabled(u.enabled)}}}><option value="">Select an account</option>{users.filter(u=>!u.isBootstrap&&u.id!==currentSession().userId).map(u=><option key={u.id} value={u.id}>{u.username}</option>)}</select></label><form onSubmit={e=>{e.preventDefault();void post(`/api/admin/users/${selected}/access`,{role:newRole,enabled,reason})}}><label>Role<select value={newRole} onChange={e=>setNewRole(e.target.value)}>{['Customer','Associate','Manager','Admin'].map(r=><option key={r}>{r}</option>)}</select></label><label className="account-checkbox"><input type="checkbox" checked={enabled} onChange={e=>setEnabled(e.target.checked)} />Enabled</label><label>Reason<input required minLength={3} maxLength={500} value={reason} onChange={e=>setReason(e.target.value)} /></label><button disabled={busy||!selected}>Save access</button></form><form onSubmit={e=>{e.preventDefault();void post(`/api/admin/users/${selected}/password`,{password:reset,reason}).then(ok=>{if(ok)setReset('')})}}><label>Reset password<input type="password" required minLength={12} maxLength={1024} autoComplete="new-password" value={reset} onChange={e=>setReset(e.target.value)} /></label><p>Use the reason above. Resetting credentials invalidates existing sessions.</p><button disabled={busy||!selected||reason.trim().length<3}>Reset password</button></form><h3>Forgot-password recovery</h3><p>Verify the person's identity first. Use the reason above, then give the code privately to the account holder. Issuing a code signs them out and invalidates older codes.</p><button disabled={busy||!selected||reason.trim().length<3} onClick={()=>void issueRecovery()}>Issue recovery code</button>{recovery&&<div role="status"><p>Recovery code for {recovery.username} · valid for 30 minutes. Copy it now; it is hidden when you leave this screen.</p><label>Recovery code<textarea readOnly rows={4} value={recovery.code} onFocus={e=>e.target.select()}/></label><button onClick={()=>setRecovery(null)}>Hide code</button></div>}</div></div>
    <section className="item-panel"><h2>Accounts Â· latest 100</h2><div className="table-wrapper"><table><thead><tr><th>Username</th><th>Roles</th><th>Status</th></tr></thead><tbody>{users.map(u=><tr key={u.id}><td>{u.username}{u.isBootstrap?' Â· recovery administrator':''}</td><td>{u.roles.join(', ')}</td><td>{u.enabled?'Enabled':'Disabled'}</td></tr>)}</tbody></table></div></section>
    <section className="item-panel"><h2>Access audit Â· latest 100</h2><div className="table-wrapper"><table><thead><tr><th>When</th><th>Action</th><th>Detail</th></tr></thead><tbody>{audit.map(a=><tr key={a.id}><td>{new Date(a.createdAt).toLocaleString()}</td><td>{a.action}</td><td>{a.detail}</td></tr>)}</tbody></table></div></section>
  </section>
}
