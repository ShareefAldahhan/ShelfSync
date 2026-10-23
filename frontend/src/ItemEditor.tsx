import { useRef, useState } from 'react'
import { mutate } from './api'
import type { Operation } from './api'
import { usePendingOperation } from './usePendingOperation'

type Location = { aisle: string | null; bay: string | null; shelf: string | null; bin: string | null }
type UpdateBody = { aisle?: string; bay?: string; shelf?: string; bin?: string; reason: string; shelfUnits?: number; expectedShelfUnits?: number }
export default function ItemEditor({ id, location, shelfUnits, mode, changed }: { id: string; location: Location; shelfUnits: number; mode: 'location' | 'stock-correction'; changed: () => void }) {
  const [retry,setRetry]=usePendingOperation<Operation>(`item-edit:${id}:${mode}`)
  const saved=retry?.body as UpdateBody | undefined
  const [open,setOpen]=useState(!!retry)
  const [fields,setFields]=useState({aisle:saved?.aisle ?? location.aisle ?? '',bay:saved?.bay ?? location.bay ?? '',shelf:saved?.shelf ?? location.shelf ?? '',bin:saved?.bin ?? location.bin ?? ''})
  const [count,setCount]=useState(String(saved?.shelfUnits ?? shelfUnits))
  const [reason,setReason]=useState(saved?.reason ?? '')
  const [pending,setPending]=useState(false)
  const [message,setMessage]=useState('')
  const busy=useRef(false)
  const correction=mode==='stock-correction'
  async function submit(event: React.FormEvent) {
    event.preventDefault();if(busy.current)return
    if(!retry && (reason.trim().length<3 || reason.trim().length>500)) {setMessage('Enter a reason of 3 to 500 characters.');return}
    if(!retry && correction && (!count.trim() || !Number.isInteger(Number(count)) || Number(count)<0 || Number(count)>1000000)) {setMessage('Enter the actual shelf count as a whole number from 0 to 1,000,000.');return}
    const body: UpdateBody=correction ? {shelfUnits:Number(count),expectedShelfUnits:shelfUnits,reason:reason.trim()} : {...fields,reason:reason.trim()}
    const op=retry ?? {key:crypto.randomUUID(),url:`/api/products/${id}/${mode}`,body}
    try {setRetry(op)} catch {setMessage('Unable to save the retry key in this tab. No change was sent.');return}
    busy.current=true;setPending(true);setMessage('')
    try {
      const result=await mutate(op)
      setRetry(null)
      if(result.ok){setMessage(correction?'Shelf count corrected and audited.':'Location saved and audited.');setOpen(false);changed()}
      else setMessage(result.body.error ?? `Update rejected (HTTP ${result.status}).`)
    } catch {setMessage('Result uncertain. Use Retry update; it keeps the same request key and values.')}
    finally{busy.current=false;setPending(false)}
  }
  return <div className="item-editor"><button type="button" className="item-edit-toggle" disabled={pending} onClick={()=>setOpen(v=>!v)}>{open ? 'Hide editor' : correction?'Correct shelf quantity':'Edit shelf / bin location'}</button>
    {open && <form onSubmit={event=>void submit(event)}><p>{correction ? 'Enter the physically counted shelf units. This replaces the shelf count; it does not add units or change picked stock.' : 'Update the item’s shelf address. Leave a field empty to clear it.'}</p>
      {correction ? <label>Actual shelf count<input type="number" min="0" max="1000000" step="1" required disabled={pending || !!retry} value={count} onChange={e=>setCount(e.target.value)} /></label> : <div className="location-edit-grid">{(['aisle','bay','shelf','bin'] as const).map(field=><label key={field}>{field}<input aria-label={`Edit ${field}`} maxLength={40} disabled={pending || !!retry} value={fields[field]} onChange={e=>setFields(v=>({...v,[field]:e.target.value}))} /></label>)}</div>}
      <label>Reason for {correction?'stock correction':'location change'}<textarea required minLength={3} maxLength={500} disabled={pending || !!retry} value={reason} onChange={e=>setReason(e.target.value)} /></label><button className="item-save-button" disabled={pending}>{pending?'Saving…':retry?'Retry update':correction?'Save shelf correction':'Save location'}</button>
    </form>}<p role="status" aria-live="polite">{message}</p></div>
}
