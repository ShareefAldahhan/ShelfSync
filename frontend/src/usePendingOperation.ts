import { currentSession } from './session'
import { useState } from 'react'

// Keep unresolved requests in this browser tab across filtering and page reloads.
export function usePendingOperation<T>(storageKey: string) {
  const accountKey = `${currentSession().userId ?? 'anonymous'}:${storageKey}`
  const [value, setValue] = useState<T | null>(() => {
    try { const saved = sessionStorage.getItem(accountKey); return saved ? JSON.parse(saved) as T : null }
    catch { return null }
  })
  function save(next: T | null) {
    // Do not send a new operation if its retry identifier cannot be saved.
    if (next === null) sessionStorage.removeItem(accountKey)
    else sessionStorage.setItem(accountKey, JSON.stringify(next))
    setValue(next)
  }
  return [value, save] as const
}
