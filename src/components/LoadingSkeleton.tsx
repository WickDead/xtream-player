interface LoadingSkeletonProps {
  variant: 'row' | 'poster'
  count?: number
}

export function LoadingSkeleton({ variant, count = 1 }: LoadingSkeletonProps) {
  const items = Array.from({ length: count }, (_, index) => index)

  if (variant === 'poster') {
    return (
      <div className="poster-grid" aria-hidden="true">
        {items.map((item) => (
          <div key={item} className="poster-card skeleton-card">
            <div className="poster-art skeleton-block" />
            <div className="poster-body">
              <div className="skeleton-line skeleton-line--short" />
              <div className="skeleton-line" />
              <div className="skeleton-pill-row">
                <div className="skeleton-pill" />
                <div className="skeleton-pill" />
              </div>
            </div>
          </div>
        ))}
      </div>
    )
  }

  return (
    <div className="list-stack" aria-hidden="true">
      {items.map((item) => (
        <div key={item} className="list-row skeleton-row">
          <div className="skeleton-copy-group">
            <div className="skeleton-line skeleton-line--short" />
            <div className="skeleton-line" />
            <div className="skeleton-line skeleton-line--medium" />
          </div>
        </div>
      ))}
    </div>
  )
}