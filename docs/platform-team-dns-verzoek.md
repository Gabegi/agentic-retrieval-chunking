# Verzoek aan platform team: private DNS zone group ontbreekt op storage private endpoints

Context: deploy van de Function App (`cor-func-idx-cap-dev-we-001`, resource group
`cor-cap-data-dev-we-001`, subscription `cor-cap-dev` /
`b61f3453-5d67-4125-b9dd-ff5458c590bf`) faalt bij het zip-deployen naar de SCM/Kudu-site.

## Probleem

Kudu crasht bij het opstarten met een `WinIOError` op `Directory.CreateDirectory` (zie
stack trace hieronder), tijdens het aanmaken van zijn deployments-directory op de EP1
content share. Die content share draait via `WEBSITE_CONTENTAZUREFILECONNECTIONSTRING` /
`WEBSITE_CONTENTOVERVNET=1` op storage account `corstfunccapdevwe`, en moet dus via het
private endpoint benaderd worden.

```
at Kudu.Core.Infrastructure.FileSystemHelpers.EnsureDirectoryIgnoreAccessExceptions(String path)
at Kudu.Core.Environment.get_DeploymentsPath()
at Kudu.Services.Web.App_Start.NinjectServices.GetSettingsPath(IEnvironment environment)
...
[InvalidOperationException]: The pre-application start initialization method Run on type
WebActivatorEx.ActivationManager threw an exception with the following error message:
Exception has been thrown by the target of an invocation.
```

## Root cause (bevestigd)

Geen van de private endpoints op `corstfunccapdevwe` heeft een private DNS zone group
gekoppeld — gecheckt via de ARM API, niet alleen via de statische `customDnsConfigs`
snapshot:

```bash
az network private-endpoint dns-zone-group list \
  -g cor-cap-data-dev-we-001 --endpoint-name cor-pep-stfunc-file-cap-dev-we-001
# -> []

az network private-endpoint dns-zone-group list \
  -g cor-cap-data-dev-we-001 --endpoint-name cor-pep-stfunc-blob-cap-dev-we-001
# -> []
```

Zonder zone group bestaat er geen A-record voor `corstfunccapdevwe.file.core.windows.net`
(en waarschijnlijk ook niet voor de blob-variant) in een `privatelink.*.core.windows.net`
zone die aan de VNet gekoppeld is. Resolutie valt dan terug op het publieke endpoint van
het storage account, en dat staat dicht (`public_network_access_enabled = false`) —
vandaar dat de content share niet mount en Kudu crasht.

We hebben in dit subscription (`cor-cap-dev`) en in `cor-cap-prd` geen private DNS zones
kunnen vinden (`az network private-dns zone list` geeft niets terug), dus die zones +
policy-koppeling zitten kennelijk in een subscription die wij niet kunnen inzien/beheren.

## Gevraagde actie

Kunnen jullie controleren en zo nodig remediëren dat er een private DNS zone group
gekoppeld is voor onderstaande private endpoints, tegen de juiste
`privatelink.<subresource>.core.windows.net` zone, gelinkt aan VNet
`cor-vnet-cap-dev-we-001` (resource group `cor-cap-network-dev-we-001`)?

| Private endpoint | Subresource | Resource ID |
|---|---|---|
| `cor-pep-stfunc-file-cap-dev-we-001` | file | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stfunc-file-cap-dev-we-001` |
| `cor-pep-stfunc-blob-cap-dev-we-001` | blob | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stfunc-blob-cap-dev-we-001` |
| `cor-pep-stfunc-queue-cap-dev-we-001` | queue | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stfunc-queue-cap-dev-we-001` |
| `cor-pep-stfunc-table-cap-dev-we-001` | table | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stfunc-table-cap-dev-we-001` |

`file` is het blokkerende endpoint (zie hierboven), maar `blob`/`queue`/`table` staan er
net zo bij en zijn nog niet end-to-end getest — graag in één keer meenemen.

Zelfde vraag geldt straks voor de overige private endpoints in deze resource group
(storage account `corstdatacapdevwe`, Function App zelf, Search, Key Vault) zodra we die
paden daadwerkelijk gebruiken - nu nog niet bevestigd stuk of heel, maar waarschijnlijk
hetzelfde probleem.

Laat het weten als jullie iets anders nodig hebben (bv. toegang tot de subscription/RG
waar de zones staan) om dit te verifiëren.
