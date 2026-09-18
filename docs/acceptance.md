# First end-to-end support acceptance

This acceptance proves the first complete support path. It is deliberately
limited to the implemented slice and is not a claim that every MVP capability
or production concern is complete.

## Automated acceptance

Run Docker, restore the toolchains described in
[`operations.md`](operations.md), and execute:

```sh
dotnet test tests/Helpaffe.IntegrationTests \
  --filter FullyQualifiedName~CliWorkflowTests
dotnet test tests/Helpaffe.IntegrationTests \
  --filter FullyQualifiedName~TicketConcurrencyTests
dotnet test tests/Helpaffe.IntegrationTests \
  --filter FullyQualifiedName~NotificationDeliveryTests
npm --prefix src/web test -- --run
```

`CliWorkflowTests` starts a real Kestrel process and PostgreSQL container,
builds the actual Go CLI, and runs one coherent scenario:

1. Create two projects, two independent product keys, two SMTP configurations,
   and their fixed English templates.
2. Use two `HelpaffeProductClient` instances with the same external user id.
   Create two tickets in the first project and one in the second; the primary
   ticket includes technical context.
3. Use a named agent owned by a support user who can access only the first
   project. Combined CLI search filters return only its two tickets, requester
   history contains only the related first-project ticket, and reading the
   second-project ticket returns not found.
4. Acquire the primary ticket through the CLI, add an internal note with a
   private attachment, reply publicly with a customer-visible attachment, and
   move it to Waiting for customer. Download the private file through the CLI,
   reject an overwrite, and prove that the Product API exposes only the public
   file.
5. Reply through the SDK. The ticket reopens. Repeat the identical SDK request
   with the same idempotency key, representing an unknown response after a
   connection break; the version and conversation remain unchanged.
6. Send the final CLI reply and resolve the ticket. Read the same complete
   detail used by the Web UI and verify context, assignee, conversation,
   requester history, and five `submitted_to_smtp` deliveries.
7. Capture all nine SMTP messages and prove that customer, project-support,
   and assigned-support recipients remain distinct across both projects.

`TicketConcurrencyTests` proves that two parallel agents acquire different
tickets in priority/wait order and that replaying an acquisition is
idempotent. `NotificationDeliveryTests` proves persistence across an application
restart, both assigned and unassigned recipient routing, SMTP outage retries at
1, 5, and 30 minutes, terminal failure visibility, and manual retry without a
new ticket reply. The Web tests prove that combined search filters, technical
context, requester-history navigation, delivery retry, attachment selection,
multipart upload, visibility labels, safe downloads, local limits, loading
state, and draft/file preservation across stale conflicts are rendered and
call the same backoffice contract.

## Operator walkthrough

For a human review, start the Compose stack and sign in as the bootstrap
administrator. Create two projects in Administration. For each project:

- create a separate Product API key;
- configure its SMTP server, sender, support recipient, branding, and customer
  and backoffice ticket links under Email;
- review the five effective English templates and send a test email; and
- grant a support user access to only the first project, then create a named
  agent for that user.

Store the two product keys in their respective backend secret stores. Run the
[example product backend](../examples/Helpaffe.ProductExample/README.md) or use
the [.NET SDK](../src/Helpaffe.Sdk/README.md) to create a ticket with `context`.
Configure the support agent without placing its token in command arguments:

```sh
export HELPAFFE_URL=https://support.example.test
export HELPAFFE_TOKEN=hfa_replace-from-secret-store
helpaffe status
helpaffe me
helpaffe ticket list --project PROJECT_UUID --status open --search settings
helpaffe ticket next --project PROJECT_UUID
helpaffe ticket get HLP-NUMBER
helpaffe ticket reply HLP-NUMBER --version 2 \
  --status waiting_for_customer --message-file reply.txt --file screenshot.png
```

Reply from the product with the last-read version. Confirm that its status is
Open, then send a final CLI reply with `--status resolved`. In the Web ticket,
review the assignee, public and internal history, attachment names, media types,
sizes and visibility labels, technical context, other requester tickets, and
email delivery states. Add one public reply attachment and one internal-note
attachment, download both, and confirm a stale conflict keeps the message draft
and selected files. Use the second product with the same external user id and
confirm it never appears in first-project search or requester history.

For a controlled SMTP outage, point a non-production project at an unavailable
test SMTP port, create a notification-producing event, and observe `pending`
then `failed`. Restore the SMTP setting and use **Retry delivery** or:

```sh
helpaffe ticket notification retry HLP-NUMBER NOTIFICATION_UUID
```

Do not create another public reply merely to retry email. A successfully
submitted retry changes only the delivery record; the ticket version and
conversation remain unchanged.
