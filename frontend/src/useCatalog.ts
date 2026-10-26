import { useEffect, useState } from 'react'
import type { Product } from './api'
import { secureFetch } from './session'

type Catalog = { items: Product[]; total: number; page: number; pageSize: number }
type Query = { search: string; department: string; sort: string; page: number; revision: number }

export function useCatalog({ search, department, sort, page, revision }: Query) {
  const [catalog, setCatalog] = useState<Catalog>({ items: [], total: 0, page: 1, pageSize: 24 })
  const requestKey = JSON.stringify([search, department, sort, page, revision])
  const [settledKey, setSettledKey] = useState('')
  const [error, setError] = useState('')
  useEffect(() => {
    const controller = new AbortController()
    const timer = setTimeout(async () => {
      try {
        const query = new URLSearchParams({ search, department, sort, page: String(page), pageSize: '24' })
        const response = await secureFetch(`/api/catalog?${query}`, { signal: controller.signal })
        if (!response.ok) throw new Error('Unable to load products. Please try again.')
        const data: Catalog = await response.json()
        if (!controller.signal.aborted) { setCatalog(data); setError('') }
      } catch (error) {
        if (!controller.signal.aborted) setError(error instanceof Error ? error.message : 'Unable to load products.')
      } finally {
        if (!controller.signal.aborted) setSettledKey(requestKey)
      }
    }, 250)
    return () => { clearTimeout(timer); controller.abort() }
  }, [search, department, sort, page, revision, requestKey])
  return { ...catalog, loading: settledKey !== requestKey, error }
}
