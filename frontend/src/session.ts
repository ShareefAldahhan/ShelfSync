let csrfToken = ''
export async function session() {
  const response = await fetch('/api/auth/session')
  if (!response.ok) throw new Error('Unable to connect to ShelfSync.')
  const data = await response.json()
  csrfToken = data.token
  return data.authenticated as boolean
}
export async function secureFetch(url: string, init: RequestInit = {}) {
  if (!csrfToken) await session()
  const response = await fetch(url, { ...init, headers: { ...init.headers, 'X-CSRF-TOKEN': csrfToken } })
  if (response.status === 401 && !url.startsWith('/api/auth/')) window.dispatchEvent(new Event('session-expired'))
  return response
}
