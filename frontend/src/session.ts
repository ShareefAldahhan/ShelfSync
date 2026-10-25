export type UserSession = { authenticated: boolean; userId: string | null; username: string | null; roles: string[] }
let csrfToken = ''
let current: UserSession = { authenticated:false,userId:null,username:null,roles:[] }
export const currentSession = () => current
export const isStaff = () => current.roles.some(r=>['Associate','Manager','Admin'].includes(r))
export const isManagement = () => current.roles.some(r=>['Manager','Admin'].includes(r))
export const canShop = () => current.roles.some(r=>['Customer','Manager','Admin'].includes(r))
export const isAdmin = () => current.roles.includes('Admin')
export async function session() {
  const response=await fetch('/api/auth/session',{cache:'no-store'})
  if(!response.ok)throw new Error('Unable to connect to ShelfSync.')
  const data=await response.json()
  csrfToken=data.token
  current={authenticated:data.authenticated===true,userId:data.userId??null,username:data.username??null,roles:Array.isArray(data.roles)?data.roles:[]}
  return current.authenticated
}
export async function secureFetch(url: string,init: RequestInit={}) {
  if(!csrfToken)await session()
  const headers=new Headers(init.headers);headers.set('X-CSRF-TOKEN',csrfToken)
  const response=await fetch(url,{...init,headers})
  if(response.status===401&&!url.startsWith('/api/auth/'))window.dispatchEvent(new Event('session-expired'))
  return response
}
