# Portfolio and demo guide

## Project description

ShelfSync is an inventory reservation and fulfillment application built with ASP.NET Core, PostgreSQL, Npgsql and React. It coordinates online reservations and simulated store sales using transactional row locking, explicit order states, idempotent requests and an inventory audit ledger. Its local release includes operator authentication, protected mutation routes and an isolated verification runner with 42 passing integration tests.

## Three-minute walkthrough

| Time | Show and explain |
| --- | --- |
| 0:00–0:25 | Sign in. Explain one store, shelf stock, picked stock and logical reservation holds. |
| 0:25–1:00 | Search a stocked product, reserve one unit, show receipt and Orders entry. |
| 1:00–1:35 | Pick then fulfill. Explain the movement shelf to picked to fulfilled. |
| 1:35–2:00 | Reserve another unit, cancel it and filter for CANCELLED. |
| 2:00–2:25 | Record a POS sale and inspect the shelf count. Explain event-ID replay protection. |
| 2:25–3:00 | Show the verification command/result and explain one concurrency invariant and one limitation. |

Use fictional stocked products. Picking/fulfilling and POS sales consume stock; use a disposable test database or deliberately prepared demo stock for recording repeated takes. A reservation/cancellation demonstration preserves shelf stock. Do not reset the user's database to make a demo look clean.

A screenshot is included at `docs/images/orders.png`. A video recording is not included; this guide provides the recording script without claiming that a video was produced.

## Engineering discussion prompts

- Why lock an inventory row before deciding availability? Trace two concurrent attempts at the final unit.
- Why does picking change stock buckets while reservation does not?
- What if a commit succeeds but its HTTP response is lost?
- Why must replay payloads be compared, not only request keys?
- What happens if the expiry worker stops temporarily?
- Why use a dedicated test-owner database for trigger-based failure injection?
- Which invariants are enforced by SQL constraints, and which depend on transaction conventions?
- What does this software model fail to know about physical shoppers and shelf movement?

## Honest claims

Say: implemented and tested transactional reservations, lifecycle operations, POS deduplication, operator security and a local release pipeline. Say: 42 integration tests passed in the verified local environment. Explain that POS is a controlled simulator, not a live retail integration.

Do not claim production deployment, real customers, measured throughput/latency, zero possible overselling in all physical scenarios, cloud CI success, Docker execution, or complete automated browser coverage. No benchmark was run. Authentication is a single-operator model, not a customer identity platform.

## Repository presentation

Pin the repository on your profile if you choose. The README links to setup, architecture, API, release verification and this demo guide. The release package and checksum are generated under ignored artifacts; publish them as a release only after you explicitly choose to share them. Git commits are local unless pushed separately.

## Learning continuation

The implementation is ready for the requested beginner walkthrough. Suggested order: HTTP/JSON and frontend/backend boundaries; C# syntax and async; dependency injection; PostgreSQL schema/constraints; Npgsql queries; request-by-request reservation trace; transactions and locks; idempotency and failure recovery; lifecycle/expiry; testing strategy; authentication; deployment and tradeoffs.

Use the actual files listed in architecture.md and trace a real request before introducing alternatives. Work through one manageable lesson at a time with exercises and comprehension checks. The existing lesson-01.md is historical learning material; a comprehensive line-by-line course is a separate teaching phase, not claimed as completed documentation here.
