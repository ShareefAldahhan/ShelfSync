import { useEffect, useState } from 'react'
import { secureFetch } from './session'
type Mail={id:string;recipient:string;subject:string;body:string;createdAt:string}
export default function MailPreview(){const [messages,setMessages]=useState<Mail[]>([]),[error,setError]=useState(''),[revision,setRevision]=useState(0)
 useEffect(()=>{const c=new AbortController();void secureFetch('/api/admin/mail-preview',{signal:c.signal}).then(async r=>{if(!r.ok)throw new Error('Unable to load mail preview.');return r.json()}).then(m=>{if(!c.signal.aborted){setMessages(m);setError('')}}).catch(e=>{if(!c.signal.aborted)setError(String(e))});return()=>c.abort()},[revision])
 return <section className="accounts-workspace"><h1>Local email preview</h1><p>No external email is sent. This administrator-only inbox previews order notifications and recovery messages. Verify the account holder before privately sharing a recovery code. Messages can contain sensitive recovery codes.</p><button onClick={()=>setRevision(v=>v+1)}>Refresh inbox</button>{error&&<p role="alert">{error}</p>}{messages.map(m=><details className="item-panel" key={m.id}><summary>{m.subject} · {m.recipient} · {new Date(m.createdAt).toLocaleString()}</summary><pre className="mail-body">{m.body}</pre></details>)}{!messages.length&&<p>No preview messages yet.</p>}</section>
}
