import { useEffect, useState } from 'react'
import { LoadingSkeleton } from '../../components/LoadingSkeleton'
import { StatusBanner, type StatusTone } from '../../components/StatusBanner'
import { previewProvider, saveProviderProfile } from '../../lib/backend'
import { getSelectedProfile } from '../../stores/appStore'
import type { AppState, ProviderPreview } from '../../types/xtream'

function normalizeServerUrl(value: string) {
  const trimmed = value.trim()
  if (!trimmed) {
    return ''
  }

  return /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`
}

function deriveProfileName(name: string, username: string, serverUrl: string) {
  const trimmedName = name.trim()
  if (trimmedName) {
    return trimmedName
  }

  const normalizedUrl = normalizeServerUrl(serverUrl)
  try {
    const host = normalizedUrl ? new URL(normalizedUrl).host : 'provider'
    return username.trim() ? `${username.trim()} @ ${host}` : host
  } catch {
    return username.trim() || 'Xtream provider'
  }
}

interface LoginPageProps {
  state: AppState
  refreshBootstrap: () => Promise<void>
  onSelectProfile: (profileId: string) => void
}

export function LoginPage({ state, refreshBootstrap, onSelectProfile }: LoginPageProps) {
  const selectedProfile = getSelectedProfile(state)
  const hasSavedSelection = state.profiles.some((profile) => profile.id === selectedProfile.id)
  const [editingProfileId, setEditingProfileId] = useState<string | null>(
    hasSavedSelection ? selectedProfile.id : null,
  )
  const [name, setName] = useState(selectedProfile.name)
  const [serverUrl, setServerUrl] = useState(selectedProfile.serverUrl)
  const [username, setUsername] = useState(selectedProfile.username)
  const [password, setPassword] = useState('')
  const [preview, setPreview] = useState<ProviderPreview | null>(null)
  const [statusMessage, setStatusMessage] = useState('')
  const [statusTone, setStatusTone] = useState<StatusTone>('idle')
  const isBusy = statusTone === 'loading'

  useEffect(() => {
    setEditingProfileId(hasSavedSelection ? selectedProfile.id : null)
    setName(selectedProfile.name)
    setServerUrl(selectedProfile.serverUrl)
    setUsername(selectedProfile.username)
    setPassword('')
    setPreview(null)
    setStatusMessage('')
    setStatusTone('idle')
  }, [hasSavedSelection, selectedProfile])

  function loadProfileForEditing(profile: typeof selectedProfile) {
    setEditingProfileId(profile.id)
    setName(profile.name)
    setServerUrl(profile.serverUrl)
    setUsername(profile.username)
    setPassword('')
    setPreview(null)
    setStatusMessage('')
    setStatusTone('idle')
  }

  async function handlePreview() {
    const normalizedServerUrl = normalizeServerUrl(serverUrl)
    const normalizedUsername = username.trim()
    const normalizedPassword = password.trim()
    const normalizedName = deriveProfileName(name, normalizedUsername, normalizedServerUrl)

    if (!normalizedServerUrl || !normalizedUsername || !normalizedPassword) {
      setStatusTone('error')
      setStatusMessage('Server URL, username, and password are required to preview a provider.')
      setPreview(null)
      return
    }

    setStatusTone('loading')
    setStatusMessage('Previewing Xtream credentials...')
    try {
      const response = await previewProvider({
        id: editingProfileId ?? undefined,
        name: normalizedName,
        serverUrl: normalizedServerUrl,
        username: normalizedUsername,
        password: normalizedPassword,
      })
      setName(normalizedName)
      setServerUrl(normalizedServerUrl)
      setUsername(normalizedUsername)
      setPreview(response)
      setStatusTone('success')
      setStatusMessage(response.message)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Preview failed'
      setStatusTone('error')
      setStatusMessage(message)
      setPreview(null)
    }
  }

  async function handleSave() {
    const normalizedServerUrl = normalizeServerUrl(serverUrl)
    const normalizedUsername = username.trim()
    const normalizedPassword = password.trim()
    const normalizedName = deriveProfileName(name, normalizedUsername, normalizedServerUrl)

    if (!normalizedServerUrl || !normalizedUsername || !normalizedPassword) {
      setStatusTone('error')
      setStatusMessage('Server URL, username, and password are required to save a provider.')
      return
    }

    setStatusTone('loading')
    setStatusMessage('Saving provider profile...')
    try {
      const savedProfile = await saveProviderProfile({
        id: editingProfileId ?? undefined,
        name: normalizedName,
        serverUrl: normalizedServerUrl,
        username: normalizedUsername,
        password: normalizedPassword,
      })
      setName(normalizedName)
      setServerUrl(normalizedServerUrl)
      setUsername(normalizedUsername)
      await refreshBootstrap()
      setEditingProfileId(savedProfile.id)
      onSelectProfile(savedProfile.id)
      setStatusTone('success')
      setStatusMessage('Provider profile saved to SQLite.')
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Save failed'
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  return (
    <div className="view-stack">
      <section className="split-layout">
        <article className="form-card">
          <p className="card-label">Provider profile</p>
          <h3>Saved Xtream credentials</h3>
          <p className="workspace-copy">
            Save a real Xtream provider here. When no providers exist, this form
            creates a new one instead of depending on placeholder records.
          </p>

          <div className="form-grid">
            <div className="field">
              <label htmlFor="server-url">Server URL</label>
              <input
                id="server-url"
                value={serverUrl}
                onChange={(event) => setServerUrl(event.target.value)}
              />
            </div>
            <div className="field">
              <label htmlFor="profile-name">Profile name</label>
              <input
                id="profile-name"
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
            </div>
            <div className="field">
              <label htmlFor="username">Username</label>
              <input
                id="username"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
              />
            </div>
            <div className="field">
              <label htmlFor="password">Password</label>
              <input
                id="password"
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                placeholder="Enter Xtream password"
              />
            </div>
          </div>

          <div className="hero-actions" style={{ marginTop: '18px' }}>
            <button
              type="button"
              className="secondary-action"
              disabled={isBusy}
              onClick={() => {
                setEditingProfileId(null)
                setName('')
                setServerUrl('')
                setUsername('')
                setPassword('')
                setPreview(null)
                setStatusTone('success')
                setStatusMessage('Creating a new provider profile.')
              }}
            >
              New provider
            </button>
            <button type="button" className="secondary-action" onClick={handlePreview} disabled={isBusy}>
              Preview provider
            </button>
            <button type="button" className="primary-action" onClick={handleSave} disabled={isBusy}>
              Save to SQLite
            </button>
          </div>

          <StatusBanner
            tone={statusTone}
            message={statusMessage}
            fallback="Use Preview to call player_api.php and inspect the account."
          />
        </article>

        <article className="detail-card">
          <p className="card-label">Backend response</p>
          <div className="list-stack" style={{ marginTop: '18px' }}>
            {isBusy && !preview ? <LoadingSkeleton variant="row" count={3} /> : null}
            {preview ? (
              <>
                <div className="list-row">
                  <div>
                    <h3>{preview.displayName}</h3>
                    <p>
                      {preview.accountStatus} • max {preview.maxConnections ?? 'unknown'}
                    </p>
                  </div>
                </div>
                <div className="list-row">
                  <div>
                    <h3>Allowed formats</h3>
                    <p>{preview.allowedFormats.join(', ') || 'No formats reported'}</p>
                  </div>
                </div>
                <div className="list-row">
                  <div>
                    <h3>Suggested endpoints</h3>
                    <p>{preview.suggestedEndpoints.join(' • ')}</p>
                  </div>
                </div>
              </>
            ) : null}
            {state.profiles.length === 0 && !isBusy ? (
              <div className="list-row">
                <div>
                  <h3>No saved providers</h3>
                  <p>Add your first Xtream server with the form on the left.</p>
                </div>
              </div>
            ) : null}
            {!isBusy && state.profiles.map((profile) => {
              const isActive = profile.id === state.selectedProfileId

              return (
                <div key={profile.id} className={isActive ? 'list-row active-provider-row' : 'list-row'}>
                  <div>
                    <h3>{profile.name}</h3>
                    <p>
                      {profile.serverUrl} • max {profile.maxConnections} connections
                    </p>
                  </div>
                  <button
                    type="button"
                    className="inline-action"
                    aria-pressed={isActive}
                    onClick={() => {
                      loadProfileForEditing(profile)
                      onSelectProfile(profile.id)
                    }}
                  >
                    {isActive ? 'Active · Edit' : 'Select'}
                  </button>
                </div>
              )
            })}
          </div>
        </article>
      </section>
    </div>
  )
}
