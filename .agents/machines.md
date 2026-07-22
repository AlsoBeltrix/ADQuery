# Machine Notes

## ASHBIAMWEB1 — 2026-07-21

- As of 2026-07-22, Claude Code CLI `2.1.217` is installed and exposes headless JSON output, explicit model selection, and `max` effort.
- On 2026-07-21, a bounded headless smoke test that omitted model selection and used `max` effort succeeded through the machine's configured Vertex integration. Explicitly overriding the model bypassed that integration route and failed at the local Portkey gateway, so review dispatches must leave model selection unset unless a newer smoke test proves the route changed.
- As of 2026-07-22, .NET SDK `10.0.302` is installed and satisfies P01's `10.0.300` feature-band pin with `latestPatch` roll-forward. SDKs `8.0.423` and `9.0.316` are also installed.
- As of 2026-07-22, the deployment host is x64 Windows Server 2022 with 32 GiB RAM and 8 logical processors. The `adquery_pool` application pool uses one x64 worker, has no configured private/virtual-memory recycle threshold, and has a 1,000-request IIS queue. The effective site request-filter limit was 10,485,760 bytes before P05.
- A 2026-07-22 read-only RootDSE/schema/query-policy check using the interactive identity read no user objects or attribute values. The one discovered query policy reported `MaxReceiveBuffer=10485760`, `MaxPageSize=1000000`, `MaxQueryDuration=120`, `MaxResultSetSize=262144`, and `MaxTempTableSize=10000`. The deployed schema reported `userPrincipalName` 1,024/indexed, `sAMAccountName` 256/indexed, `mail` 256/indexed, `displayName` 256/indexed, and `employeeID` 16/unindexed.
- Safe IIS inspection uses explicit fields with `%SystemRoot%\System32\inetsrv\appcmd.exe list apppool /name:adquery_pool /text:<field>` and `list config "Default Web Site/adquery" /section:requestFiltering /text:requestLimits.maxAllowedContentLength`. Never use `/text:*`; it can print protected credential configuration.
