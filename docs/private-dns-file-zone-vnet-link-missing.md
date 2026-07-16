# Function App deploy failing: `privatelink.file.core.windows.net` not linked to our VNet


rivatelink.file.core.windows.net is the DNS zone for Azure Files (the file service on a storage account) — it's not scoped to Function Apps at all. The Function App's own DNS zone is privatelink.azurewebsites.net, and that one's fine (confirmed Completed link).

Where the Function App comes in: corstfunccapdevwe is a storage account, but it isn't just any storage account — it's the one backing the Function App's EP1 content share. Elastic Premium plans need a persistent file share (WEBSITE_CONTENTSHARE) to store the deployed app content, and Azure Files has no managed-identity auth path, so it's reached over WEBSITE_CONTENTAZUREFILECONNECTIONSTRING + WEBSITE_CONTENTOVERVNET=1 — see infra/function_app.tf:37-42:

# storage_uses_managed_identity only covers AzureWebJobsStorage/Durable
# Functions (blob/queue/table). The EP1 plan's content share still needs a
# key-based connection string - Azure Files/SMB has no managed-identity
# auth path - plus WEBSITE_CONTENTOVERVNET so the platform reaches it via
# the private endpoint (azurerm_private_endpoint.stfunc_file in storage.tf)
# instead of the public endpoint.

So the chain is: Kudu (running inside the Function App) → needs to mount its deployments directory on the content share → content share lives on corstfunccapdevwe's File service → that's reached at corstfunccapdevwe.file.core.windows.net → which only resolves to a private IP if privatelink.file.core.windows.net is linked to the VNet Kudu's DNS queries originate from. That's the missing link. The Function App doesn't consume that zone as "its own" identity zone (that's azurewebsites) — it depends on it transitively, through the storage account it uses for content storage.

## Summary

The `4-deploy-application.yml` zip-deploy to `cor-func-idx-cap-dev-we-001` fails with a
500 from Kudu (`IOException: The specified network name is no longer available`) while
creating its deployments directory on the EP1 content share. This is a recurrence of the
issue in [`platform-team-dns-verzoek.md`](./platform-team-dns-verzoek.md), narrowed down
to one specific zone: `privatelink.file.core.windows.net` still isn't linked to
`cor-vnet-cap-dev-we-001`, even though the other zones that doc flagged (search, queue,
table) were remediated.

## Evidence (pipeline run, 2026-07-15)

The `DIAGNOSTIC: full Function App + networking state` step in the pipeline confirms:

- The private endpoint `cor-pep-stfunc-file-cap-dev-we-001` has a DNS zone group
  attached, and `privatelink.file.core.windows.net` does have an A record for
  `corstfunccapdevwe` → `10.243.4.7`. The endpoint and the record both look healthy in
  isolation.
- `az network private-dns link vnet list` for `privatelink.file.core.windows.net`
  against `cor-vnet-cap-dev-we-001` returns **empty** — no VNet link.
- The same check for `privatelink.azurewebsites.net` returns a link in state
  `Completed`. That zone resolves fine; the Function App's own site DNS
  (`cor-func-idx-cap-dev-we-001.privatelink.azurewebsites.net`) works.
- `corstfunccapdevwe`'s storage account has `publicNetworkAccess: Disabled`, so there's
  no fallback path once private resolution fails.

An A record existing in a zone doesn't help if the zone isn't linked to the VNet the
query originates from — the VNet's resolver never consults it. The VNet's custom DNS
server (`10.240.0.68`, the hub firewall's DNS proxy) doesn't provide an alternate path
here either: per `platform-team-dns-verzoek.md`, `file` was the one zone in
`cor-connectivity-dns-prd-we-001` linked *only* to `cor-vnet-workplace-prd-we-001` — it
never got the "core three" VNet links (`connectivity`/`management`/`IAAS-VNET`) that
every other zone in that resource group has. So there's no path, direct or via the
firewall, that resolves `corstfunccapdevwe.file.core.windows.net` to its private IP.
Resolution fails, Kudu can't mount its content share, and it crashes on startup with the
stack trace below.

```
[IOException: The specified network name is no longer available.
   at System.IO.Directory.CreateDirectory(String path)
   at Kudu.Core.Infrastructure.FileSystemHelpers.EnsureDirectoryIgnoreAccessExceptions(String path)
   at Kudu.Core.Environment.get_DeploymentsPath()
   at Kudu.Services.Web.App_Start.NinjectServices.GetSettingsPath(IEnvironment environment)
```

## Root cause

`privatelink.file.core.windows.net` (hub subscription `cor-connectivity-prd`, resource
group `cor-connectivity-dns-prd-we-001`) has no virtual network link to
`cor-vnet-cap-dev-we-001` (resource group `cor-cap-network-dev-we-001`). Everything else
in the chain — private endpoint, DNS zone group, A record, storage account network
rules — is correctly configured.

## Remediation options

1. **Ask the platform team to link the zone**, same as the original request in
   `platform-team-dns-verzoek.md`. Straightforward, but was apparently missed or
   deprioritized last time since it's the one zone outside the "core three" pattern.
2. **Self-service via Terraform.** `infra/providers.tf` notes the deploy SP already has
   *Private DNS Zone Contributor* on these zones (confirmed by the platform team,
   2026-07-07) — that's how `private_dns_zone_group` blocks on our private endpoints
   create their A records without waiting on the platform team. That role also grants
   `Microsoft.Network/privateDnsZones/virtualNetworkLinks/write`, so an
   `azurerm_private_dns_zone_virtual_network_link` resource (using the `azurerm.hub`
   provider alias, targeting `data.azurerm_private_dns_zone.file` and
   `data.azurerm_virtual_network.main`) should be able to create the missing link
   directly. Additive only — doesn't touch the existing link to
   `cor-vnet-workplace-prd-we-001`.

Not yet applied either way — pending a decision on which path to take.

## Update 2026-07-16: platform team explanation changes the picture

Platform team's response (paraphrased): all spoke VNets point to the hub firewall, and the
firewall forwards to Cordaan's domain controllers; the DCs hold `file.core.windows.net`
(the base zone, **not** `privatelink.file.core.windows.net`) as an authoritative zone
on-prem.

That's a different, more fundamental blocker than "our VNet isn't linked to the privatelink
zone." The normal resolution chain for a private-linked storage FQDN is:

1. Client resolves `corstfunccapdevwe.file.core.windows.net`.
2. That query has to reach Azure public DNS, which returns a CNAME to
   `corstfunccapdevwe.privatelink.file.core.windows.net`.
3. The privatelink zone (which already has the correct record —
   `corstfunccapdevwe.privatelink.file.core.windows.net` → `10.243.4.7`, confirmed above)
   resolves the private IP.

Because the DC is authoritative for the *base* `file.core.windows.net` zone, step 1 never
leaves the on-prem DNS estate — the DC answers (or NXDOMAINs) locally and never forwards to
Azure DNS. Steps 2–3 never run. This holds true **regardless of whether
`cor-vnet-cap-dev-we-001` gets linked to `privatelink.file.core.windows.net`**, since the
VNet uses the firewall (`10.240.0.68`) as its DNS server, not Azure-provided DNS directly —
so remediation option 2 above (self-service Terraform VNet link) is likely necessary but
**not sufficient** on its own.

### Revised recommended action

Ask the platform team to add a static A record directly inside their authoritative
`file.core.windows.net` DC zone:

```
corstfunccapdevwe.file.core.windows.net  A  10.243.4.7
```

This is the IP already published in `privatelink.file.core.windows.net`'s own record set, so
no new discovery work is needed on their end — it just needs to be mirrored into the zone
that's actually in the resolution path. This sidesteps the CNAME hop entirely and doesn't
depend on the firewall ever reaching Azure DNS for this suffix. The Terraform VNet-link
option can still be pursued in parallel for correctness/future private endpoints, but
shouldn't be assumed to unblock this deploy by itself.

### Why this isn't self-service

`file.core.windows.net` (no `privatelink.` prefix) isn't an Azure Private DNS Zone at all —
it's an Active Directory–integrated DNS zone hosted on Cordaan's domain controllers. That's
a completely different admin plane:

- No Azure resource ID, no RBAC role applies to it — it's managed via AD DNS (DNS Manager /
  `DnsServer` PowerShell module against a DC), which requires either being a domain admin or
  having a delegated ACE on that specific AD DNS zone.
- Our OIDC service principal authenticates to Azure Resource Manager. It has no domain
  identity and no path into AD DNS management — Azure RBAC grants don't reach into
  on-prem/AD-integrated infrastructure at all.
- Even `providers.tf`'s `hub` alias — which does have write access to the Azure-side zones —
  is explicitly scoped to ARM (`azurerm` provider), which can't touch a DC's local DNS zone
  either.

This one has to go back to the platform team; only someone with AD DNS admin rights on those
domain controllers can add the record.
