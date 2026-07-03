# Verzoek aan platform team: private DNS zone group ontbreekt op alle private endpoints

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

Geen van de private endpoints in `cor-cap-data-dev-we-001` heeft een private DNS zone
group gekoppeld — gecheckt via de ARM API, niet alleen via de statische `customDnsConfigs`
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

We hebben in `cor-cap-dev` en `cor-cap-prd` geen private DNS zones kunnen vinden - die
zitten in een subscription die wij zelf niet kunnen inzien. Via de deploy-identity
(service principal achter de OIDC service connection) konden we ze wel vinden: subscription
`cor-connectivity-prd` (`c8e46005-ce0e-4be5-9ded-0178e19fbe28`), resource group
`cor-connectivity-dns-prd-we-001`. Die identity heeft daar blijkbaar al Reader-achtige
rechten (kon zones + vnet-links listen), maar wij persoonlijk niet.

Twee bevindingen die de scope van dit verzoek groter maken dan we dachten:

1. **Geen van de 7 zones in die RG is gelinkt aan onze VNet** (`cor-vnet-cap-dev-we-001`,
   resource group `cor-cap-network-dev-we-001`). Elke zone heeft wel een vaste kern van
   3 andere VNets gelinkt (`cor-vnet-connectivity-prd-we-001`,
   `cor-vnet-management-prd-we-001`, `IAAS-VNET`), plus soms extra (bv. `blob` ook aan
   `cor-vnet-ccc-dev-we-001` en `cor-vnet-data-prd-we-001`; `file` juist alléén aan
   `cor-vnet-workplace-prd-we-001`). Onze VNet staat nergens tussen. Zonder vnet-link
   helpt een zone group op onze private endpoints niet - de VNet moet sowieso gelinkt
   worden aan elke zone die we nodig hebben.
2. **Er bestaat geen `privatelink.queue.core.windows.net`, `privatelink.table.core.windows.net`
   of `privatelink.search.windows.net` zone in die RG.** Voor `cor-pep-stfunc-queue-*`,
   `cor-pep-stfunc-table-*` en `cor-pep-srch-*` is dit dus geen kwestie van linken, maar
   van de zone zelf eerst aanmaken.

Volledige zone-lijst in `cor-connectivity-dns-prd-we-001` op dit moment: `privatelink.
azurewebsites.net`, `privatelink.blob.core.windows.net`,
`privatelink.cognitiveservices.azure.com`, `privatelink.file.core.windows.net`,
`privatelink.openai.azure.com`, `privatelink.services.ai.azure.com`,
`privatelink.vaultcore.azure.net`.

## Gevraagde actie

We hebben **alle** private endpoints in deze resource group nagelopen (niet alleen de
blokkerende) — geen enkele heeft een private DNS zone group gekoppeld
(`az network private-endpoint dns-zone-group list` geeft voor elke hieronder `[]` terug).
Kunnen jullie deze allemaal controleren en remediëren tegen de juiste
`privatelink.<subresource>.<service>` zone, gelinkt aan VNet `cor-vnet-cap-dev-we-001`
(resource group `cor-cap-network-dev-we-001`)?

| Private endpoint | Target resource | Subresource | Zone group aanwezig? | Resource ID |
|---|---|---|---|---|
| `cor-pep-stfunc-file-cap-dev-we-001` | `corstfunccapdevwe` (storage) | file | Nee | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stfunc-file-cap-dev-we-001` |
| `cor-pep-stfunc-blob-cap-dev-we-001` | `corstfunccapdevwe` (storage) | blob | Nee | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stfunc-blob-cap-dev-we-001` |
| `cor-pep-stfunc-queue-cap-dev-we-001` | `corstfunccapdevwe` (storage) | queue | Nee | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stfunc-queue-cap-dev-we-001` |
| `cor-pep-stfunc-table-cap-dev-we-001` | `corstfunccapdevwe` (storage) | table | Nee | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stfunc-table-cap-dev-we-001` |
| `cor-pep-stdata-cap-dev-we-001` | `corstdatacapdevwe` (storage) | blob | Nee | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-stdata-cap-dev-we-001` |
| `cor-pep-func-cap-dev-we-001` | `cor-func-idx-cap-dev-we-001` (Function App) | sites | Nee | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-func-cap-dev-we-001` |
| `cor-pep-srch-cap-dev-we-001` | `cor-srch-cap-dev-we-001` (AI Search) | searchService | Nee | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-srch-cap-dev-we-001` |
| `cor-pep-kv-cap-dev-we-001` | Key Vault (`azurerm_key_vault.main`) | vault | Nee | `/subscriptions/b61f3453-5d67-4125-b9dd-ff5458c590bf/resourceGroups/cor-cap-data-dev-we-001/providers/Microsoft.Network/privateEndpoints/cor-pep-kv-cap-dev-we-001` |

`file` op `corstfunccapdevwe` is het endpoint dat nu concreet de deploy blokkeert (zie
hierboven), de rest is nog niet end-to-end getest vanuit de Function App maar heeft
dezelfde ontbrekende koppeling — graag in één keer meenemen zodat we niet steeds opnieuw
tegen deze fout aanlopen zodra er weer een pad wordt aangesproken.

Let op: dit is de lijst van vandaag. Er komen op korte termijn nog private endpoints bij
(o.a. voor een App Service, zie de uitgecommentarieerde `azurerm_private_endpoint.api` in
`app_service.tf`), dus we verwachten dat dit vaker terugkomt zolang de policy-koppeling
niet automatisch/betrouwbaar loopt. Fijn als jullie ook kunnen aangeven of dit normaal
gesproken vanzelf (met vertraging) gebeurt via policy-remediation, of dat het altijd een
handmatige stap aan jullie kant is na het aanmaken van een nieuw private endpoint - dan
weten wij wanneer we moeten wachten versus escaleren.

Laat het weten als jullie iets anders nodig hebben (bv. toegang tot de subscription/RG
waar de zones staan) om dit te verifiëren.
