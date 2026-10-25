# The $0 portfolio plan

## What gets published

The existing public repository `ShareefAldahhan/ShelfSync` contains the real application and its reproducible tests. `showcase/` is a dependency-free static project page intended for GitHub Pages. It includes actual application screenshots, an interactive explanation of the order/payment state model, benchmark evidence, and links to source and setup instructions.

The showcase explicitly identifies itself as a walkthrough. It does not run the backend, accept credentials, take payments or change inventory. The full .NET/PostgreSQL application remains locally runnable. No paid hosting, domain, database service or live-payment account is required.

## Preview

From the repository root:

```powershell
python -m http.server 5077 --bind 127.0.0.1 --directory showcase
```

Open http://127.0.0.1:5077. The files can also be served by any ordinary static web server. No installation or build step is required for the showcase.

## GitHub Pages

In the public repository's Settings > Pages, select GitHub Actions as the build/deployment source. The `Portfolio showcase` workflow publishes only `showcase/`; it cannot include backend secrets, database backups or ignored artifacts. It runs on showcase changes on main and supports manual dispatch. The intended URL is `https://shareefaldahhan.github.io/ShelfSync/`; treat it as live only after the deployment succeeds and the page is checked.

GitHub Pages supports static sites for public repositories on GitHub Free: [official documentation](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages). Do not switch to a paid plan or enable a paid resource to complete this setup.

## Resume use

Link the verified Pages URL as **Project walkthrough** and the repository as **Source code**. Do not label the showcase a production store or claim live customers. Use the [resume bullets and interview guide](portfolio.md) and the [benchmark methodology](benchmark-results-2026-10-25.md).

The recorded evidence includes 135 passing backend tests, 3 passing frontend tests, published API checks, migration replay, backup/restore and package-integrity checks. The previous local load run completed 274,265 requests with zero final-run errors. These counts describe recorded checks, not a production SLA.

## Visual checks

The showcase was inspected in a browser at a narrow mobile viewport and a 1280-pixel desktop breakpoint. Images loaded, desktop layout had no page-wide horizontal overflow, the payment-step control updated the explanation, and no browser console errors were captured. Screenshots are local artifacts; this does not claim an automated cross-browser test suite.
