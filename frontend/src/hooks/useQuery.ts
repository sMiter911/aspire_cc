import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '@/lib/api'

interface QueryState<T> {
  data: T | null
  error: string | null
  /** HTTP status of the failure, when there is one (lets pages tell 404 from 403). */
  errorStatus: number | null
  loading: boolean
}

export function describeError(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 403) return 'You do not have permission to view this.'
    if (err.status === 404) return 'Not found, or you do not have access to it.'
    return err.message
  }
  return 'Something went wrong'
}

/** Minimal fetch hook: refetches when `deps` change and exposes `reload()`. Swap for TanStack Query when it grows. */
export function useQuery<T>(fetcher: () => Promise<T>, deps: readonly unknown[] = []) {
  const [state, setState] = useState<QueryState<T>>({ data: null, error: null, errorStatus: null, loading: true })
  const [nonce, setNonce] = useState(0)
  const fetcherRef = useRef(fetcher)
  fetcherRef.current = fetcher

  useEffect(() => {
    let cancelled = false
    setState((s) => ({ ...s, loading: true, error: null, errorStatus: null }))
    fetcherRef
      .current()
      .then((data) => !cancelled && setState({ data, error: null, errorStatus: null, loading: false }))
      .catch((err: unknown) => {
        if (cancelled) return
        setState({
          data: null,
          error: describeError(err),
          errorStatus: err instanceof ApiError ? err.status : null,
          loading: false,
        })
      })
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- refetch only when the caller's deps or nonce change
  }, [nonce, ...deps])

  const reload = useCallback(() => setNonce((n) => n + 1), [])
  return { ...state, reload }
}
