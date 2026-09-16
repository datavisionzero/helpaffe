# Project email configuration

Email delivery is configured independently for each project by an
administrator or administrator agent. Support users cannot read or change this
configuration. The Web administration screen and the `project email` CLI
commands use the same backoffice operations documented in
[`backoffice.openapi.json`](api/backoffice.openapi.json).

## Settings

Each project stores:

- SMTP host, port, TLS switch, optional username, and an encrypted password;
- sender name and address;
- one or more project support recipient addresses;
- brand name, optional absolute logo URL, and a hexadecimal brand color;
- a customer ticket URL and a backoffice ticket URL, each containing
  `{{ticket_number}}`; and
- the fixed language value `en`.

The password can be set or replaced, but reads expose only
`password_configured`. Branding accepts a URL for an already hosted logo; there
is no upload or file-storage endpoint. Preview renders without sending. Test
delivery renders the selected template and submits one multipart text/HTML
message through the project's SMTP server.

## Fixed notification templates

| Type | Recipient and event |
| --- | --- |
| `new_ticket_customer` | Confirmation to the customer after ticket creation |
| `new_ticket_support` | Notification to project support recipients after ticket creation |
| `customer_reply_support` | Notification to the assignee, or project recipients when unassigned, after a customer reply |
| `public_reply_customer` | Notification to the customer after a public support reply |
| `assignment_support` | Notification to a newly assigned support user when someone else assigns the ticket |

Every type has a ready-to-use English subject, plain-text body, and HTML body.
A project may replace any of the three strings. There is no language switch,
translation catalogue, or programmable template logic.

## Variables

Variables use `{{variable_name}}`. Unknown names and malformed braces are
rejected when a template is saved. The fixed catalogue is:

| Variable | Value |
| --- | --- |
| `product_name` | Project/product name |
| `brand_name` | Configured email brand name |
| `brand_color` | Configured hexadecimal brand color |
| `brand_logo_url` | Configured logo URL, or an empty value |
| `customer_name` | Requester display name |
| `customer_email` | Requester email address |
| `ticket_number` | Instance-unique ticket number |
| `ticket_subject` | Ticket subject |
| `ticket_url` | Customer or backoffice link for the template's audience |
| `message` | Initial request, customer reply, or public support reply |
| `assignee_name` | Assigned support user's name |

Variable values are HTML-encoded in HTML bodies. Text bodies remain plain text,
and line breaks are removed from rendered subjects. SMTP submission is not a
claim that the recipient's mail system delivered the message.
