# Security policy

## Supported versions

Only the latest tagged release is actively maintained. The WDC daemon
verifies the SHA-256 of every plugin zip against `wdc-catalog-api`
before loading, so users who download via the in-app marketplace are
protected against asset tampering even on older daemon builds.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security reports.**

Email: security@nks-hub.cz (preferred) or open a private security
advisory via GitHub's "Security" tab on this repository.

Include:

- A description of the vulnerability
- Steps to reproduce (proof of concept if possible)
- Expected impact
- Your suggested fix, if any

We aim to acknowledge within 72 hours and ship a fix within 14 days for
critical issues, 30 days for high severity, and best-effort for lower
severity findings. Coordinated disclosure is appreciated.

## Plugin author security guidance

- Validate every argument that crosses the endpoint boundary before
  passing it to a subprocess / shell / filesystem operation. The
  daemon's auth middleware authenticates the caller but does NOT
  sanitize semantic inputs.
- Prefer `CliWrap` argument arrays over string concatenation so
  untrusted substrings can never be interpreted as shell metachars.
- Never log secrets (tokens, passwords, API keys). The event bus is
  routed to the Electron UI — assume everything you `EmitLogLine` is
  visible to whoever has the desktop session.
- If your plugin fetches from the internet, verify SHA-256 of the
  downloaded artifact against a known-good manifest.

## Binary signing

Plugin DLLs ship **unsigned** at this time; SHA-256 on the catalog
entry is the trust anchor. Codesigning via sigstore / cosign is
tracked as a future milestone.
