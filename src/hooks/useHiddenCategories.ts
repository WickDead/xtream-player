import { useCallback, useState } from 'react'

const STORAGE_KEY_PREFIX = 'xtream-hidden-categories-'

export function useHiddenCategories(scope: 'live' | 'vod' | 'series') {
  const storageKey = STORAGE_KEY_PREFIX + scope

  const [hiddenCategories, setHiddenCategories] = useState<Set<string>>(() => {
    try {
      const stored = localStorage.getItem(storageKey)
      return stored ? new Set(JSON.parse(stored) as string[]) : new Set()
    } catch {
      return new Set()
    }
  })

  const toggleHidden = useCallback(
    (category: string) => {
      setHiddenCategories((current) => {
        const next = new Set(current)
        if (next.has(category)) {
          next.delete(category)
        } else {
          next.add(category)
        }
        localStorage.setItem(storageKey, JSON.stringify([...next]))
        return next
      })
    },
    [storageKey],
  )

  const isHidden = useCallback(
    (category: string) => hiddenCategories.has(category),
    [hiddenCategories],
  )

  return { hiddenCategories, toggleHidden, isHidden }
}
