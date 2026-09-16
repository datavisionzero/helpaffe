# The Foundation Is Adapted from planaffe and hostingaffe

helpaffe adopts the proven technical shape of its sibling products: a four-layer
.NET backend, a React application, PostgreSQL, a standalone Go CLI, checked-in
OpenAPI contracts, generated clients, and one container image for the server and
web application. The implementation starts from the current local sibling
checkouts and renames and removes their product domain before introducing the
helpdesk domain; it does not create shared runtime packages between the three
products.

hostingaffe is the nearer operational template because it already places HTTP
under `/api`, includes human browser sessions and device login, and ships the
current Compose and release layout. planaffe remains the reference for atomic
work selection, cursor pagination, idempotent writes, and concurrency guards.
Their authorization models are not copied: helpaffe implements the fixed roles,
project access, agent scope, and separate product credentials required by its
own vision.

The copied foundation is a starting point, not a continuing dependency. This
keeps each product independently deployable and allows helpaffe's product and
backoffice boundaries to be enforced in its own contract and composition root.
The cost is that shared fixes must sometimes be applied to more than one
repository; that is preferable to coupling their releases before a third stable
consumer justifies extracting a common component.

The concrete layout and toolchain are in [`codebase.md`](../codebase.md). The
contract boundaries are in [`api.md`](../api.md), and the console contract is in
[`cli.md`](../cli.md).
