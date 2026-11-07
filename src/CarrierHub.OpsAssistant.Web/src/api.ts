// The Ops Assistant's API, as the page uses it.

export type ToolCall = { name: string; arguments?: Record<string, unknown> | null }
export type ProposedAction = { id: string; kind: string; summary: string; expiresAt: string }
export type AskResponse = {
  answer: string
  toolCalls: ToolCall[]
  model: string
  inputTokens?: number | null
  outputTokens?: number | null
  durationMs: number
  conversationId: string
  pendingActions?: ProposedAction[] | null
}
export type AuditLine = { at: string; event: string; actor: string; detail?: string | null }
export type ActionView = {
  id: string
  kind: string
  summary: string
  status: 'Pending' | 'Approved' | 'Rejected' | 'Executed' | 'Failed'
  proposedAt: string
  expiresAt: string
  decidedBy?: string | null
  decidedAt?: string | null
  claimId?: string | null
  error?: string | null
  audit: AuditLine[]
}

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

// Streams an answer. EventSource can't POST or send an Authorization header, so this reads the
// Server-Sent Events from a fetch response itself: blocks separated by a blank line, "event:" and "data:" lines.
export async function askStream(
  question: string,
  token: string,
  conversationId: string | null,
  onEvent: (type: string, data: unknown) => void,
): Promise<void> {
  const response = await fetch('/v1/assistant/ask/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({ question, conversationId }),
  })

  // The server checks everything before the first byte, so a problem arrives as a normal status.
  if (!response.ok || !response.body) throw new ApiError(response.status, await errorText(response))

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += value.replace(/\r\n/g, '\n')

    let end: number
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const block = buffer.slice(0, end)
      buffer = buffer.slice(end + 2)

      let type = 'message'
      const data: string[] = []
      for (const line of block.split('\n')) {
        if (line.startsWith('event:')) type = line.slice(6).trim()
        else if (line.startsWith('data:')) data.push(line.slice(5).trimStart())
      }
      if (data.length > 0) onEvent(type, JSON.parse(data.join('\n')))
    }
  }
}

// Approve or reject. Refusals (403 without the write scope, 409 already decided, 502 no answer) come back as a
// message plus the action as it now stands, so the card can show both.
export async function decide(
  id: string,
  token: string,
  verb: 'approve' | 'reject',
): Promise<{ action?: ActionView; error?: string }> {
  const response = await fetch(`/v1/assistant/actions/${encodeURIComponent(id)}/${verb}`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}` },
  })
  const body = await response.json().catch(() => null)

  if (response.ok) return { action: body as ActionView }
  if (response.status === 401) throw new ApiError(401, 'The token was refused (401).')
  return { action: body?.action, error: body?.error ?? `The request failed (${response.status}).` }
}

async function errorText(response: Response): Promise<string> {
  const body = await response.json().catch(() => null)
  if (body?.error) return body.error
  if (response.status === 401) return 'The token was refused (401). Paste a valid token.'
  if (response.status === 403) return 'This token may not do that (403).'
  if (response.status === 429) return 'Too many requests. Wait a moment and try again.'
  return `The request failed (${response.status}).`
}