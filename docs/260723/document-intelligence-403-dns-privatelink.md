# ExtractActivity 403 — private DNS zones not linked to the DNS-resolver VNet

## Symptom

`ExtractActivity` in `PdfIndexingFunction.cs` fails with:

```
System.InvalidOperationException: ExtractActivity failed: Service request failed.
Status: 403 (Forbidden)
```

This persisted even after the platform team linked `cor-vnet-cap-dev-we-001` (the
spoke VNet) to `privatelink.cognitiveservices.azure.com`, and after confirming RBAC
(`Cognitive Services User` on `cor-ais-cap-dev-we-001`) was correctly assigned to the
function app's managed identity.

## Root cause

`cor-vnet-cap-dev-we-001` does not use Azure's default DNS (`168.63.129.16` answering
from the spoke's own zone links) — it forwards to a custom DNS server at `10.240.0.68`
(a hub-VNet resolver/firewall). With centralized DNS like this, a private DNS zone
being linked to the *spoke* VNet is not sufficient: Azure's recursive resolver answers
privatelink queries based on the VNet the query *originates from*, which is the hub
where `10.240.0.68` lives. If a zone isn't *also* linked to that hub VNet, resolution
falls through to the zone's public record, the request goes out over the internet, and
the target resource (which has `publicNetworkAccess: Disabled`) correctly rejects it
with 403 rather than a connection error.

This was confirmed directly from the function app's Kudu console (`nslookup`), which
approximates the DNS path the running app instance actually uses:

| Hostname | Resolved to | Status |
|---|---|---|
| `cor-func-idx-cap-dev-we-001.azurewebsites.net` | `10.243.4.8` | OK (private) |
| `corstfunccapdevwe.file.core.windows.net` | public IP (`am5prdstrz28a.store.core.windows.net` cluster) | **broken** |
| `cor-ais-cap-dev-we-001.cognitiveservices.azure.com` | `10.243.4.10` | OK (private) |
| `cor-srch-cap-dev-we-001.search.windows.net` | public IP (`azszeft.westeurope.cloudapp.azure.com`) | **broken** |
| `corstdatacapdevwe.blob.core.windows.net` | public IP (`am5prdstrz28a.store.core.windows.net` cluster) | **broken** |

A curl call from Kudu using a real managed-identity token against
`https://cor-ais-cap-dev-we-001.cognitiveservices.azure.com/documentintelligence/info`
returned `200 OK`, proving Document Intelligence itself, RBAC, and the private endpoint
are all correctly configured. The 403 `ExtractActivity` still throws comes from earlier
in the same activity — `EnsureIndexAsync()` (Azure AI Search) and/or the PDF blob read
against `corstdatacapdevwe` — both of which resolve publicly per the table above.

## What "fixed" should look like

For `corstdatacapdevwe.blob.core.windows.net` and
`cor-srch-cap-dev-we-001.search.windows.net`, it should resolve to a `10.243.4.x`
address (the `pe` subnet range) — the same pattern as `10.243.4.8` (func site),
`10.243.4.7` (file share), and `10.243.4.10` (AI Services) that worked. Getting back a
public cluster IP instead is exactly the "falls through to public DNS" failure mode.

## Fix (platform team, `cor-connectivity-prd`)

Link the following private DNS zones to the hub VNet hosting the DNS resolver at
`10.240.0.68` (not just the spoke `cor-vnet-cap-dev-we-001`, which was already linked
for these two and made no difference):

- `privatelink.search.windows.net`
- `privatelink.blob.core.windows.net`

Same underlying gap independently confirmed for these zones (still open as of this
writeup):

- `privatelink.file.core.windows.net`
- `privatelink.openai.azure.com`

## Verification

Re-run the `nslookup` checks above from the function app's Kudu console
(`https://cor-func-idx-cap-dev-we-001.scm.azurewebsites.net/DebugConsole`) after the
platform team applies the link — all five hostnames should resolve to `10.243.4.x`.
Then retrigger the indexing orchestration and confirm `ExtractActivity` completes.
