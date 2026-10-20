# Security policy

## Scope and threat model

NX Hub is a **LAN appliance**. It controls game consoles and talks to sys-botbase, which has no
authentication and can read and write console memory. The intended deployment is a trusted home
network.

- NX Hub requires a bearer token for its API by default.
- It must **not** be exposed to the internet. Use a VPN for remote access.
- sys-botbase's port (6000) should only be reachable from NX Hub's host.

## Reporting a vulnerability

Please report privately using GitHub's "Report a vulnerability" (Security Advisories) on this
repository rather than opening a public issue. Include the version or commit, a description, and
reproduction steps. We aim to acknowledge reports within 7 days.

## Supported versions

Pre-1.0: only the latest `main` receives fixes.
