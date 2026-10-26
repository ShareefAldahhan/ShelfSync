import { useCatalog } from './useCatalog'
import PaymentCheckout from './PaymentCheckout'
import StoreOperations from './StoreOperations'
import InventoryHistory from './InventoryHistory'
import Cart from './Cart'
import type { CartLine } from './Cart'
import OrderReceipt from './OrderReceipt'
import { usePendingOperation } from './usePendingOperation'
import { isStaff, canShop } from './session'
import CustomerItem from './CustomerItem'
import ProductDetails from './ProductDetails'
import { secureFetch } from './session'
import { useEffect, useState } from 'react'
import { ShoppingBag, Search, ArrowUpRight, MapPin, RefreshCw, Leaf } from 'lucide-react'
import { ProductArt } from './ProductArt'
import { category } from './productCategory'
import { OrderActions, PosForm } from './Workflows'
import type { Product, Order } from './api'
import './App.css'

export default function App() {
  const [page, setPage] = useState(()=>window.location.hash==='#cart'?'Cart':'Inventory')
  useEffect(()=>{const sync=()=>{if(window.location.hash==='#cart')setPage('Cart');if(window.location.hash==='#orders')setPage('Orders')};window.addEventListener('hashchange',sync);return()=>window.removeEventListener('hashchange',sync)},[])
  const [paymentId,setPaymentId]=useState(()=>window.location.hash.startsWith('#payment/')?window.location.hash.slice(9):'')
  useEffect(()=>{const sync=()=>setPaymentId(window.location.hash.startsWith('#payment/')?window.location.hash.slice(9):'');window.addEventListener('hashchange',sync);return()=>window.removeEventListener('hashchange',sync)},[])
  const [storedCart,saveCart]=usePendingOperation<CartLine[]>('shopping-cart')
  const cart=storedCart??[]
  const [orderPage,setOrderPage]=useState(1)
  const [cartMessage,setCartMessage]=useState('')
  function addToCart(id:string){const existing=cart.find(l=>l.productId===id);if((existing?.quantity??0)>=100||(!existing&&cart.length>=30)){setCartMessage('Cart limit reached (30 products, 100 units each).');return}saveCart(existing?cart.map(l=>l.productId===id?{...l,quantity:l.quantity+1}:l):[...cart,{productId:id,quantity:1}]);setCartMessage('Added to pickup cart.')}
  const [itemId,setItemId]=useState(() => window.location.hash.startsWith('#product/') ? window.location.hash.slice(9) : '')
  useEffect(() => { const sync=()=>setItemId(window.location.hash.startsWith('#product/') ? window.location.hash.slice(9) : ''); window.addEventListener('hashchange',sync); return ()=>window.removeEventListener('hashchange',sync) }, [])
  const [catalogPage, setCatalogPage] = useState(1)
  const [cartProducts, setCartProducts] = useState<Product[]>([])
  const [posProducts, setPosProducts] = useState<Product[]>([])
  const [orders, setOrders] = useState<Order[]>([])
  const [search, setSearch] = useState('')
  const [department, setDepartment] = useState('All products')
  const [sort, setSort] = useState('featured')
  const [orderSearch, setOrderSearch] = useState('')
  const [stateFilter, setStateFilter] = useState('ALL')
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer) }, [])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [revision, setRevision] = useState(0)
  const refresh = () => setRevision(v => v + 1)
  useEffect(() => {
    const controller = new AbortController()
    async function load() {
      setLoading(true); setError('')
      try {
        const response = await secureFetch(`/api/orders?page=${orderPage}`, { signal: controller.signal })
        if (!response.ok) throw new Error(`Unable to load orders (HTTP ${response.status}).`)
        const recent: unknown = await response.json()
        if (!Array.isArray(recent)) throw new Error('Unexpected order response.')
        if (!controller.signal.aborted) setOrders(recent as Order[])
      } catch (err) { if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Unable to load data.') }
      finally { if (!controller.signal.aborted) setLoading(false) }
    }
    void load()
    return () => controller.abort()
  }, [revision,orderPage])
  useEffect(()=>{if(page!=='Orders'&&page!=='Payments')return;const timer=setInterval(()=>setRevision(v=>v+1),15000);const reload=()=>setRevision(v=>v+1);window.addEventListener('focus',reload);return()=>{clearInterval(timer);window.removeEventListener('focus',reload)}},[page])
  const catalog = useCatalog({ search, department, sort, page: catalogPage, revision })
  const products = catalog.items
  const filtered = products
  const cartIds = cart.map(line => line.productId).join(',')
  useEffect(() => {
    const controller = new AbortController()
    if (!cartIds) return () => controller.abort()
    void Promise.all(cartIds.split(',').map(async id => {
      const response = await secureFetch(`/api/products/${id}`, { signal: controller.signal })
      if (!response.ok) throw new Error('Unable to load a cart item. Refresh before checking out.')
      return response.json() as Promise<Product>
    })).then(items => { if (!controller.signal.aborted) setCartProducts(items) })
      .catch(error => { if (!controller.signal.aborted) setError(String(error)) })
    return () => controller.abort()
  }, [cartIds, revision])
  useEffect(() => {
    if (page !== 'POS Sales' || !isStaff()) return
    const controller = new AbortController()
    void secureFetch('/api/products', { signal: controller.signal }).then(async response => {
      if (!response.ok) throw new Error('Unable to load store checkout products.')
      return response.json() as Promise<Product[]>
    }).then(items => { if (!controller.signal.aborted) setPosProducts(items) })
      .catch(error => { if (!controller.signal.aborted) setError(String(error)) })
    return () => controller.abort()
  }, [page, revision])
  const effectiveState = (o: Order) => o.state === 'RESERVED' && Date.parse(o.expiresAt) <= now ? 'EXPIRED' : o.state
  const visibleOrders = orders.filter(o => (stateFilter === 'ALL' || effectiveState(o) === stateFilter) && `${o.id} ${o.sku} ${o.name}`.toLowerCase().includes(orderSearch.toLowerCase()))
  if(paymentId)return <PaymentCheckout key={paymentId} id={paymentId} back={()=>{window.location.hash='shop-products'}}/>
  if (itemId && !isStaff()) return <CustomerItem key={itemId} id={itemId} addToCart={addToCart} back={()=>{window.location.hash='shop-products'}} />
  if (itemId) return <ProductDetails key={itemId} id={itemId} back={()=>{window.location.hash='shop-products'}} changed={refresh} />
  return <div className="storefront">
    <div className="announcement">A little local. A lot to love. <span>Shop your everyday essentials for local pickup.</span></div>
    <header className="store-header"><button className="store-logo" onClick={() => setPage('Inventory')}><ShoppingBag size={28} />ShelfSync<span>MARKET</span></button><div className="store-search"><Search size={18} /><input aria-label="Search products" placeholder="Find your everyday favorites…" value={search} onChange={e => { setSearch(e.target.value); setCatalogPage(1); setPage('Inventory') }} /></div><div className="store-location"><MapPin size={18} /><span>Your neighborhood store<small>Local pickup</small></span></div><button className="bag-button" onClick={() => setPage('Orders')}><ShoppingBag size={20} /><span>{isStaff()?'Pickup queue':'My orders'}</span><b>{orders.filter(o => ['RESERVED','PICKED','SHORTAGE_PENDING'].includes(effectiveState(o))).length}</b></button></header>
    <nav className="store-nav" aria-label="Store navigation"><div>{(isStaff()?(canShop()?['Inventory','Cart','Orders','Payments','Stockroom','History','POS Sales']:['Inventory','Orders','Stockroom','History','POS Sales']):['Inventory','Cart','Orders','Payments']).map(label => <button key={label} aria-current={page === label ? 'page' : undefined} className={page === label ? 'selected' : ''} onClick={() => setPage(label)}>{label === 'Inventory' ? 'Shop all' : label === 'Orders' ? (isStaff()?'Pickup orders':'My orders') : label === 'POS Sales' ? 'Store checkout' : label}</button>)}</div><span><Leaf size={14} /> Thoughtfully stocked. Simply shopped.</span></nav>
    <main className="shop-main">
    {page === 'Inventory' && <><section className="shop-hero"><div className="hero-copy"><div className="hero-eyebrow">THE EVERYDAY, DONE WELL</div><h1>Your favorites.<br />Just around<br /><em>the corner.</em></h1><p>From pantry staples to little daily essentials.<br />Add your favorites to the cart and check out securely.</p><a href="#shop-products">Explore the shelves <ArrowUpRight size={18} /></a></div><div className="hero-art" aria-hidden="true"><div className="hero-orbit" />{products.slice(0,3).map((p,i) => <div className={`hero-package package-${i}`} key={p.id}><ProductArt product={p} /></div>)}<div className="hero-stamp">YOUR DAILY<br /><strong>GOOD STUFF</strong><br />ALL IN ONE PLACE</div></div></section><div className="service-strip"><span><b>01</b> Fill your cart</span><span><b>02</b> Pay securely</span><span><b>03</b> We pick, you collect</span><small>Stock held during checkout</small></div></>}
    {page==='Payments'&&<section className="payment-help"><h2>Pay for your pickup</h2><p>Checkout holds your items while you pay through Stripe. Staff prepare your order after payment is confirmed. No real money is collected in test mode.</p><button onClick={refresh}>Refresh payment availability</button></section>}
    {page !== 'Inventory' && <div className="shop-section-heading"><h1>{page === 'Orders' ? (isStaff()?'Pickup orders':'My orders') : page === 'POS Sales' ? 'Store checkout' : page}</h1></div>}
    {error && <div className="error-message" role="alert">{error} <button onClick={refresh}>Try again</button></div>}
    <div hidden={page !== 'Inventory'} id="shop-products"><div className="shop-section-heading"><div className="eyebrow">A WELL-STOCKED EVERYDAY</div><h2>Meet your daily essentials<span>({catalog.total})</span></h2><p>A few good things for your pantry, your home, and everything in between.</p></div><div className="shop-toolbar"><div className="category-tabs">{['All products','Pantry','Dairy & eggs','Produce','Bakery','Home & care'].map(c => <button key={c} aria-pressed={department === c} className={department === c ? 'selected' : ''} onClick={() => { setDepartment(c); setCatalogPage(1) }}>{c}</button>)}</div><label className="sort-label">Sort by <select aria-label="Sort products" value={sort} onChange={e => { setSort(e.target.value); setCatalogPage(1) }}><option value="featured">Featured</option><option value="price-low">Price: low to high</option><option value="price-high">Price: high to low</option></select></label></div>
    {catalog.loading && <p role="status" className="loading-note">Refreshing the shelves…</p>}
    {catalog.error&&<p role="alert">{catalog.error}</p>}
    {cartMessage&&<p role="status">{cartMessage} <button onClick={()=>setPage('Cart')}>View cart ({cart.length})</button></p>}<div className="product-grid">{filtered.map(p => <article className="product-card" key={p.id}><a className="product-image-link" href={`#product/${p.id}`} aria-label={`View details for ${p.name}`}><div className={`product-visual visual-${category(p).split(' ')[0].toLowerCase()}`}><span className="product-category">{category(p)}</span><ProductArt product={p} /></div></a><div className="product-info"><small>{p.sku} · {category(p)}</small><h3><a href={`#product/${p.id}`}>{p.name}</a></h3><div className="product-price">{new Intl.NumberFormat('en-US', { style:'currency',currency:'USD' }).format(p.priceCents / 100)}<span> / each</span></div><p className="stock-label"><i />{p.quarantined ? 'Temporarily unavailable' : p.shelfUnits === 0 ? 'Out of shelf stock' : canShop()?'Availability checked at checkout':'View inventory and shelf location'}</p>{canShop()?<><button className="cart-add" disabled={p.quarantined||p.shelfUnits===0} onClick={()=>addToCart(p.id)}>Add to pickup cart</button></>:<a className="associate-item-link" href={`#product/${p.id}`}>View item details</a>}</div></article>)}</div>{!catalog.loading && !filtered.length && <div className="empty-state">No products found. Try another search or department.</div>}<nav className="page-controls" aria-label="Product pages"><button disabled={catalogPage===1||catalog.loading} onClick={()=>setCatalogPage(p=>p-1)}>Previous products</button><span>Page {catalogPage} of {Math.max(1,Math.ceil(catalog.total/catalog.pageSize))}</span><button disabled={catalog.loading||catalogPage*catalog.pageSize>=catalog.total} onClick={()=>setCatalogPage(p=>p+1)}>Next products</button></nav><div className="shop-bottom"><span>{catalog.total} matching products · Prices in USD</span><button onClick={refresh} disabled={loading}><RefreshCw size={14} />Refresh availability</button></div></div>
    <div hidden={page !== 'Orders'&&page !== 'Payments'}><section className="table-section"><div className="section-heading"><h2>{page==='Payments'?'Your order payments':'Orders'} · page {orderPage}</h2><div className="order-filters"><input aria-label="Search orders" placeholder="Order ID, SKU or product…" value={orderSearch} onChange={e => setOrderSearch(e.target.value)} /><select aria-label="Filter orders by state" value={stateFilter} onChange={e => setStateFilter(e.target.value)}>{["ALL", "RESERVED", "PICKED", "SHORTAGE_PENDING", "RESTOCK_PENDING", "CANCELLED", "EXPIRED", "FULFILLED"].map(state => <option key={state} value={state}>{state.replaceAll("_", " ")}</option>)}</select></div></div><div className="table-wrapper"><table><thead><tr>{['Order', 'Product', 'Quantity', 'State', 'Expires', 'Reason', 'Actions'].map(h => <th key={h}>{h}</th>)}</tr></thead><tbody>{visibleOrders.map(o => <tr key={o.id}><td className="product-id">{o.id}</td><td>{o.sku} · {o.name}</td><td>{o.quantity}</td><td><span className="state-badge">{o.state==='RESERVED'&&o.paymentState==='PAID'?'PAID - Awaiting picking':effectiveState(o).replaceAll("_", " ")}</span>{['PICKED','FULFILLED'].includes(o.state)&&<small> Payment: {o.paymentState??'Not started'}</small>}{effectiveState(o) !== o.state && <small> Awaiting expiry sweep</small>}</td><td>{o.paymentState==='PAID'?'Paid · held for pickup':o.paymentState&&['EXPIRED','FAILED','REFUNDED'].includes(o.paymentState)?'Checkout closed':o.paymentState&&['READY','OPEN','PROCESSING','REVIEW'].includes(o.paymentState)?'Checkout in progress':new Date(o.expiresAt).toLocaleString()}</td><td>{o.reason ?? '—'}</td><td><OrderActions order={o} changed={refresh} />{o.paymentState==='PAID'&&<OrderReceipt id={o.id}/>}<a className={`payment-link ${canShop()&&['RESERVED','PICKED'].includes(o.state)&&effectiveState(o)!=='EXPIRED'&&!['PAID','PROCESSING','REVIEW','REFUNDED'].includes(o.paymentState??'')?'payment-link-pay':''}`} href={`#payment/${o.id}`}>{!canShop()?'Payment status':o.paymentState==='PAID'?'View payment':['RESERVED','PICKED'].includes(effectiveState(o))?'Pay securely':'Payment details'}</a>{effectiveState(o)==='RESERVED'&&o.paymentState!=='PAID'&&<small className="payment-waiting">Awaiting payment · not in the picking queue.</small>}</td></tr>)}</tbody></table>{!loading && !visibleOrders.length && <p className="empty-state">No matching orders. Try another filter or start checkout from your cart.</p>}</div></section></div>
    {(page==='Orders'||page==='Payments')&&<div className="page-controls"><button disabled={orderPage===1} onClick={()=>setOrderPage(p=>p-1)}>Previous orders</button>Page {orderPage}<button disabled={orders.length<100} onClick={()=>setOrderPage(p=>p+1)}>Next orders</button></div>}
    {canShop()&&page==='Cart'&&<Cart lines={cart} update={saveCart} products={cartProducts} changed={refresh}/>}
    {isStaff()&&page==='Stockroom'&&<StoreOperations changed={refresh}/>}
    {isStaff()&&page==='History'&&<InventoryHistory/>}
    {isStaff() && <div hidden={page !== 'POS Sales'}><PosForm products={posProducts} changed={refresh} /></div>}
    </main><footer className="store-footer"><strong>ShelfSync<span> Market</span></strong><p>Good things, close to home.</p><small>A fictional local store · Product illustrations are representative · Prices shown in USD</small></footer></div>
}
