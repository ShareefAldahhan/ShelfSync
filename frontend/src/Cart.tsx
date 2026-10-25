import { useRef, useState } from 'react'
import { mutate } from './api'
import { redirectToCheckout } from './checkout'
import type { Operation, Product } from './api'
import { usePendingOperation } from './usePendingOperation'
export type CartLine={productId:string;quantity:number}
export default function Cart({lines,update,products,changed}:{lines:CartLine[];update:(lines:CartLine[])=>void;products:Product[];changed:()=>void}){
 const [retry,saveRetry]=usePendingOperation<Operation>('cart-checkout'),[pending,setPending]=useState(false),[message,setMessage]=useState(''),[order,setOrder]=useState('')
 const busy=useRef(false)
 async function checkout(){if(busy.current)return;busy.current=true;setPending(true);setMessage('');const op=retry??{key:crypto.randomUUID(),url:'/api/cart/checkout',body:{items:lines}}
 try{
  saveRetry(op);const result=await mutate(op)
  if(!result.ok){saveRetry(null);setMessage(result.body.error??'Checkout could not be started.');return}
  const legacyOrders=Array.isArray(result.body.orders)?result.body.orders as {orderId:string}[]:[]
  const orderId=typeof result.body.orderId==='string'?result.body.orderId:legacyOrders[0]?.orderId
  if(!orderId)throw new Error('Checkout response is incomplete.')
  setOrder(orderId)
  const reserved=(op.body as {items:CartLine[]}).items
  update(lines.map(l=>({...l,quantity:l.quantity-(reserved.find(r=>r.productId===l.productId)?.quantity??0)})).filter(l=>l.quantity>0))
  saveRetry(null);changed()
  if(typeof result.body.url==='string'){
   try { redirectToCheckout(result.body.url) }
   catch { setMessage('Your checkout was created. Use Resume checkout / payment status below to continue. Do not create another order.') }
  }else window.location.hash=`payment/${orderId}`
 }catch{setMessage('Checkout could not be confirmed. Retry the saved request to avoid duplicate orders. Your request remains saved in this tab.')}finally{busy.current=false;setPending(false)}}
 const total=lines.reduce((sum,line)=>sum+(products.find(p=>p.id===line.productId)?.priceCents??0)*line.quantity,0)
 return <section className="cart-panel table-section"><h1>Your pickup cart</h1><p>Adding items does not reserve stock. Checkout checks current prices and holds your items while you pay. Staff start picking after payment is confirmed.</p><div className="table-wrapper"><table><thead><tr><th>Product</th><th>Quantity</th><th>Line total</th><th/></tr></thead><tbody>{lines.map(line=>{const p=products.find(p=>p.id===line.productId);return <tr key={line.productId}><td>{p?.name??'Unavailable product'}</td><td><input aria-label={`Cart quantity for ${p?.name??line.productId}`} type="number" min={1} max={100} step={1} disabled={pending||!!retry} value={line.quantity} onChange={e=>{const n=Number(e.target.value);if(Number.isInteger(n)&&n>=1&&n<=100)update(lines.map(l=>l.productId===line.productId?{...l,quantity:n}:l))}}/></td><td>${((p?.priceCents??0)*line.quantity/100).toFixed(2)}</td><td><button disabled={pending||!!retry} onClick={()=>update(lines.filter(l=>l.productId!==line.productId))}>Remove</button></td></tr>})}</tbody></table></div><p>Estimated total: ${(total/100).toFixed(2)} USD. Prices and stock are confirmed by the server at checkout.</p><button className="checkout-pay-button" disabled={pending||(!lines.length&&!retry)} onClick={()=>void checkout()}>{pending?'Opening secure checkout…':retry?'Retry saved checkout':`Checkout · $${(total/100).toFixed(2)}`}</button><p>Stripe test mode · No real money is collected. Abandoned checkout holds are released after Stripe confirms expiry.</p><p role="status">{message}</p>{order&&<a className="payment-link" href={`#payment/${order}`}>Resume checkout / payment status</a>}</section>
}
