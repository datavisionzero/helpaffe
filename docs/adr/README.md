# Architecture Decisions

This directory records decisions that are difficult to reverse or surprising
without their context. Product scope belongs in [`VISION.md`](../../VISION.md).

## Decisions

- [0001 – The foundation is adapted from planaffe and hostingaffe](./0001-the-foundation-is-adapted-from-planaffe-and-hostingaffe.md)

## Adopted decisions

The following planaffe decisions also govern helpaffe. They are referenced
rather than copied so their rationale has one canonical home:

| Decision | What helpaffe adopts |
| --- | --- |
| [0002](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0002-the-backend-is-four-layers-not-one-project.md) | Four inward-pointing .NET layers |
| [0003](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0003-the-cli-is-go-not-a-second-dotnet-binary.md) | A standalone Go CLI |
| [0004](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0004-the-frontend-is-react-not-blazor.md) | React on its own toolchain |
| [0005](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0005-the-contract-is-checked-in-and-both-clients-are-generated-from-it.md) | Checked-in OpenAPI contracts and generated clients |
| [0006](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0006-the-web-application-is-a-shell-before-it-is-a-screen.md) | A persistent application shell |
| [0007](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0007-markdown-is-rendered-in-the-browser-and-never-as-html.md) | Markdown stored as source and rendered in the browser |
| [0010](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0010-the-product-speaks-english-and-only-english.md) | English-only product text |
| [0011](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0011-the-api-carries-no-version-and-migrations-only-run-forward.md) | An unversioned API and forward-only database migrations |
| [0017](https://github.com/datavisionzero/planaffe/blob/main/docs/adr/0017-the-web-application-is-drawn-by-tailwind-and-base-ui-components-the-repository-owns.md) | Tailwind and owned Base UI components |

Authentication, authorization, and deletion rules are helpaffe decisions. The
similar planaffe rules are not adopted because helpaffe has fixed support roles,
project-scoped agents, product credentials, and human-only administration.
