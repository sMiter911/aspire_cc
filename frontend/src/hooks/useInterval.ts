import { useEffect, useRef } from 'react'

/** Calls `callback` every `ms` milliseconds while `ms` is a number (pass null to pause). */
export function useInterval(callback: () => void, ms: number | null) {
  const saved = useRef(callback)
  saved.current = callback
  useEffect(() => {
    if (ms === null) return
    const id = setInterval(() => saved.current(), ms)
    return () => clearInterval(id)
  }, [ms])
}
