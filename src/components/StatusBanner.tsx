type StatusTone = 'idle' | 'loading' | 'success' | 'error'

interface StatusBannerProps {
  tone: StatusTone
  message: string
  fallback?: string
}

const toneLabel: Record<Exclude<StatusTone, 'idle'>, string> = {
  loading: 'Loading',
  success: 'Success',
  error: 'Error',
}

export function StatusBanner({ tone, message, fallback }: StatusBannerProps) {
  const content = message || fallback
  if (!content) {
    return null
  }

  if (tone === 'idle') {
    return <p className="workspace-copy">{content}</p>
  }

  return (
    <div className={`status-banner status-banner--${tone}`} role={tone === 'error' ? 'alert' : 'status'}>
      <span className="status-banner__label">{toneLabel[tone]}</span>
      <p>{content}</p>
    </div>
  )
}

export type { StatusTone }