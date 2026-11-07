import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, askStream, decide, type ActionView, type AskResponse, type ProposedAction, type ToolCall } from './api'
import './App.css'

type Turn = {
  question: string
  answer: string
  tools: ToolCall[]
  actions: ProposedAction[]
  done?: AskResponse
  error?: string
}

// What the person sees while a tool runs, instead of the tool's internal name.
const TOOL_LABELS: Record<string, string> = {
  count_shipments: 'Counting shipments',
  find_shipments: 'Searching shipments',
  find_shipments_needing_attention: 'Checking what needs attention',
  get_shipment_timeline: 'Looking up the shipment',
  search_carrier_policies: "Reading the carrier's terms",
  propose_open_claim: 'Preparing a claim',
}

const EXAMPLES = [
  'Which of my shipments are late?',
  'ORDER-20003 was late. Can I get compensation from the carrier?',
  'ORDER-20001 arrived damaged, the box was crushed. Please open a claim.',
]

function describe(call: ToolCall): string {
  const label = TOOL_LABELS[call.name] ?? call.name
  const args = call.arguments ?? {}
  const detail = args.reference ?? args.orderNumber ?? args.carrier ?? args.status
  return detail ? `${label}: ${String(detail)}` : label
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : 'Something went wrong.'
}

export default function App() {
  // Kept for this browser tab only. Fine for a development demo; a real page would sign in instead.
  const [token, setToken] = useState(() => {
    try { return sessionStorage.getItem('carrierhub-token') ?? '' } catch { return '' }
  })
  const [question, setQuestion] = useState('')
  const [turns, setTurns] = useState<Turn[]>([])
  const [conversationId, setConversationId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const end = useRef<HTMLDivElement>(null)

  useEffect(() => { end.current?.scrollIntoView({ behavior: 'smooth' }) }, [turns])

  function saveToken(value: string) {
    setToken(value)
    try { sessionStorage.setItem('carrierhub-token', value) } catch { /* private window: keep it in memory only */ }
  }

  // Changes only the turn being answered (the last one).
  function updateLast(change: (turn: Turn) => Turn) {
    setTurns(all => all.map((turn, i) => (i === all.length - 1 ? change(turn) : turn)))
  }

  async function ask(text: string) {
    const q = text.trim()
    if (!q || busy || !token) return
    setQuestion('')
    setBusy(true)
    setTurns(all => [...all, { question: q, answer: '', tools: [], actions: [] }])

    try {
      await askStream(q, token, conversationId, (type, data) => {
        if (type === 'tool') {
          updateLast(t => ({ ...t, tools: [...t.tools, data as ToolCall] }))
        } else if (type === 'text') {
          updateLast(t => ({ ...t, answer: t.answer + (data as { text: string }).text }))
        } else if (type === 'done') {
          const response = data as AskResponse
          setConversationId(response.conversationId)
          updateLast(t => ({ ...t, answer: response.answer, actions: response.pendingActions ?? [], done: response }))
        } else if (type === 'error') {
          updateLast(t => ({ ...t, error: (data as { error: string }).error }))
        }
      })
    } catch (error) {
      updateLast(t => ({ ...t, error: message(error) }))
      if (error instanceof ApiError && error.status === 404) setConversationId(null)
    } finally {
      setBusy(false)
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault()
    void ask(question)
  }

  function newConversation() {
    setTurns([])
    setConversationId(null)
  }

  return (
    <div className="page">
      <header>
        <div>
          <h1>CarrierHub Ops Assistant</h1>
          <p className="quiet">Answers from your shipments and the carriers' terms. Claims are only prepared: a person approves them.</p>
        </div>
        <div className="token">
          <input
            type="password"
            placeholder="Paste a merchant token"
            value={token}
            onChange={e => saveToken(e.target.value)}
            aria-label="Merchant token"
          />
          <button type="button" className="secondary" onClick={newConversation} disabled={busy}>New conversation</button>
        </div>
      </header>

      <main>
        {turns.length === 0 && (
          <div className="empty">
            <p className="quiet">Try one of these:</p>
            {EXAMPLES.map(example => (
              <button key={example} type="button" className="example" disabled={!token || busy} onClick={() => void ask(example)}>
                {example}
              </button>
            ))}
            {!token && <p className="quiet">Paste a token first. A read-only token can ask; approving needs shipments:write.</p>}
          </div>
        )}

        {turns.map((turn, i) => (
          <section key={i} className="turn">
            <p className="question">{turn.question}</p>

            {turn.tools.length > 0 && (
              <ul className="tools">
                {turn.tools.map((call, j) => <li key={j}>{describe(call)}</li>)}
              </ul>
            )}

            {turn.answer && <p className="answer">{turn.answer}</p>}
            {!turn.answer && !turn.error && busy && i === turns.length - 1 && <p className="quiet">Thinking…</p>}
            {turn.error && <p className="error">{turn.error}</p>}

            {turn.actions.map(action => <ActionCard key={action.id} action={action} token={token} />)}

            {turn.done && (
              <p className="meta">
                {(turn.done.durationMs / 1000).toFixed(1)} s · {turn.done.inputTokens ?? '?'} tokens in, {turn.done.outputTokens ?? '?'} out
              </p>
            )}
          </section>
        ))}
        <div ref={end} />
      </main>

      <form className="ask" onSubmit={submit}>
        <input
          value={question}
          onChange={e => setQuestion(e.target.value)}
          placeholder={conversationId ? 'Ask a follow-up…' : 'Ask about your shipments…'}
          maxLength={1000}
          disabled={!token}
          aria-label="Question"
        />
        <button type="submit" disabled={!token || busy || !question.trim()}>Ask</button>
      </form>
    </div>
  )
}

// A prepared claim. The buttons call the approval endpoints with the token in the box: a read-only token is
// refused (403), which is the point. "Approved" means no answer came back: approving again is safe.
function ActionCard({ action, token }: { action: ProposedAction; token: string }) {
  const [view, setView] = useState<ActionView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const status = view?.status ?? 'Pending'

  async function run(verb: 'approve' | 'reject') {
    setBusy(true)
    setError(null)
    try {
      const result = await decide(action.id, token, verb)
      if (result.action) setView(result.action)
      setError(result.error ?? null)
    } catch (e) {
      setError(message(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className={`action ${status.toLowerCase()}`}>
      <div className="action-head">
        <span className="badge">{status}</span>
        <span>{action.summary}</span>
      </div>

      {(status === 'Pending' || status === 'Approved') && (
        <div className="action-buttons">
          <button type="button" disabled={busy} onClick={() => void run('approve')}>
            {status === 'Approved' ? 'Approve again' : 'Approve'}
          </button>
          {status === 'Pending' && (
            <button type="button" className="secondary" disabled={busy} onClick={() => void run('reject')}>Reject</button>
          )}
        </div>
      )}

      {view?.claimId && <p className="quiet">Claim {view.claimId} opened.</p>}
      {error && <p className="error">{error}</p>}

      {view && view.audit.length > 0 && (
        <ol className="audit">
          {view.audit.map((line, i) => (
            <li key={i}>
              <span>{new Date(line.at).toLocaleTimeString()}</span> <strong>{line.event}</strong> {line.actor}
              {line.detail ? ` · ${line.detail}` : ''}
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}