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

## Why 403, not 401

401 means "I don't know who you are / your credentials are missing or invalid." 403
means "I know who you're asking as, but this request is not allowed." The network
access check on these resources (`publicNetworkAccess: Disabled`) happens independently
of — and before — token validation: it's a property of *where the request came from*,
not *who it claims to be*.

So when the request lands over the public internet instead of through the private
endpoint:

- The TLS handshake still succeeds (the resource still has a valid public listener/cert
  even with `publicNetworkAccess: Disabled` — that setting blocks *access*, not the
  listener itself).
- The `Authorization: Bearer <token>` header is still sent and would still be valid.
- But the resource's network policy layer sees the request arriving from a
  non-approved path (public internet, not the private-link connection) and rejects it
  outright — it never gets far enough to actually check the token.

That's also why the response comes back in ~1-2ms (`elapsed-time: 1`/`2` in the error
headers) — it's a fast network-policy rejection, not a real backend call. It's also why
the curl repro from Kudu, using a real, valid managed-identity token, still proved
useful: when it hit Document Intelligence over the *correctly resolved private path* it
got `200 OK` with the same token that would 403 over a public path. The token was never
the problem — the network path was.

## Step-by-step: how the DNS lookup actually resolves in this case

What *should* happen (and does, for the hostnames marked OK in the table above):

- The app calls, e.g., `https://cor-ais-cap-dev-we-001.cognitiveservices.azure.com/...`
- The Function App's OS resolver sends a DNS query for that hostname.
- Because the VNet's configured DNS server is `10.240.0.68` (not Azure's default
  `168.63.129.16`), the query goes to that custom resolver first — this is a hub-VNet
  resolver/firewall, not something living in the spoke `cor-vnet-cap-dev-we-001`.
- That hub resolver forwards the query on to Azure's internal DNS service
  (`168.63.129.16`) — but it does so *from the hub VNet's own network context*, since
  that's the VNet the forwarding resolver actually lives in.
- Azure's `168.63.129.16`, asked from the hub VNet, checks which private DNS zones are
  linked *to the hub VNet* — that's the deciding step. It has no notion of "the spoke
  asked on the app's behalf"; all it sees is a query originating from the hub.
- `privatelink.cognitiveservices.azure.com` **is** linked to the hub VNet, so it returns
  the private A record: `10.243.4.10`.
- The Function App connects to `10.243.4.10` over the private endpoint. Azure Cognitive
  Services sees the request arrive via the approved private-link path, checks RBAC on
  the bearer token, and returns `200 OK`.

What *actually* happens for `search.windows.net` / `blob.core.windows.net` /
`file.core.windows.net` / `openai.azure.com` (the broken ones):

- Same first three steps: app calls the hostname, OS resolver queries `10.240.0.68`,
  the hub resolver forwards to `168.63.129.16` from the hub VNet's context.
- Azure's `168.63.129.16` checks the hub VNet's zone links for, e.g.,
  `privatelink.search.windows.net` — and finds **no link** (it's only linked to the
  spoke `cor-vnet-cap-dev-we-001`, which is irrelevant here since the query isn't
  coming from the spoke's context).
- With no linked private zone to answer from, resolution falls through to the zone's
  normal public DNS record — a CNAME to the resource's actual public cluster hostname
  (e.g. `cor-srch-cap-dev-we-001.search.windows.net` → `azszeft.westeurope.cloudapp.azure.com`).
- That public hostname resolves to the service's real public IP
  (`9.163.137.70` in this case).
- The hub resolver hands that public IP back to the Function App, which connects to it
  over the internet instead of the private endpoint.
- TLS still succeeds, the bearer token is still sent — but Azure AI Search sees the
  request arrive over the public path while `publicNetworkAccess: Disabled` is set, and
  rejects it with `403` before auth is ever evaluated.

The fix (linking the missing zones to the hub VNet) changes nothing about the app code
or RBAC — it only changes what `168.63.129.16` returns in the fourth bullet of the
second list, which is enough to make the whole chain follow the first (working) path
instead.

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
