# Support Context

helpaffe is the shared support workspace in which people and their agents handle requests from the users of several connected products.

## Projects and people

**Project**:
The organizational boundary for one connected product. Every ticket, requester, product credential, and product-facing operation belongs to exactly one project.
_Avoid_: Product, tenant, workspace

**Requester**:
An end user identified by a stable external user ID within one project, with a current display name and email address.
_Avoid_: Customer account, helpaffe user

**Requester Ticket History**:
The other tickets whose requester has the same stable external user ID within the same project. It is support context derived from a visible ticket, not a cross-project customer record.
_Avoid_: Customer profile, CRM record, global requester history

**Support User**:
A human helpaffe user who may be assigned tickets and remains responsible for work performed by their agents.
_Avoid_: Agent, requester

**Acting Agent**:
The named agent credential that performed an action for a support user. It supplements the responsible human identity and never replaces it.
_Avoid_: Assignee, user token

**Support Instructions**:
The single project-specific Markdown document that gives support users and their agents product context, important links, known constraints, and communication guidance.
_Avoid_: Knowledge base, customer-facing documentation

## Tickets

**Ticket**:
A single support request in one project, identified instance-wide by a short ticket number and carrying its requester, subject, status, priority, assignment, conversation, and timestamps.
_Avoid_: Case, issue, task

**Ticket Number**:
The short, instance-wide unique reference used by people, agents, and product integrations to address a ticket.
_Avoid_: Database ID, project-local number

**Assignee**:
The optional support user responsible for a ticket. Work performed by one of that user's agents does not change the assignee to the agent.
_Avoid_: Acting agent, owner

**Status**:
Exactly one of Open, In Progress, Waiting for Customer, or Resolved, representing where the ticket is in the fixed support workflow.
_Avoid_: Closed, blocked, custom status

**Priority**:
Either Normal or Urgent. New tickets have Normal priority unless Urgent is explicitly chosen.
_Avoid_: Severity, rank, custom priority

**Ticket Version**:
A positive counter representing the complete current ticket state. One atomic ticket change advances it exactly once, regardless of how many fields or conversation entries that change contains.
_Avoid_: Conversation sequence, release version

**Waiting Since**:
The instant an Open ticket most recently began waiting for support, used after priority to order next-ticket acquisition. Further customer messages while already Open do not reset it.
_Avoid_: Updated at, last customer reply

## Conversation

**Conversation Entry**:
An immutable chronological record on a ticket, classified as a Customer Message, Public Reply, Internal Note, or System Event.
_Avoid_: Editable comment, attachment

**Customer Message**:
A public conversation entry authored by the requester through the connected product.
_Avoid_: Internal note, email delivery

**Public Reply**:
A public conversation entry authored by a support user or their acting agent for the requester.
_Avoid_: Draft, internal note

**Internal Note**:
A support-only conversation entry authored by a support user or their acting agent and never exposed through the product surface.
_Avoid_: Public reply, customer message

**System Event**:
A support-only conversation entry that records a meaningful ticket change rather than authored message content.
_Avoid_: Audit log, public reply
