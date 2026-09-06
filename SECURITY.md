# Security

BackBurner is designed for a trusted private network. It is not hardened for
direct Internet exposure. Keep the coordinator behind a firewall, enable API
authentication before crossing an untrusted network, and use TLS when traffic
leaves a trusted LAN.

Never commit API keys, NAS credentials, Plex tokens, machine-local path maps,
integration job control tokens, real media filenames, or coordinator state. Local worker configuration belongs
in an ignored `worker.local.json` file. Deployment secrets belong in the
operating system's secret or service configuration, outside a release.

If a credential is accidentally committed, revoke or rotate it immediately;
deleting it in a later commit is not sufficient. Do not open a public issue
containing a credential, private hostname, private address, or real media name.
Contact the repository owner through GitHub first to arrange a private report.
