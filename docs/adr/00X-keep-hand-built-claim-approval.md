# Keep the hand-built approval for claims

**Status:** accepted · **Date:** 2026-11-10

## Context
Agent Framework has a built-in tool approval (`ApprovalRequiredAIFunction`): the tool waits, the response carries a
`ToolApprovalRequestContent`, and a `CreateResponse(true/false)` message in the same session runs or stops it.
We measured it in `labs/CarrierHub.ApprovalLab` against our own pending-action approval.

## Measurements
1. The tool waits until approved, and a pending request survives a restart inside the session JSON.
2. An approval carries no approver identity: whoever continues the session approves.
3. A forged approval (a request id that was never issued, arguments chosen by the sender) **ran**, and the model's
   answer didn't mention it.

## Decision
Claims keep the hand-built approval: a different person with the write scope approves, the call runs with the
approver's token, the arguments are stored at proposal, every step is audited, and only ids stored in our database
can be approved.

## Consequences
- The built-in approval may be used for low-risk confirmations by the same user, in the same chat, if the server
  creates the response from its own stored request and never accepts a tool call or an approval object from a client.
- Any protocol that sends approval objects from a client (AG-UI) must be bound server-side the same way.