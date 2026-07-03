# ---------------------------------------------------------------------------
# Existing landing zone resources, referenced read-only via data sources.
# Nothing in this file is created or modified by this Terraform config.
# ---------------------------------------------------------------------------

# --- Networking (owned by the platform/network team) ------------------------

data "azurerm_resource_group" "network" {
  name = "cor-cap-network-${local.env}-${local.region}-${local.instance}"
}

data "azurerm_virtual_network" "main" {
  name                = "cor-vnet-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

data "azurerm_subnet" "pe" {
  name                 = "cor-snet-cap-pe-${local.instance}"
  virtual_network_name = data.azurerm_virtual_network.main.name
  resource_group_name  = data.azurerm_resource_group.network.name
}

data "azurerm_network_security_group" "pe" {
  name                = "cor-nsg-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

data "azurerm_route_table" "spoke" {
  name                = "cor-rt-spoke-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

# --- Foundry / AI -------------------------------------------------------------

data "azurerm_resource_group" "ai" {
  name = "cor-cap-ai-${local.env}-${local.region}-${local.instance}"
}

data "azurerm_cognitive_account" "foundry" {
  name                = "cor-ais-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
}

data "azurerm_application_insights" "foundry" {
  name                = "cor-appi-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
}

# No data source exists for azurerm_private_endpoint - it's resource-only in
# this provider. The PE's NIC is readable directly since its name is
# deterministic ("<private endpoint name>_nic").
data "azurerm_network_interface" "ai_services_pe" {
  name                = "cor-pep-ais-cap-${local.env}-${local.region}-${local.instance}_nic"
  resource_group_name = data.azurerm_resource_group.ai.name
}

# --- Data tier ------------------------------------------------------------

data "azurerm_resource_group" "data" {
  name = "cor-cap-data-${local.env}-${local.region}-${local.instance}"
}

# --- Private DNS zones (hub, owned by platform team) -----------------------
# Confirmed via the diagnostic step in 1-infra-deploy.yml (subscription
# cor-connectivity-prd, RG cor-connectivity-dns-prd-we-001). This is the
# *complete* zone list in that RG - there is no queue/table/search.windows.net
# zone at all, so stfunc_queue/stfunc_table/search private endpoints have no
# zone to attach to yet (a bigger ask than the others - the zone itself would
# need to be created, not just linked). None of these 7 zones has a virtual
# network link to our VNet (cor-vnet-cap-dev-we-001) either - see
# docs/platform-team-dns-verzoek.md. Adding these data sources doesn't fix
# DNS by itself; it gets the zone IDs into code for when the VNet link and
# private_dns_zone_group wiring happen.

data "azurerm_private_dns_zone" "azurewebsites" {
  provider            = azurerm.hub
  name                = "privatelink.azurewebsites.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "blob" {
  provider            = azurerm.hub
  name                = "privatelink.blob.core.windows.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "file" {
  provider            = azurerm.hub
  name                = "privatelink.file.core.windows.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "vaultcore" {
  provider            = azurerm.hub
  name                = "privatelink.vaultcore.azure.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

# Not currently consumed by any private endpoint in this repo (the Foundry
# account is referenced via data.azurerm_cognitive_account.foundry, managed
# elsewhere) - captured here since the diagnostic surfaced them, in case
# they're needed later.
data "azurerm_private_dns_zone" "cognitiveservices" {
  provider            = azurerm.hub
  name                = "privatelink.cognitiveservices.azure.com"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "openai" {
  provider            = azurerm.hub
  name                = "privatelink.openai.azure.com"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "services_ai" {
  provider            = azurerm.hub
  name                = "privatelink.services.ai.azure.com"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}
