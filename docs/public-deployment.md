# Public portfolio deployment preparation

Current decision: a strict $0 budget. The selected presentation is a GitHub Pages static showcase plus the locally runnable full application; see [free publishing](free-portfolio.md). The hosted-backend configuration below is retained as optional future work. No paid service is authorized or provisioned. Keep Stripe in test mode for a recruiter demo. A live merchant service is a separate scope with onboarding, real-money operational procedures and production email.

## Hosting configuration

The existing multi-stage Dockerfile builds a non-root .NET application serving the compiled React frontend. It needs PostgreSQL with schema plus migrations 001-005, a restricted application login, TLS termination and persistent private key storage.

Set these values in the host's secret/configuration manager:

| Setting | Value |
|---|---|
| ASPNETCORE_ENVIRONMENT | Production |
| Hosting__Enabled | true |
| Hosting__PublicOrigin | Exact HTTPS origin, e.g. https://demo.example.com |
| Hosting__KnownProxies | Comma-separated, verified proxy IP addresses; empty only for direct TLS |
| Hosting__DataProtectionPath | Absolute persistent directory outside the application, e.g. /var/lib/shelfsync/keys |
| ConnectionStrings__ShelfSync | Restricted database login with explicit per-instance Maximum Pool Size |
| Operator__Username / Operator__PasswordHash | Private recovery Admin credentials; never publish them |
| Stripe__Enabled | true only after sandbox configuration |
| Stripe__SecretKey / Stripe__WebhookSecret | Sandbox key and the deployed endpoint's signing secret, supplied privately |
| Stripe__BaseUrl | Same HTTPS public origin |

The app restricts allowed hosts to the configured hostname, uses one forwarded hop, trusts only configured proxy addresses and sets HSTS. With no proxy addresses it ignores forwarded headers. Do not enable ASPNETCORE_FORWARDEDHEADERS_ENABLED or DOTNET_FORWARDEDHEADERS_ENABLED: unrestricted trust is explicitly rejected in hosted mode. Verify the hosting platform's actual proxy chain before configuring this; do not guess a trust-all range just to make HTTPS work.

Data-protection keys must survive redeploys and be writable by the container's non-root application user. File persistence alone does not encrypt these keys. Use provider-protected encrypted storage with restrictive permissions or a configured certificate/key-management encryption provider; verify that choice on the selected host. Back up key material privately alongside the database/configuration recovery plan. Never serve or commit keys.

Official references: [ASP.NET proxy trust](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0), [Data Protection persistence and encryption](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

## Deployment sequence

1. Select provider, region and budget; review the concrete service configuration and cost before provisioning.
2. Use a dedicated fictional-data demo database, not the existing development store. Provision owner and runtime roles separately; apply migrations as owner and grant only the documented runtime permissions.
3. Configure private secrets, TLS, verified proxy trust and persistent protected keys. Deploy one instance initially and budget database connections for all services.
4. Confirm hostname filtering, HTTPS-only API access, secure cookies, CSRF, user-role boundaries and readiness on the actual host.
5. Configure a public Stripe sandbox webhook endpoint and its endpoint-specific signing secret. Perform a test payment and verify signature-validated event ingestion, paid-only picking and receipt display. A local CLI signing secret is not interchangeable with a new hosted endpoint secret.
6. Verify key persistence across a redeploy, backup/restore recovery and alerts for readiness failure, 5xx responses and payment reconciliation errors.
7. Run a modest authorized hosted smoke/load test, document results separately from local benchmarks, and record the five-minute walkthrough.
8. Add the verified URL and evidence to README/resume. Do not publish Admin credentials or claim deployment before these checks pass.

## Provider recommendation under consideration

Render supports Docker web services, managed PostgreSQL and persistent disks on paid services, making it a candidate for this stack. Costs and proxy/key-storage suitability must be checked for the actual chosen configuration; no free-tier or monthly-price promise is made. See [Render services](https://render.com/docs/service-types), [persistent disks](https://render.com/docs/disks), and [current pricing](https://render.com/pricing). Another existing host can be used if preferred.

Production email remains local preview in this build. A resume demo can state that limitation clearly; it must not imply it sends real recovery emails. Enabling real payments or email requires explicit provider configuration and separate end-to-end verification.
